---
name: postfix-spam-blocking
description: Block unwanted sender domains on the postfix01 mail server
tags: [mail, spam, postfix, security, email, block]
---

# Postfix Spam Domain Blocking

Block unwanted sender domains on the postfix01 mail server.

## Execution Model

**CRITICAL:** All commands in this skill MUST be executed via the FunnelCloud agent system.

- Use the `execute` tool with `agent_id: "postfix01"`
- NEVER output raw shell commands - always wrap them in the execute tool
- The orchestrator routes commands to agents; you cannot run them directly

### Correct Action Format

```json
{
  "tool": "execute",
  "params": { "agent_id": "postfix01", "command": "your command here" },
  "reasoning": "why"
}
```

### Example: Block a spam domain

User: "Block spammer.com on postfix"

```json
{
  "tool": "execute",
  "params": {
    "agent_id": "postfix01",
    "command": "echo 'spammer.com              REJECT Spam domain blocked' | sudo tee -a /etc/postfix/sender_access && sudo postmap /etc/postfix/sender_access && sudo systemctl reload postfix"
  },
  "reasoning": "Adding spammer.com to sender_access block list and reloading postfix"
}
```

## Connection Details

- **Agent ID:** `postfix01`
- **Platform:** Linux
- **Authentication:** Handled by FunnelCloud agent (no SSH needed)

## Important Notes

1. Commands execute via FunnelCloud agent - use `execute` tool with `agent_id: "postfix01"`
2. Most operations require sudo (the agent runs with appropriate permissions)
3. After modifying the block list, always run `postmap` and reload postfix

## Block List Location

- **File:** `/etc/postfix/sender_access`
- **Format:** `domain.com    REJECT Reason message`
- **Database:** Hash map at `/etc/postfix/sender_access.db`

## Operations

### Block a Sender Domain

To block a domain from sending mail:

```bash
# Add the domain to the block list
echo 'spammer.com              REJECT Spam domain blocked' | sudo tee -a /etc/postfix/sender_access

# Rebuild the hash database and reload postfix
sudo postmap /etc/postfix/sender_access && sudo systemctl reload postfix
```

### View Currently Blocked Domains

```bash
sudo cat /etc/postfix/sender_access
```

### Check Recent Mail Activity

View recent mail log entries:

```bash
sudo tail -100 /var/log/mail.log
```

### Find Top Senders (Spam Detection)

Identify high-volume senders that might be spam:

```bash
sudo grep 'status=sent' /var/log/mail.log | grep -oP 'from=<[^>]+>' | sort | uniq -c | sort -rn | head -20
```

### Search for Specific Domain

Check if a domain has been sending mail:

```bash
sudo grep 'example.com' /var/log/mail.log | tail -30
```

## Block List Entry Formats

```
# Comment describing the block
domain.com              REJECT Spam domain blocked
subdomain.domain.com    REJECT Specific subdomain blocked
.domain.com             REJECT All subdomains of domain.com
user@domain.com         REJECT Specific sender address
```

## After Any Block List Changes

Always run these commands after modifying `/etc/postfix/sender_access`:

```bash
sudo postmap /etc/postfix/sender_access
sudo systemctl reload postfix
```

## Log Files

- **Current:** `/var/log/mail.log`
- **Previous:** `/var/log/mail.log.1`
- **Archived:** `/var/log/mail.log.*.gz` (use `zgrep` or `zcat`)
