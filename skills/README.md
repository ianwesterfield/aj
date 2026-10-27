# AJ Skills

Skills define how AJ handles specific tasks. AJ uses a **two-tier skill system**:

| Tier  | File         | Execution                      | Speed  | Use Case                       |
| ----- | ------------ | ------------------------------ | ------ | ------------------------------ |
| **1** | `skill.yaml` | Deterministic (no LLM)         | Fast   | Repeatable, well-defined tasks |
| **2** | `SKILL.md`   | LLM-guided (context injection) | Slower | Novel tasks, complex reasoning |

## How Skills Work

```
User Request
    ↓
[SkillExecutor.TryMatch()] ─── Pattern/keyword ≥0.5? ──→ Tier 1: Execute YAML workflow
    ↓ (no match)
[ReasoningEngine] ─── Inject top 3 SKILL.md files ──→ Tier 2: LLM generates tool calls
```

1. **Discovery**: Skills are loaded from `skills/` at startup (both `.yaml` and `.md`)
2. **Matching**: YAML skills are checked first via pattern/keyword scoring
3. **Tier 1**: If YAML match ≥0.5 confidence, execute workflow deterministically
4. **Tier 2**: If no YAML match, inject relevant SKILL.md files into LLM context

## Directory Structure

```
skills/
├── README.md                    # This file
└── postfix-spam-blocking/
    ├── skill.yaml               # Tier 1: Deterministic execution
    └── SKILL.md                 # Tier 2: LLM guidance (optional)
```

---

## Tier 1: YAML Skills (Deterministic)

YAML skills bypass the LLM entirely for speed and reliability. Define triggers, extract parameters, execute commands.

### Full Schema

```yaml
name: skill-name # Unique identifier
version: 1 # Schema version

triggers:
  patterns: # Regex patterns (0.9 confidence)
    - "block.*(?:domain|spam)"
    - "postfix.*(?:block|reject)"
  keywords: # Keyword matches (0.5+ confidence)
    - spam
    - block
    - postfix

parameters:
  domain: # Parameter name (used in {{domain}})
    pattern: "([a-z0-9.-]+\\.[a-z]{2,})" # Regex with capture group
    required: true # Fail if not extracted
    description: "Domain to block" # For debugging
    default: "example.com" # Optional fallback

target:
  agent: postfix01 # FunnelCloud agent ID
  platform: linux # windows | linux
  description: "Mail server" # For display

workflow:
  steps:
    - name: "Add to block list" # Step name (for logging)
      command: "echo '{{domain}} REJECT' | sudo tee -a /etc/postfix/sender_access"
      continueOnError: false # Stop on failure (default: false)
      timeoutSeconds: 30 # Per-step timeout (default: 30)
    - name: "Rebuild hash"
      command: "sudo postmap /etc/postfix/sender_access"
    - name: "Reload postfix"
      command: "sudo systemctl reload postfix"

responses:
  success: "Done! Blocked {{domain}} on postfix."
  partial: "Added {{domain}} but reload failed: {{error}}"
  failure: "Failed to block {{domain}}: {{error}}"
```

### Matching Algorithm

1. **Pattern Match** (0.9 confidence): Any regex in `triggers.patterns` matches
2. **Keyword Match** (0.5+ confidence): At least 2 keywords found, score = 0.5 + (matches/total × 0.3)
3. **Highest confidence wins**; ties go to first match

### Parameter Extraction

Parameters are extracted via regex capture groups from the user's input:

```yaml
parameters:
  domain:
    pattern: "[`'\"]?([a-z0-9.-]+\\.[a-z]{2,})[`'\"]?"
```

Given input: `Block spammer.com on postfix`, extracts `domain = "spammer.com"`.

### Workflow Execution

1. Validate all `required: true` parameters extracted
2. For each step:
   - Substitute `{{param}}` placeholders
   - Execute via `GrpcAgentClient.ExecuteAsync()` on target agent
   - Check exit code; if failed and `continueOnError: false`, stop
3. Return templated response (`success`, `partial`, or `failure`)

---

## Tier 2: Markdown Skills (LLM-Guided)

Markdown skills provide context to the LLM for tasks that require reasoning. The LLM reads the instructions and generates appropriate tool calls.

### Format

```markdown
---
name: my-skill-name
description: Brief description of what this skill does
tags: [tag1, tag2, tag3]
targetAgent: hostname # Optional: specific agent
---

# Skill Title

Instructions, commands, and documentation that the LLM will read
when planning execution. Include examples, expected outputs, and
any special considerations.

## Example Usage

\`\`\`bash

# Example command the LLM might generate

sudo systemctl restart nginx
\`\`\`
```

### Frontmatter Fields

| Field         | Required | Description                                   |
| ------------- | -------- | --------------------------------------------- |
| `name`        | No       | Skill identifier (defaults to directory name) |
| `description` | No       | Brief description for matching and display    |
| `tags`        | No       | Keywords for skill discovery                  |
| `targetAgent` | No       | Agent ID if skill is host-specific            |

### When to Use Markdown vs YAML

| Use YAML                              | Use Markdown                     |
| ------------------------------------- | -------------------------------- |
| Fixed command sequence                | Requires reasoning about context |
| Parameters can be extracted via regex | Complex parameter combinations   |
| Same steps every time                 | Steps vary based on situation    |
| Speed is critical                     | Flexibility is critical          |

---

## Writing Good Skills

1. **Start with YAML**: If the task is repeatable, define a YAML skill first
2. **Add Markdown fallback**: Create SKILL.md for edge cases the YAML doesn't handle
3. **Be Specific**: Include exact commands, paths, and syntax
4. **Document Context**: Explain when and why to use each command
5. **Show Examples**: Provide complete examples with expected output
6. **Note Requirements**: Document sudo requirements, prerequisites, etc.
7. **Use Tags**: Add relevant keywords for discovery

## Skill Locations

Skills are loaded from (in priority order):

1. `./skills/` - Workspace root
2. `~/.aj/skills/` - User home directory

## API Endpoints

- `GET /api/orchestrate/skills` - List all loaded skills
- `POST /api/orchestrate/reload-skills` - Reload skills from disk
