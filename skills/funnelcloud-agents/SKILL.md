---
name: funnelcloud-agents
description: Execute commands on remote machines via FunnelCloud agent mesh
tags: []
---

# FunnelCloud Agent Execution

Execute commands on remote Windows and Linux machines via the FunnelCloud agent mesh.

## Architecture Overview

FunnelCloud is a distributed agent mesh for remote command execution:

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              Orchestrator                                    │
│                         (Docker container)                                   │
│  ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐          │
│  │ Reasoning Engine│───▶│ Skill Executor  │───▶│  Agent Client   │          │
│  └─────────────────┘    └─────────────────┘    └────────┬────────┘          │
└─────────────────────────────────────────────────────────┼────────────────────┘
                                                          │ gRPC (port 41235)
                    ┌─────────────────────────────────────┼─────────────────┐
                    │                                     │                 │
                    ▼                                     ▼                 ▼
           ┌────────────────┐                    ┌────────────────┐  ┌────────────────┐
           │  FunnelCloud   │◀──── Gossip ─────▶│  FunnelCloud   │  │  FunnelCloud   │
           │    Agent       │    Protocol       │    Agent       │  │    Agent       │
           │  (Windows)     │  (port 41420)     │   (Linux)      │  │   (macOS)      │
           │  ians-r16      │                   │  webserver01   │  │  macbook01     │
           └────────────────┘                   └────────────────┘  └────────────────┘
```

### How It Works

1. **Agent Discovery**: Agents announce themselves via UDP gossip protocol (port 41420). Each agent shares its capabilities, platform, and IP address with peers.

2. **Cross-Subnet Discovery**: A gossip seed host bridges network segments, allowing agents on different subnets/VLANs to discover each other.

3. **Command Execution**: The orchestrator routes commands via gRPC (port 41235) to the target agent, which executes locally and streams stdout/stderr back.

4. **Platform Detection**: Agents report their platform (windows/linux/macos). The orchestrator selects the appropriate shell (PowerShell for Windows, bash for Linux/macOS).

## CRITICAL: No Fabrication

**NEVER fabricate command output.** You cannot run commands directly—all execution happens through the orchestrator API. If you don't see real output from the orchestrator, DO NOT make up results.

### Signs of Fabrication (IRON GATE will block these)

- Showing `ping` output without calling `execute`
- Displaying `ipconfig` results you invented
- Pretending to see file contents without `read_file`
- Claiming a command succeeded without orchestrator confirmation

### Correct Behavior

1. Call the `execute` tool with agent_id and command
2. Wait for orchestrator to return actual output
3. Show the REAL output to the user

## Available Tools

### `list_agents` - Discover Available Agents

Returns all FunnelCloud agents currently connected to the mesh.

**Response format:**

```
Found N agent(s):
- agent_id (hostname, platform)
- agent_id (hostname, platform)
```

**Example:**

```
Found 4 agent(s):
- ians-r16 (IANS-R16, windows)
- webserver01 (webserver01, linux)
- dbserver01 (dbserver01, linux)
- dc01 (DC01, windows)
```

### `execute` - Run Command on Agent

Execute a shell/PowerShell command on a specific agent.

**Parameters:**
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `agent_id` | string | Yes | Target agent ID from `list_agents` |
| `command` | string | Yes | Shell command to execute |

**Action format:**

```json
{
  "tool": "execute",
  "params": {
    "agent_id": "target-agent-id",
    "command": "your command here"
  },
  "reasoning": "Brief explanation of why"
}
```

## Platform-Specific Commands

### Windows Agents

Use PowerShell syntax. Commands run in a PowerShell session:

```json
{
  "tool": "execute",
  "params": {
    "agent_id": "dc01",
    "command": "Get-Service | Where-Object {$_.Status -eq 'Running'} | Select-Object -First 10"
  }
}
```

### Linux Agents

Use bash syntax. Commands run in a bash shell:

```json
{
  "tool": "execute",
  "params": {
    "agent_id": "webserver01",
    "command": "systemctl status nginx"
  }
}
```

## Base64 Execution (Escape-Safe)

For complex commands with quotes, special characters, or multi-line scripts, use base64 encoding to avoid JSON escaping issues.

### Windows (PowerShell) - EncodedCommand

PowerShell accepts base64-encoded UTF-16LE commands via `-EncodedCommand`:

```json
{
  "tool": "execute",
  "params": {
    "agent_id": "ians-r16",
    "command": "powershell -EncodedCommand JABzAGUAcgB2AGkAYwBlAHMAIAA9ACAARwBlAHQALQBTAGUAcgB2AGkAYwBlACAALQBOAGEAbQBlACAAIgBXAGkAbgBSAE0AIgA7ACAAJABzAGUAcgB2AGkAYwBlAHMA"
  },
  "reasoning": "Using encoded command to avoid quote escaping issues"
}
```

To encode a PowerShell command (UTF-16LE → base64):

```powershell
$cmd = '$services = Get-Service -Name "WinRM"; $services'
[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cmd))
```

### Linux (Bash) - Pipe from base64

Bash can decode and execute base64-encoded scripts:

```json
{
  "tool": "execute",
  "params": {
    "agent_id": "webserver01",
    "command": "echo 'ZWNobyAiSGVsbG8gZnJvbSBiYXNoIgpscyAtbGEgL3Zhci9sb2cv' | base64 -d | bash"
  },
  "reasoning": "Using base64 to safely pass multi-line script"
}
```

To encode a bash script:

```bash
echo -n 'echo "Hello from bash"
ls -la /var/log/' | base64
```

### When to Use Base64

- Commands with nested quotes (`"` inside `'` or vice versa)
- Multi-line scripts
- Commands with special characters (`$`, `\`, backticks)
- Long PowerShell pipelines with complex Where-Object filters
- Regex patterns with escape sequences

## Common Tasks

### Check connectivity

Windows:

```json
{
  "tool": "execute",
  "params": {
    "agent_id": "ians-r16",
    "command": "ping -n 4 google.com"
  },
  "reasoning": "Testing internet connectivity from workstation"
}
```

Linux:

```json
{
  "tool": "execute",
  "params": {
    "agent_id": "webserver01",
    "command": "ping -c 4 google.com"
  },
  "reasoning": "Testing internet connectivity from server"
}
```

### List running processes (Windows)

```json
{
  "tool": "execute",
  "params": {
    "agent_id": "dc01",
    "command": "Get-Process | Sort-Object CPU -Descending | Select-Object -First 10 Name, CPU, WorkingSet"
  }
}
```

### Check disk space (Linux)

```json
{
  "tool": "execute",
  "params": {
    "agent_id": "webserver01",
    "command": "df -h"
  }
}
```

### Service management (Linux)

```json
{
  "tool": "execute",
  "params": {
    "agent_id": "webserver01",
    "command": "sudo systemctl restart nginx && systemctl status nginx"
  }
}
```

## Workflow

1. **If agents are unknown:** Call `list_agents` first to see what's available
2. **Match agent to task:** Use hostname/platform to pick the right target
3. **Execute command:** Use `execute` with the correct agent_id (use base64 if escaping is tricky)
4. **Report results:** Show the ACTUAL orchestrator output, not invented data

## Expected Response Flow

When you call `execute`, the orchestrator will:

1. Discover agents via gossip protocol
2. Route the command via gRPC to the target agent
3. Agent executes locally (PowerShell on Windows, bash on Linux)
4. Stream stdout/stderr back to orchestrator
5. Display in format:

```
**Execute `command` on `agent_id`:**
```

actual command output here

```

```

## Error Handling

If execution fails, you'll see:

- `(no output)` - Command produced no stdout
- `Agent not found` - Agent is offline or unknown
- `Connection refused` - gRPC port (41235) unreachable
- `Timeout` - Command exceeded 30 second limit
- Error messages from stderr

**Do not invent success if you see failure. Report what actually happened.**

## What You Cannot Do

- Execute locally (no direct shell access)
- Access agents not in the mesh
- Run commands without going through orchestrator
- Fabricate output that wasn't returned by the system

## Agent Identification

When user says vague things like "my workstation" or "the server":

1. Check `list_agents` output for context clues
2. Look for hostnames that match (e.g., "ians-r16" might be Ian's workstation)
3. **ASK if ambiguous** - don't guess which agent to target

## Security Notes

- Commands run with agent's configured permissions
- Some operations require `sudo` on Linux
- Elevation requests are logged
- The orchestrator enforces timeouts (default 30s)
- All gRPC traffic can be TLS-encrypted with mutual certificate auth
