# Web Fetch Skill

Fetches and parses web pages to extract readable text content.

## Usage

Ask AJ to fetch a URL:

- "Fetch https://example.com"
- "What does the page at https://news.ycombinator.com say?"
- "Get the content from https://status.github.com"
- "Look up https://api.ipify.org"

## How It Works

1. Uses `curl` to fetch the page (30 second timeout, 1MB max)
2. Pipes through a Python HTML parser that:
   - Strips `<script>`, `<style>`, `<nav>`, `<footer>`, `<header>`, `<aside>` tags
   - Extracts readable text content
   - Truncates to ~4000 characters for context window

## Target Agent

Any agent with:

- `curl` installed
- `python3` with standard library (no extra packages needed)

Default: `ians-r16` (can be changed in skill.yaml)

## Security Notes

- Only fetches via secure HTTPS connections
- No file downloads, only text content extraction
- No max file size (string only content)
- No timeout
- Agent's network access policies apply
  `
