"""
AJ Filter - Agentic Reasoning & Memory

Open-WebUI filter that handles:
  - Intent classification (task vs casual - 2 class)
  - Multi-step reasoning via orchestrator
  - Code execution via FunnelCloud agents
  - Semantic memory storage and retrieval (automatic)
"""

import os
import re
import json
import base64
import sys
import requests

from typing import Optional, List, Tuple, Dict, Any
from enum import Enum
from pydantic import BaseModel, Field

# ============================================================================
# Centralized Logging Utilities (Inline for Open-WebUI Compatibility)
# ============================================================================


class LogLevel(str, Enum):
    """Status/state of a message."""

    SUCCESS = "success"
    DONE = "done"
    SAVED = "saved"
    VERIFIED = "verified"
    RUNNING = "running"
    THINKING = "thinking"
    PROCESSING = "processing"
    LOADING = "loading"
    SCANNING = "scanning"
    INFO = "info"
    READY = "ready"
    WAITING = "waiting"
    WARNING = "warning"
    PARTIAL_ERROR = "partial_error"
    ERROR = "error"
    FAILED = "failed"
    BLOCKED = "blocked"
    MEMORY = "memory"
    CONTEXT = "context"
    EXECUTING = "executing"
    RESULT = "result"


class LogCategory(str, Enum):
    """Source/component emitting the message."""

    FILTER = "filter"
    ORCHESTRATOR = "orchestrator"
    EXECUTOR = "executor"
    MEMORY = "memory"
    PRAGMATICS = "pragmatics"
    EXTRACTOR = "extractor"
    DISPATCHER = "dispatcher"
    REASONING = "reasoning"
    FILE_HANDLER = "file_handler"
    SHELL_HANDLER = "shell_handler"
    POLYGLOT_HANDLER = "polyglot_handler"
    STATE = "state"
    TASK_PLANNER = "task_planner"
    TRAINING = "training"
    VALIDATION = "validation"
    MIGRATION = "migration"


# Icon mapping: (LogCategory, LogLevel) -> icon
ICON_MAP: Dict[Tuple[LogCategory, LogLevel], str] = {
    (LogCategory.FILTER, LogLevel.SUCCESS): "✅",
    (LogCategory.FILTER, LogLevel.DONE): "✅",
    (LogCategory.FILTER, LogLevel.SAVED): "🧠",
    (LogCategory.FILTER, LogLevel.THINKING): "💭",
    (LogCategory.FILTER, LogLevel.RUNNING): "⏳",
    (LogCategory.FILTER, LogLevel.READY): "✅",
    (LogCategory.FILTER, LogLevel.MEMORY): "📚",
    (LogCategory.FILTER, LogLevel.CONTEXT): "📖",
    (LogCategory.FILTER, LogLevel.WAITING): "⏸️",
    (LogCategory.FILTER, LogLevel.INFO): "ℹ️",
    (LogCategory.FILTER, LogLevel.WARNING): "⚠️",
    (LogCategory.FILTER, LogLevel.PARTIAL_ERROR): "⚠️",
    (LogCategory.FILTER, LogLevel.ERROR): "❌",
    (LogCategory.FILTER, LogLevel.FAILED): "❌",
    (LogCategory.FILTER, LogLevel.BLOCKED): "❌",
    (LogCategory.FILTER, LogLevel.SCANNING): "🔍",
    (LogCategory.ORCHESTRATOR, LogLevel.SUCCESS): "✅",
    (LogCategory.ORCHESTRATOR, LogLevel.DONE): "✅",
    (LogCategory.ORCHESTRATOR, LogLevel.THINKING): "🧠",
    (LogCategory.ORCHESTRATOR, LogLevel.RUNNING): "⚡",
    (LogCategory.ORCHESTRATOR, LogLevel.INFO): "ℹ️",
    (LogCategory.ORCHESTRATOR, LogLevel.WARNING): "⚠️",
    (LogCategory.ORCHESTRATOR, LogLevel.ERROR): "❌",
    (LogCategory.ORCHESTRATOR, LogLevel.LOADING): "⏳",
    (LogCategory.MEMORY, LogLevel.SAVED): "💾",
    (LogCategory.MEMORY, LogLevel.MEMORY): "📚",
    (LogCategory.MEMORY, LogLevel.CONTEXT): "📖",
    (LogCategory.MEMORY, LogLevel.ERROR): "❌",
}

DEFAULT_ICON = "◆"


def get_icon(category: LogCategory, level: LogLevel) -> str:
    """Get icon for category and level combination."""
    return ICON_MAP.get((category, level), DEFAULT_ICON)


def log_message(
    message: str,
    category: LogCategory,
    level: LogLevel,
    include_icon: bool = True,
    icon_override: Optional[str] = None,
) -> str:
    """Generate formatted log message with icon."""
    if not include_icon:
        return message
    icon = icon_override if icon_override else get_icon(category, level)
    return f"{icon} {message}"


def create_status_dict(
    message: str,
    category: LogCategory,
    level: LogLevel,
    done: bool = False,
    hidden: bool = False,
) -> Dict[str, any]:
    """Create status dict ready for event emitter."""
    formatted_message = log_message(message, category, level)
    return {
        "type": "status",
        "data": {
            "description": formatted_message,
            "done": done,
            "hidden": hidden,
        },
    }


def create_error_dict(
    message: str, category: LogCategory, done: bool = True, hidden: bool = False
) -> Dict[str, any]:
    """Create error status dict."""
    return create_status_dict(
        message, category, LogLevel.ERROR, done=done, hidden=hidden
    )


def create_success_dict(
    message: str, category: LogCategory, done: bool = True, hidden: bool = False
) -> Dict[str, any]:
    """Create success status dict."""
    return create_status_dict(
        message, category, LogLevel.SUCCESS, done=done, hidden=hidden
    )


# ============================================================================
# Configuration
# ============================================================================

MEMORY_API_URL = os.getenv("MEMORY_API_URL", "http://memory_api:8000")
EXTRACTOR_API_URL = os.getenv("EXTRACTOR_API_URL", "http://extractor_api:8002")
ORCHESTRATOR_API_URL = os.getenv("ORCHESTRATOR_API_URL", "http://orchestrator_api:8004")
EXECUTOR_API_URL = os.getenv("EXECUTOR_API_URL", "http://executor_api:8005")

# FunnelCloud tray app HTTP listener for event forwarding (Docker -> host)
# Uses host.docker.internal on Windows/Mac Docker Desktop
TRAY_EVENT_URL = os.getenv("TRAY_EVENT_URL", "http://host.docker.internal:6666")

# System prompt describing AJ capabilities and output contracts
# This is injected into the chat LLM that presents results to users
AJ_SYSTEM_PROMPT = """# AJ

You are AJ, a capable AI assistant. Speak in first person. Be helpful, concise, and natural.

## Personality

- Mirror the user's communication style — match their formality, brevity, or depth.
- Technically strong, calm, and direct. Slightly witty when it fits the mood.
- Never theatrical, never robotic. Just a competent assistant who gets things done.

## Response Length

CRITICAL: Match response length to question complexity.
- Simple questions get simple answers. "What is 5+3?" → "8"
- Math problems: Show work briefly, give the answer, stop.
- Technical questions: Be thorough but focused.
- Conversational: Be warm but don't ramble.

## Stopping Rules (IMPORTANT)

- Once you've answered the question, STOP. Do not:
  - Generate follow-up questions you then answer
  - Create fictional dialogue or Q&A sessions
  - Repeat your answer in different words
  - Add "I hope this helps!" multiple times
- One complete answer. One closing if needed. Done.

## Output Style

- Use clean Markdown.
- Use fenced code blocks for commands, code, and log text.
- Use inline `backticks` for paths, commands, variables, hostnames, and technical identifiers.
- Do NOT reply with JSON unless specifically requested.

## Working With Results

When commands or tools produce output:
- Show the important output accurately
- Preserve exact wording for logs, errors, and diffs
- Add brief commentary only when it genuinely helps
- Keep multiple outputs in chronological order

## Accuracy Rules

- NEVER fabricate command results, file contents, agents, hosts, IPs, or execution status.
- Only state things supported by actual input, tool output, or file content.
- When uncertain, say so plainly.

## Internal Markers (DO NOT OUTPUT)

Never include these in your response — they are internal system markers:
- ### Retrieved Memories ### / ### End Memories ###
- ### Task Plan ### / ### End Result ###
- <context> / </context> tags

## Core Behaviors

- For technical tasks, be results-oriented with concrete outputs.
- Prefer CLI-style workflows over vague descriptions.
- Do not expose internal mechanics, routing logic, or memory plumbing unless asked.

## File Editing

- Prefer minimal, surgical edits.
- Preserve surrounding style and structure unless rewrite is requested.
"""

# File type to MIME type mapping
CONTENT_TYPE_MAP = {
    ".md": "text/markdown",
    ".txt": "text/plain",
    ".py": "text/x-python",
    ".js": "text/javascript",
    ".ts": "text/typescript",
    ".json": "application/json",
    ".pdf": "application/pdf",
    ".png": "image/png",
    ".jpg": "image/jpeg",
    ".jpeg": "image/jpeg",
    ".gif": "image/gif",
    ".mp3": "audio/mpeg",
    ".wav": "audio/wav",
    ".m4a": "audio/mp4",
}

# ============================================================================
# IRON GATE: Fabricated CLI Output Detection
# Blocks LLM responses that contain fake command output (ipconfig, ping, etc.)
# when the orchestrator never actually executed anything.
# ============================================================================

# Regex signatures that identify fabricated CLI/shell command output.
# These patterns match the distinctive formatting of common Windows/Linux
# command outputs that LLMs tend to fabricate when asked to run commands.
# Requires 2+ matches to trigger (avoids false positives from casual mentions).
FABRICATED_CLI_OUTPUT_SIGNATURES = [
    # Network configuration (ipconfig/ifconfig)
    r"IPv4 Address[.\s]*:\s*\d+\.\d+\.\d+\.\d+",
    r"IP Address[.\s]*:\s*\d+\.\d+\.\d+\.\d+",
    r"Subnet Mask[.\s]*:\s*\d+\.\d+\.\d+\.\d+",
    r"Default Gateway[.\s]*:\s*\d+\.\d+\.\d+\.\d+",
    r"IPv6 Address[.\s]*:",
    r"Physical Address[.\s]*:\s*[\da-fA-F:-]+",
    r"MAC[.\s]*:\s*[\da-fA-F:-]+",
    r"DHCP (?:Enabled|Server)",
    r"DNS (?:Servers?|Suffix)",
    # Ping output
    r"Reply from\s+\d+\.\d+\.\d+\.\d+",
    r"bytes=\d+\s+time[<=]\d+",
    r"icmp_seq=\d+\s+ttl=\d+",
    r"\d+\s+bytes\s+from\s+\d+\.\d+",
    r"time=\d+(?:\.\d+)?\s*ms",
    r"TTL=\d+",
    # Process/service listing
    r"(?:Handles|NPM|PM|WS|CPU)\s+\w+.*\d+",
    r"PID\s*[:=]?\s*\d{3,}",
    # Ethernet adapter output
    r"Ethernet adapter\s+\w+:",
    r"Wireless.*adapter\s+\w+:",
    r"Connection-specific DNS Suffix",
    r"Link-local IPv6 Address",
    # General command output markers
    r"Lease (?:Obtained|Expires)",
    r"Autoconfiguration (?:Enabled|IPv4)",
]


def _contains_fabricated_cli_output(text: str) -> bool:
    """
    Check if text contains fabricated CLI command output.

    Used by the IRON GATE to block LLM responses that fake execution results
    (e.g., made-up ipconfig output, fake ping replies, invented process lists)
    when the orchestrator never actually ran any commands.

    Returns True if 2+ signature patterns match (high confidence of fabrication).
    """
    if not text:
        return False

    match_count = 0
    for pattern in FABRICATED_CLI_OUTPUT_SIGNATURES:
        if re.search(pattern, text, re.IGNORECASE):
            match_count += 1
            # Require multiple matches for stronger confidence
            if match_count >= 2:
                return True

    return False


# ============================================================================
# Intent Classification (2-class: task vs casual)
# Uses orchestrator's LLM - single model for all classification
# ============================================================================


def _classify_intent(text: str) -> Dict[str, Any]:
    """
    Classify user intent via orchestrator's LLM.

    Two intents only:
      - task: Requires execution (remote commands, file ops, queries to agents)
      - casual: Conversation, questions, chitchat

    Memory search/save happens automatically regardless of intent.

    Uses orchestrator's /classify endpoint which uses the same Ollama model
    as task execution - no separate classifier model needed.

    Returns:
        {"intent": "task"|"casual", "confidence": float}
    """
    try:
        resp = requests.post(
            f"{ORCHESTRATOR_API_URL}/api/orchestrate/classify",
            json={"text": text},
            timeout=10,  # LLM inference may take a moment
        )
        if resp.status_code == 200:
            result = resp.json()
            intent = result.get("intent", "casual")
            confidence = result.get("confidence", 0.5)

            print(f"[aj] Intent: {intent} ({confidence:.2f})")
            return {"intent": intent, "confidence": confidence}
    except Exception as e:
        print(f"[aj] Orchestrator classify error: {e}")

    # Fallback if orchestrator is down - default to task (safer to check than miss)
    print("[aj] Warning: Orchestrator unavailable, defaulting to task")
    return {"intent": "task", "confidence": 0.3}


def _detect_task_continuation(
    user_text: str, messages: List[dict], confidence: float
) -> bool:
    """
    Detect if a 'casual' response is actually continuing a task.

    Uses orchestrator's LLM with context to determine if short
    responses like "yes", "ok", "do it" are task continuations.

    Args:
        user_text: Current user message
        messages: Conversation history
        confidence: Initial classification confidence

    Returns True if we should upgrade intent to 'task'.
    """
    # Get recent assistant message for context
    context = ""

    for msg in reversed(messages):
        if msg.get("role") == "assistant":
            content = msg.get("content", "")
            if isinstance(content, str) and content.strip():
                # Take last ~500 chars of assistant message as context
                context = content[-500:] if len(content) > 500 else content
                break

    # If no context, can't determine continuation
    if not context:
        print(f"[aj] Task continuation: no assistant context")
        return False

    # Use orchestrator's classify endpoint with context
    try:
        resp = requests.post(
            f"{ORCHESTRATOR_API_URL}/api/orchestrate/classify",
            json={"text": user_text, "context": context},
            timeout=10,
        )
        if resp.status_code == 200:
            result = resp.json()
            intent = result.get("intent", "casual")
            ctx_confidence = result.get("confidence", 0.5)

            if intent == "task":
                print(
                    f"[aj] Task continuation: YES via orchestrator (conf={ctx_confidence:.2f})"
                )
                return True
            else:
                print(
                    f"[aj] Task continuation: NO via orchestrator (intent={intent}, conf={ctx_confidence:.2f})"
                )
                return False
    except Exception as e:
        print(f"[aj] Task continuation: orchestrator error: {e}")

    # Fallback: if orchestrator unavailable, don't upgrade (safe default)
    return False


# ============================================================================
# Orchestrator Integration
# ============================================================================


def _build_task_description(messages: List[dict]) -> str:
    """
    Build a complete task description from conversation context.

    For continuation requests like "Please do" or "yes", we need to include
    the original task context from previous messages so the orchestrator
    knows what to do.
    """
    user_text = _extract_user_text_prompt(messages) or ""

    # Check if this is a short continuation response
    short_continuations = [
        "please do",
        "yes",
        "yeah",
        "sure",
        "ok",
        "okay",
        "go ahead",
        "do it",
        "proceed",
        "make the changes",
        "sounds good",
        "that works",
        "perfect",
        "great",
        "continue",
        "next",
    ]

    user_lower = user_text.lower().strip()
    is_continuation = len(user_text) < 100 and any(
        user_lower.startswith(cont) or user_lower == cont
        for cont in short_continuations
    )

    if not is_continuation:
        return user_text

    # This is a continuation - find the original task from conversation history
    # Look for the most recent substantial user message
    for msg in reversed(messages[:-1]):  # Skip current message
        if msg.get("role") != "user":
            continue

        content = msg.get("content", "")
        if isinstance(content, list):
            # Extract text from multi-part content
            texts = [
                p.get("text", "")
                for p in content
                if isinstance(p, dict) and p.get("type") == "text"
            ]
            content = " ".join(texts)

        if not isinstance(content, str):
            continue

        # Skip short messages that are also continuations
        if len(content) < 50:
            continue

        # Skip messages that look like injected context
        if content.startswith("### ") or "End Workspace Files" in content:
            continue

        # Found the original task
        print(f"[aj] Continuation detected - using original task: {content[:100]}...")
        return f"User originally asked: {content}\n\nUser now confirms: {user_text}"

    # Fallback to current text if no original task found
    return user_text


def _is_json_plan_content(content: str) -> bool:
    """
    Detect if content is raw JSON planning output that should be formatted.

    Returns True for:
    - JSON plan arrays with "step" and "action" fields
    - JSON objects with "requires_confirmation"
    - "Follow up" suggestion blocks
    """
    if not content:
        return False

    # Check for plan step patterns
    if '"step":' in content and '"action":' in content:
        return True

    # Check for confirmation patterns
    if '"requires_confirmation":' in content:
        return True

    # Check for follow-up blocks
    if content.strip().startswith("Follow up"):
        return True

    return False


def _format_json_plan_as_list(content: str) -> str:
    """
    Convert raw JSON plan output into readable list format.

    Input: {"step": 1, "action": "..."}, {"step": 2, "action": "..."}
    Output: 1. First action
            2. Second action
    """
    import re

    # Try to extract steps from JSON
    steps = []

    # Pattern to match {"step": N, "action": "text"}
    step_pattern = r'\{\s*"step"\s*:\s*(\d+)\s*,\s*"action"\s*:\s*"([^"]+)"\s*\}'
    matches = re.findall(step_pattern, content)

    if matches:
        for step_num, action in matches:
            steps.append(f"{step_num}. {action}")

    if steps:
        # Return formatted list
        return "\n".join(steps) + "\n"

    # If no steps found, return empty (will be filtered)
    return ""


# ============================================================================
# Tray Event Forwarding
# ============================================================================

import uuid
from datetime import datetime, timezone

# Global httpx client for tray events (reused across requests)
_tray_client = None


def _get_tray_client():
    """Get or create the httpx client for tray events."""
    global _tray_client
    if _tray_client is None:
        import httpx

        _tray_client = httpx.Client(timeout=1.0)  # Short timeout, non-blocking
    return _tray_client


def _forward_event_to_tray(
    kind: str,
    command: str = None,
    task_id: str = None,
    agent_id: str = None,
    exit_code: int = None,
    stdout: str = None,
    stderr: str = None,
    duration_seconds: float = None,
    error_message: str = None,
):
    """
    Forward an event to the FunnelCloud tray app via HTTP.
    This is fire-and-forget - errors are silently ignored.
    """
    try:
        event = {
            "event_id": uuid.uuid4().hex,
            "timestamp": datetime.now(timezone.utc).isoformat(),
            "kind": kind,
        }
        if task_id:
            event["task_id"] = task_id
        if command:
            event["command"] = command
        if agent_id:
            event["agent_id"] = agent_id
        if exit_code is not None:
            event["exit_code"] = exit_code
        if stdout:
            event["stdout"] = stdout[:1000]  # Truncate for tray display
        if stderr:
            event["stderr"] = stderr[:500]
        if duration_seconds is not None:
            event["duration_seconds"] = duration_seconds
        if error_message:
            event["error_message"] = error_message

        client = _get_tray_client()
        client.post(f"{TRAY_EVENT_URL}/event", json=event)
    except Exception:
        # Silently ignore - tray may not be running
        pass


async def _orchestrate_task(
    user_id: str,
    messages: List[dict],
    workspace_root: Optional[str],
    __event_emitter__,
    memory_context: Optional[List[dict]] = None,
    model: Optional[str] = None,
) -> Tuple[Optional[str], str, bool]:
    """
    Handle task intents via orchestrator streaming endpoint.

    Flow:
      1. POST /run-task to orchestrator (starts SSE stream)
      2. Forward status events to __event_emitter__
      3. Return (final_context, streamed_content, execution_occurred) when complete

    The agentic loop now runs in the orchestrator, not here.
    Returns both the context for LLM and the accumulated streamed content
    to preserve thinking/results in the final response.
    Also returns whether actual command execution occurred (for hallucination detection).
    """
    if not workspace_root:
        return None, "", False

    # Build complete task description (handles continuations)
    user_text = _build_task_description(messages)

    # Accumulate all streamed content so it persists after LLM responds
    streamed_content = ""
    # IRON GATE: Track if actual command execution occurred
    execution_occurred = False

    try:
        # Note: Don't emit "Thinking" here - orchestrator will emit it and we forward

        # Stream task execution from orchestrator
        import httpx

        async with httpx.AsyncClient(
            timeout=httpx.Timeout(300.0, connect=10.0)
        ) as client:
            async with client.stream(
                "POST",
                f"{ORCHESTRATOR_API_URL}/api/orchestrate/run-task",
                json={
                    "task": user_text,
                    "workspace_root": workspace_root,
                    "user_id": user_id,
                    "memory_context": memory_context,
                    "max_steps": 100,
                    "preserve_state": True,  # Always preserve state for multi-turn conversations
                    "model": model,  # Pass selected model from Open-WebUI
                },
            ) as response:
                final_context = None
                plan_shown = False  # Track if we already showed the plan

                async for line in response.aiter_lines():
                    if not line.startswith("data: "):
                        continue

                    try:
                        event = json.loads(line[6:])  # Strip "data: " prefix
                    except json.JSONDecodeError:
                        continue

                    event_type = event.get("event_type", "")
                    status = event.get("status", "")
                    done = event.get("done", False)

                    # Forward status events to UI (these don't accumulate in message)
                    if event_type == "status" and status:
                        await __event_emitter__(
                            create_status_dict(
                                status, LogCategory.FILTER, LogLevel.RUNNING, done=done
                            )
                        )

                    elif event_type == "plan":
                        # Stream formatted plan (only once)
                        content = event.get("content", "")
                        if content and not plan_shown:
                            # Content already has "📋 Task Plan:" header from orchestrator
                            # Display directly without blockquote wrapping for cleaner rendering
                            plan_block = content + "\n\n"
                            streamed_content += plan_block
                            await __event_emitter__(
                                {"type": "message", "data": {"content": plan_block}}
                            )
                            plan_shown = True

                    elif event_type == "thinking":
                        # Stream thinking content to the message AND accumulate
                        content = event.get("content", "")
                        if content:
                            # Skip if this is the plan content (already shown via plan event)
                            if plan_shown and (
                                "Task Plan" in content or "📋" in content
                            ):
                                continue

                            # Check for JSON plan output - format it nicely instead of raw JSON
                            if _is_json_plan_content(content):
                                formatted = _format_json_plan_as_list(content)
                                if formatted:
                                    streamed_content += formatted
                                    await __event_emitter__(
                                        {
                                            "type": "message",
                                            "data": {"content": formatted},
                                        }
                                    )
                                # Skip raw JSON that couldn't be formatted
                                continue

                            # Skip orphaned thinking emoji prefixes from orchestrator
                            if content.strip() in ("💭", "💭 "):
                                continue

                            streamed_content += content
                            await __event_emitter__(
                                {"type": "message", "data": {"content": content}}
                            )

                    elif event_type == "step":
                        # Forward task_received to tray for any agent execution —
                        # covers built-in execute tools AND skill-based executions
                        # (e.g. funnelcloud-agents) that carry agent_id + command.
                        step_result = event.get("result", {})
                        step_tool = step_result.get("tool", "")
                        step_params = step_result.get("params", {})
                        cmd = step_params.get("command", "")
                        agent_id = step_params.get("agent_id", "")
                        is_agent_exec_tool = step_tool in (
                            "execute",
                            "remote_execute",
                            "remote_execute_all",
                        )
                        if is_agent_exec_tool or (agent_id and cmd):
                            _forward_event_to_tray(
                                kind="task_received",
                                command=cmd,
                                agent_id=agent_id,
                            )

                    elif event_type == "result":
                        # Stream tool output in a code block
                        tool = event.get("tool", "")
                        params = event.get("params", {})
                        result = event.get("result", {})
                        output = result.get("output_preview", "")

                        # Always show result for execute tools, even if empty
                        # This prevents the user from thinking a command succeeded
                        # when it actually returned nothing
                        if output or tool in (
                            "execute",
                            "remote_execute",
                            "remote_execute_all",
                        ):
                            # Extract command and agent for execute tools
                            cmd = params.get("command", "")
                            agent_id = params.get("agent_id", "")

                            # Build label showing what was run
                            if tool == "execute" and cmd:
                                if agent_id:
                                    label = f"Execute `{cmd}` on `{agent_id}`"
                                else:
                                    label = f"Execute `{cmd}`"
                            else:
                                tool_labels = {
                                    "scan_workspace": "Directory listing",
                                    "read_file": "File content",
                                    "execute_shell": "Command output",
                                    "remote_execute": "remote_execute output",
                                    "remote_execute_all": "remote_execute output",
                                    "list_agents": "list_agents output",
                                }
                                label = tool_labels.get(tool, f"{tool} output")

                            # Show explicit "(no output)" for empty results
                            display_output = output if output else "(no output)"

                            # Stream the code block AND accumulate
                            code_block = (
                                f"\n\n**{label}:**\n```\n{display_output}\n```\n"
                            )
                            streamed_content += code_block
                            await __event_emitter__(
                                {"type": "message", "data": {"content": code_block}}
                            )
                            # IRON GATE: Mark that actual execution occurred
                            execution_occurred = True

                            # Forward task_completed to tray for any agent execution —
                            # covers built-in execute tools AND skill-based executions
                            # (e.g. funnelcloud-agents) that carry agent_id + command.
                            is_agent_exec_tool = tool in (
                                "execute",
                                "remote_execute",
                                "remote_execute_all",
                            )
                            if is_agent_exec_tool or (agent_id and cmd):
                                exit_code = result.get("exit_code")
                                is_error = result.get("is_error", False) or (
                                    exit_code is not None and exit_code != 0
                                )
                                _forward_event_to_tray(
                                    kind=(
                                        "task_failed" if is_error else "task_completed"
                                    ),
                                    command=cmd,
                                    agent_id=agent_id,
                                    exit_code=exit_code,
                                    stdout=output,
                                    error_message=(
                                        result.get("error") if is_error else None
                                    ),
                                )

                    elif event_type == "error":
                        await __event_emitter__(
                            create_error_dict(status, LogCategory.FILTER, done=done)
                        )

                    elif event_type == "complete":
                        result = event.get("result", {})
                        final_context = result.get("context")
                        # CRITICAL: Capture orchestrator's final answer
                        # This is the answer we want to use instead of letting
                        # Open-WebUI call Ollama again (which would hallucinate)
                        orchestrator_answer = result.get("answer")
                        if orchestrator_answer:
                            print(
                                f"[aj] Orchestrator complete with answer: {orchestrator_answer[:100]}..."
                            )
                            # Stream the answer so it appears in UI
                            answer_block = f"\n\n{orchestrator_answer}"
                            streamed_content += answer_block
                            await __event_emitter__(
                                {"type": "message", "data": {"content": answer_block}}
                            )
                        # Emit final completion status
                        complete_status = event.get("status", "Done")
                        await __event_emitter__(
                            create_status_dict(
                                complete_status,
                                LogCategory.FILTER,
                                LogLevel.SUCCESS,
                                done=True,
                            )
                        )

                return final_context, streamed_content, execution_occurred

    except Exception as e:
        # httpx timeout exceptions stringify to "" - fall back to the class name
        err = str(e).strip() or type(e).__name__
        print(f"[aj] Orchestrator streaming error: {err}")
        await __event_emitter__(
            create_error_dict(
                f"Orchestrator unreachable: {err[:60]}", LogCategory.FILTER, done=True
            )
        )
        return None, "", False


# ============================================================================
# Content Extraction Layer
# ============================================================================


def _extract_all_content_batch(
    body: dict, messages: List[dict], user_prompt: Optional[str] = None
) -> Tuple[str, List[str], List[dict]]:
    """
    Extract and chunk all files + images in a SINGLE batch request.

    Consolidates what was previously multiple HTTP calls into one.

    Returns:
        Tuple of (file_content_text, filenames, all_chunks)
    """
    batch_items = []
    filenames = []

    # Collect files from body
    files = body.get("files") or []
    if not files:
        files = body.get("metadata", {}).get("files") or []

    for file_info in files:
        try:
            if not isinstance(file_info, dict):
                continue

            file_data = file_info.get("file", file_info)
            file_path = file_data.get("path")
            filename = file_data.get("filename", file_data.get("name", "unknown"))

            if not file_path or not os.path.exists(file_path):
                continue

            with open(file_path, "rb") as f:
                content = f.read()

            ext = os.path.splitext(filename)[1].lower()
            content_type = CONTENT_TYPE_MAP.get(ext, "text/plain")

            if content_type.startswith("text/") or content_type == "application/json":
                content_str = content.decode("utf-8", errors="ignore")
            else:
                content_str = base64.b64encode(content).decode("utf-8")

            batch_items.append(
                {
                    "content": content_str,
                    "content_type": content_type,
                    "source_name": filename,
                    "source_type": "file",
                }
            )
            filenames.append(filename)

        except Exception as e:
            print(f"[aj] Error preparing file for batch: {e}")
            continue

    # Collect images from messages
    for msg in messages:
        content = msg.get("content")
        if not isinstance(content, list):
            continue

        for idx, item in enumerate(content):
            if not isinstance(item, dict) or item.get("type") != "image_url":
                continue

            image_url = item.get("image_url", {})
            url = image_url.get("url", "") if isinstance(image_url, dict) else image_url

            if not url:
                continue

            content_type, image_data = _load_image_url(url)
            if content_type and image_data:
                batch_items.append(
                    {
                        "content": image_data,
                        "content_type": content_type,
                        "source_name": f"image_{idx}",
                        "source_type": "image",
                    }
                )

    # No content to extract
    if not batch_items:
        return "", [], []

    # Single batch call to extractor
    try:
        resp = requests.post(
            f"{EXTRACTOR_API_URL}/api/extract/batch",
            json={
                "items": batch_items,
                "chunk_size": 500,
                "chunk_overlap": 50,
                "prompt": user_prompt,
            },
            timeout=120,
        )
        resp.raise_for_status()
        result = resp.json()

        all_chunks = []
        file_contents = []

        for i, extract_result in enumerate(result.get("results", [])):
            source_name = batch_items[i].get("source_name", "unknown")
            source_type = batch_items[i].get("source_type", "file")

            chunks = extract_result.get("chunks", [])
            for chunk in chunks:
                chunk["source_name"] = source_name
                chunk["source_type"] = source_type
                all_chunks.append(chunk)

            # Build text summary for files (first 3 chunks)
            if source_type == "file" and chunks:
                chunk_texts = [c.get("content", "") for c in chunks[:3]]
                file_contents.append(
                    f"[Document: {source_name}]\n" + "\n\n".join(chunk_texts)
                )

        return "\n\n".join(file_contents), filenames, all_chunks

    except Exception as e:
        print(f"[aj] Batch extraction error: {e}")
        return "", filenames, []


def _extract_images_from_messages(
    messages: List[dict], user_prompt: Optional[str] = None
) -> List[dict]:
    """
    Extract image URLs from message content and generate descriptions.
    DEPRECATED: Use _extract_all_content_batch instead.
    Kept for backwards compatibility.
    """
    image_chunks = []

    for msg in messages:
        content = msg.get("content")
        if not isinstance(content, list):
            continue

        for idx, item in enumerate(content):
            if not isinstance(item, dict):
                continue
            if item.get("type") != "image_url":
                continue

            image_url = item.get("image_url", {})
            if isinstance(image_url, str):
                url = image_url
            else:
                url = image_url.get("url", "")

            if not url:
                continue

            try:
                content_type, image_data = _load_image_url(url)
                if not content_type or not image_data:
                    continue

                chunks = _call_extractor(
                    content=image_data,
                    content_type=content_type,
                    source_name=f"image_{idx}",
                    prompt=user_prompt,
                    overlap=0,
                )

                for chunk in chunks:
                    chunk["source_type"] = "image"
                    chunk["source_name"] = f"uploaded_image_{idx}"
                    image_chunks.append(chunk)

            except Exception as e:
                print(f"[aj] Image {idx} error: {e}")
                continue

    return image_chunks


def _load_image_url(url: str) -> Tuple[Optional[str], Optional[str]]:
    """Load image from various sources (data URL, HTTP, local file)."""

    if url.startswith("data:"):
        match = re.match(r"data:([^;]+);base64,(.+)", url)
        if match:
            return match.group(1), match.group(2)
        return None, None

    if url.startswith("http"):
        try:
            resp = requests.get(url, timeout=30)
            resp.raise_for_status()
            content_type = resp.headers.get("Content-Type", "image/png")
            image_data = base64.b64encode(resp.content).decode("utf-8")
            return content_type, image_data
        except Exception as e:
            print(f"[aj] Failed to fetch {url}: {e}")
            return None, None

    if os.path.exists(url):
        try:
            with open(url, "rb") as f:
                image_data = base64.b64encode(f.read()).decode("utf-8")
            ext = os.path.splitext(url)[1].lower()
            content_type = CONTENT_TYPE_MAP.get(ext, "image/png")
            return content_type, image_data
        except Exception as e:
            print(f"[aj] Failed to read {url}: {e}")
            return None, None

    return None, None


def _extract_and_chunk_file(file_path: str, filename: str) -> List[dict]:
    """Extract text from file and split into chunks."""
    try:
        with open(file_path, "rb") as f:
            content = f.read()

        ext = os.path.splitext(filename)[1].lower()
        content_type = CONTENT_TYPE_MAP.get(ext, "text/plain")

        if content_type.startswith("text/") or content_type == "application/json":
            content_str = content.decode("utf-8", errors="ignore")
        else:
            content_str = base64.b64encode(content).decode("utf-8")

        chunks = _call_extractor(
            content=content_str,
            content_type=content_type,
            source_name=filename,
            overlap=50,
        )

        return chunks

    except Exception as e:
        print(f"[aj] Extractor error for {filename}: {e}")
        try:
            with open(file_path, "r", encoding="utf-8", errors="ignore") as f:
                return [{"content": f.read(), "chunk_index": 0, "chunk_type": "text"}]
        except Exception:
            return []


def _extract_file_contents(body: dict) -> Tuple[str, List[str], List[dict]]:
    """Extract and chunk all uploaded files from request."""
    file_contents = []
    filenames = []
    all_chunks = []

    files = body.get("files") or []
    if not files:
        files = body.get("metadata", {}).get("files") or []

    for file_info in files:
        try:
            if not isinstance(file_info, dict):
                continue

            file_data = file_info.get("file", file_info)
            file_path = file_data.get("path")
            filename = file_data.get("filename", file_data.get("name", "unknown"))

            if not file_path or not os.path.exists(file_path):
                continue

            chunks = _extract_and_chunk_file(file_path, filename)

            if chunks:
                filenames.append(filename)

                for chunk in chunks:
                    chunk["source_name"] = filename
                    all_chunks.append(chunk)

                chunk_texts = [c.get("content", "") for c in chunks[:3]]
                file_contents.append(
                    f"[Document: {filename}]\n" + "\n\n".join(chunk_texts)
                )

        except Exception as e:
            print(f"[aj] Error processing file: {e}")
            continue

    return "\n\n".join(file_contents), filenames, all_chunks


def _call_extractor(
    content: str,
    content_type: str,
    source_name: Optional[str],
    prompt: Optional[str] = None,
    overlap: int = 50,
) -> List[dict]:
    """Call extractor service to chunk content."""
    try:
        resp = requests.post(
            f"{EXTRACTOR_API_URL}/api/extract/",
            json={
                "content": content,
                "content_type": content_type,
                "source_name": source_name,
                "chunk_size": 500,
                "chunk_overlap": overlap,
                "prompt": prompt,
            },
            timeout=120,
        )
        resp.raise_for_status()
        result = resp.json()
        return result.get("chunks", [])

    except Exception as e:
        print(f"[aj] Extractor error: {e}")
        return []


# ============================================================================
# Memory Layer
# ============================================================================


async def _save_chunk_to_memory(
    user_id: str,
    chunk: dict,
    model: str,
    metadata: dict,
    source_name: str,
) -> bool:
    """Save a single chunk to memory service."""
    try:
        chunk_content = chunk.get("content", "")
        chunk_idx = chunk.get("chunk_index", 0)
        section = chunk.get("section_title", "")
        chunk_type = chunk.get("source_type", "document_chunk")

        if not chunk_content.strip():
            return False

        payload = {
            "user_id": user_id,
            "messages": [{"role": "user", "content": chunk_content}],
            "model": model,
            "metadata": {
                **metadata,
                "chunk_index": chunk_idx,
                "section_title": section,
            },
            "source_type": chunk_type,
            "source_name": f"{source_name}#{chunk_idx}"
            + (f" ({section})" if section else ""),
            "skip_classifier": True,
        }

        resp = requests.post(
            f"{MEMORY_API_URL}/api/memory/save", json=payload, timeout=30
        )
        return resp.status_code == 200

    except Exception as e:
        print(f"[aj] Failed to save chunk: {e}")
        return False


async def _search_memory(
    user_id: str,
    query_text: str,
    top_k: int = 5,
) -> List[dict]:
    """Search memory for relevant context."""
    try:
        payload = {
            "user_id": user_id,
            "query_text": query_text,
            "top_k": top_k,
        }

        resp = requests.post(
            f"{MEMORY_API_URL}/api/memory/search", json=payload, timeout=10
        )

        if resp.status_code == 404:
            # No memories found - not an error
            return []

        resp.raise_for_status()
        return resp.json()

    except Exception as e:
        print(f"[aj] Search error: {e}")
        return []


async def _save_to_memory(
    user_id: str,
    messages: List[dict],
    source_type: Optional[str] = None,
    source_name: Optional[str] = None,
    workspace_context: Optional[str] = None,
) -> bool:
    """Save conversation to memory with fact extraction. Returns True if saved successfully."""
    try:
        # Extract user text for fact extraction
        user_text = None
        for msg in reversed(messages):
            if msg.get("role") == "user":
                content = msg.get("content", "")
                if isinstance(content, str):
                    user_text = content
                elif isinstance(content, list):
                    user_text = " ".join(
                        item.get("text", "")
                        for item in content
                        if isinstance(item, dict) and item.get("type") == "text"
                    )
                break

        # Extract facts via the orchestrator (LLM-based, schema-enforced)
        facts = []
        if user_text and len(user_text.strip()) > 10:
            try:
                print(
                    f"[aj] Calling orchestrator at {ORCHESTRATOR_API_URL}/api/orchestrate/extract-facts"
                )
                resp = requests.post(
                    f"{ORCHESTRATOR_API_URL}/api/orchestrate/extract-facts",
                    json={"text": user_text},
                    timeout=120,
                )
                print(f"[aj] Fact extraction responded: status={resp.status_code}")
                if resp.status_code == 200:
                    resp_json = resp.json()
                    print(f"[aj] Fact extraction response body: {resp_json}")
                    facts = resp_json.get("facts", [])
                    if facts:
                        print(f"[aj] Extracted {len(facts)} facts for memory")
                    else:
                        print(f"[aj] No facts found, skipping memory save")
                        return False  # Nothing worth saving
                else:
                    print(
                        f"[aj] Fact extraction error: {resp.status_code} {resp.text[:200]}"
                    )
                    return False  # Don't save verbatim on extraction failure
            except Exception as e:
                print(f"[aj] Fact extraction failed: {type(e).__name__}: {e}")
                return False  # Don't save verbatim on extraction failure
        else:
            print(
                f"[aj] Text too short for fact extraction ({len(user_text.strip()) if user_text else 0} chars), skipping"
            )
            return False

        payload = {
            "user_id": user_id,
            "messages": messages,
            "facts": facts if facts else None,
            "workspace_context": workspace_context,
            "source_type": source_type,
            "source_name": source_name,
        }

        resp = requests.post(
            f"{MEMORY_API_URL}/api/memory/save", json=payload, timeout=30
        )
        resp.raise_for_status()

        result = resp.json()
        return result.get("status") == "saved"

    except Exception as e:
        print(f"[aj] Save error: {e}")
        return False


def _extract_user_text_prompt(messages: List[dict]) -> Optional[str]:
    """Extract user's text prompt from last user message."""
    if not messages:
        return None

    for msg in reversed(messages):
        if msg.get("role") != "user":
            continue

        content = msg.get("content")

        if isinstance(content, str):
            return content

        if isinstance(content, list):
            text_parts = []

            for item in content:
                if isinstance(item, dict) and item.get("type") == "text":
                    text_parts.append(item.get("text", ""))

            return " ".join(text_parts) if text_parts else None

    return None


# ============================================================================
# Context Formatting
# ============================================================================


def _format_source(ctx: dict) -> str:
    """Format a memory source as a brief status line with icon."""
    source_type = ctx.get("source_type")
    source_name = ctx.get("source_name")

    if source_type == "document" and source_name:
        return f"📄 {source_name}"

    if source_type == "url" and source_name:
        display = (source_name[:30] + "...") if len(source_name) > 30 else source_name
        return f"🔗 {display}"

    if source_type == "image":
        return f"🖼 {source_name or 'image'}"

    if source_type == "prompt" and source_name:
        snippet = source_name[:40].replace("\n", " ")
        if len(source_name) > 40:
            snippet += "..."
        return f'💬 "{snippet}"'

    user_text = ctx.get("user_text", "")
    if user_text:
        snippet = user_text[:60].replace("\n", " ")
        if len(user_text) > 60:
            snippet += "..."
        return f'💬 "{snippet}"'

    return "📝 memory"


# ============================================================================
# Filter Plugin Class
# ============================================================================


class Filter:
    """
    AJ - Agentic Assistant Filter for Open-WebUI.

    Provides:
      - Intent classification (task vs casual - 2 class)
      - Multi-step reasoning via orchestrator
      - Remote command execution via FunnelCloud agents
      - Semantic memory retrieval and storage (automatic)
      - Document/image extraction
    """

    class UserValves(BaseModel):
        """User configuration for AJ."""

        enable_orchestrator: bool = Field(
            default=True, description="Enable multi-step reasoning for task intents"
        )
        enable_code_execution: bool = Field(
            default=False, description="Allow code execution (requires explicit enable)"
        )
        max_context_items: int = Field(
            default=5, description="Maximum memory items to inject as context"
        )
        workspace_root: str = Field(
            default="/workspace",
            description="Workspace root inside container. "
            "Maps to HOST_WORKSPACE_PATH on host machine via docker-compose volume.",
        )
        host_workspace_hint: str = Field(
            default="C:/Code/aj.westerfield.cloud",
            description="Display hint: what HOST_WORKSPACE_PATH is set to on host. "
            "This is informational only - actual mapping is in docker-compose.",
        )

    def __init__(self):
        """Initialize AJ filter."""
        self.user_valves = self.UserValves()
        self.toggle = True
        # Track streamed content from orchestrator to preserve in final response
        self._streamed_content = ""
        # IRON GATE: Track execution state to catch hallucinated outputs
        self._execution_occurred = False
        self._task_intent = False  # True when intent classification returned "task"
        self._last_user_text = ""
        # Butler icon - person with bow tie silhouette
        self.icon = (
            "data:image/svg+xml;base64,"
            "PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAyNCAyNCIgZmlsbD0iY3VycmVudENvbG9yIj4KICA8IS0tIEhlYWQgLS0+CiAgPGNpcmNsZSBjeD0iMTIiIGN5PSI2IiByPSIzLjUiLz4KICA8IS0tIEJvdyB0aWUgLS0+CiAgPHBhdGggZD0iTTkgMTJsLTIgMS41TDkgMTVoNmwyLTEuNUwxNSAxMkg5eiIvPgogIDwhLS0gQm9keSAtLT4KICA8cGF0aCBkPSJNNiAxNWMwLTEgMS41LTIgNi0yIDQuNSAwIDYgMSA2IDJ2NmMwIC41LS41IDEtMSAxSDdjLS41IDAtMS0uNS0xLTF2LTZ6Ii8+Cjwvc3ZnPgo="
        )

    def _sanitize_conversation_history(self, messages: List[dict]) -> List[dict]:
        """
        Sanitize conversation history to remove leaked tokens and prevent loops.

        Cleans:
        - Chat template tokens (<|im_start, <|im_end, etc.)
        - Repeated/looping content from prior assistant messages
        - Internal context markers that shouldn't persist
        """
        import re

        # Patterns to remove from conversation history
        toxic_patterns = [
            r"<\|im_start[^>]*>?",
            r"<\|im_end[^>]*>?",
            r"<\|endoftext\|>",
            r"<\|assistant\|>",
            r"<\|user\|>",
            r"<\|system\|>",
            r"### Action Log ###",
            r"### End Action Log ###",
            r"### End Result ###",
            r"⚠️ SUMMARIZATION:.*?(?=\n\n|\Z)",
            r"YOUR TASK:.*?(?=\n\n|\Z)",
        ]

        sanitized = []
        for msg in messages:
            msg_copy = dict(msg)
            content = msg_copy.get("content", "")

            if isinstance(content, str) and content:
                # Apply all sanitization patterns
                for pattern in toxic_patterns:
                    content = re.sub(
                        pattern, "", content, flags=re.DOTALL | re.IGNORECASE
                    )

                # Truncate at conversation loop markers
                loop_markers = ["\nuser\n", "\nassistant\n", "\nuser:", "\nassistant:"]
                for marker in loop_markers:
                    idx = content.lower().find(marker)
                    if idx > 50:  # Only if we have content before
                        content = content[:idx].rstrip()
                        break

                # Clean up whitespace
                content = re.sub(r"\n{4,}", "\n\n\n", content)
                msg_copy["content"] = content.strip()

            sanitized.append(msg_copy)

        return sanitized

    def _inject_context(
        self,
        body: dict,
        context_items: List[dict],
        orchestrator_context: Optional[str] = None,
        intent: str = "casual",
    ) -> dict:
        """Inject system prompt, retrieved memories, and orchestrator analysis into conversation.

        Also injects contextType signal for Manchurian Candidate behavior:
        - contextType: external (casual intent) → conversational response
        - contextType: internal (task intent) → structured JSON response
        """
        messages = body.get("messages", [])

        # Determine contextType based on intent
        # Task intent goes through orchestrator which needs JSON, others are direct user chat
        context_type = "internal" if intent == "task" else "external"

        # Inject AJ system prompt if not already present
        has_aj_system = any(
            m.get("role") == "system" and "AJ" in m.get("content", "") for m in messages
        )

        if not has_aj_system:
            # Add AJ system prompt with contextType signal at the beginning
            system_content = f"contextType: {context_type}\n\n{AJ_SYSTEM_PROMPT}"
            aj_system = {"role": "system", "content": system_content}
            # Insert after any existing system messages, or at start
            insert_idx = 0
            for i, m in enumerate(messages):
                if m.get("role") == "system":
                    insert_idx = i + 1
                else:
                    break
            messages.insert(insert_idx, aj_system)
        else:
            # System prompt exists, prepend contextType to it
            for m in messages:
                if m.get("role") == "system" and "AJ" in m.get("content", ""):
                    existing_content = m.get("content", "")
                    if "contextType:" not in existing_content:
                        m["content"] = (
                            f"contextType: {context_type}\n\n{existing_content}"
                        )
                    break

        if not context_items and not orchestrator_context:
            body["messages"] = messages
            return body

        # Build context block
        context_lines = []

        # Add orchestrator analysis first
        if orchestrator_context:
            context_lines.append(orchestrator_context)

        # Add memory context
        if context_items:
            context_lines.append("### Retrieved Memories ###")
            for ctx in context_items[: self.user_valves.max_context_items]:
                user_text = ctx.get("user_text")
                facts = ctx.get("facts")

                if user_text:
                    context_lines.append(f"- {user_text}")

                    # Show facts that explain why this memory was retrieved
                    if facts and isinstance(facts, dict):
                        fact_parts = []
                        for fact_type, fact_value in facts.items():
                            if fact_value:
                                # Format: "names: Ian" or "preferences: dark mode"
                                if isinstance(fact_value, list):
                                    fact_parts.append(
                                        f"{fact_type}: {', '.join(str(v) for v in fact_value)}"
                                    )
                                else:
                                    fact_parts.append(f"{fact_type}: {fact_value}")
                        if fact_parts:
                            context_lines.append(f"  📌 Facts: {'; '.join(fact_parts)}")
            context_lines.append("### End Memories ###\n")

        if not context_lines:
            body["messages"] = messages
            return body

        context_block = "\n".join(context_lines) + "\n"

        # Find last user message and prepend context
        for i in range(len(messages) - 1, -1, -1):
            if messages[i].get("role") == "user":
                content = messages[i].get("content", "")

                if isinstance(content, str):
                    messages[i]["content"] = context_block + content
                elif isinstance(content, list):
                    messages[i]["content"] = [
                        {"type": "text", "text": context_block}
                    ] + content
                break

        body["messages"] = messages
        return body

    async def inlet(
        self, body: dict, __event_emitter__, __user__: Optional[dict] = None
    ) -> dict:
        """
        Main filter entry point.

        Process:
          1. Extract uploaded files/images
          2. Classify intent
          3. For tasks: engage orchestrator
          4. Search/save to memory
          5. Inject context
        """
        try:
            user_id = __user__["id"] if __user__ and "id" in __user__ else "anonymous"
            messages = body.get("messages", [])
            model = body.get("model", "unknown")
            metadata = body.get("metadata", {})

            if not messages or not isinstance(messages, list):
                return body

            # CRITICAL: Sanitize conversation history before processing
            # Remove any leaked chat template tokens from prior messages
            messages = self._sanitize_conversation_history(messages)
            body["messages"] = messages

            await __event_emitter__(
                create_status_dict("Thinking", LogCategory.FILTER, LogLevel.THINKING)
            )

            # Extract user text for classification
            user_text = _extract_user_text_prompt(messages)

            # IRON GATE: Reset execution tracking for new request
            self._execution_occurred = False
            self._task_intent = False  # Will be set after intent classification
            self._last_user_text = user_text or ""

            # Extract files and images in a SINGLE batch call
            file_content, filenames, chunks = _extract_all_content_batch(
                body, messages, user_prompt=user_text
            )

            # Build immediate image context for injection
            immediate_image_context = [
                {
                    "user_text": f"[Image]: {chunk.get('content', '')}",
                    "source_type": "image",
                    "source_name": chunk.get("source_name", "image"),
                }
                for chunk in chunks
                if chunk.get("source_type") == "image" and chunk.get("content")
            ]

            # Save document chunks
            chunks_saved = 0
            if chunks:
                await __event_emitter__(
                    create_status_dict(
                        f"Saving {len(chunks)} chunk(s)",
                        LogCategory.MEMORY,
                        LogLevel.PROCESSING,
                    )
                )

                source_name = filenames[0] if filenames else "attachment"
                for chunk in chunks:
                    if await _save_chunk_to_memory(
                        user_id, chunk, model, metadata, source_name
                    ):
                        chunks_saved += 1

            # Classify intent FIRST - this determines if we save
            orchestrator_context = None
            intent_result = {"intent": "casual", "confidence": 0.5}
            intent = "casual"

            if user_text:
                intent_result = _classify_intent(user_text)
                intent = intent_result.get("intent", "casual")
                confidence = intent_result.get("confidence", 0.5)

                # SMART TASK CONTINUATION DETECTION
                # Check if user is confirming/continuing a previous task proposal
                if intent == "casual":
                    should_upgrade = _detect_task_continuation(
                        user_text, messages, confidence
                    )
                    if should_upgrade:
                        print(
                            f"[aj] Upgrading intent from casual to task (continuation detected)"
                        )
                        intent = "task"

                # IRON GATE: Track task intent for hallucination detection
                # This replaces regex-based execution request detection
                self._task_intent = intent == "task"

            # Search memory for context (needed for tasks and recall)
            context = []
            if user_text:
                search_results = await _search_memory(user_id, user_text)
                # Convert search results to context format
                context = [
                    {
                        "user_text": r.get("user_text", ""),
                        "score": r.get("score", 0),
                        "source_type": r.get("source_type", "prompt"),
                        "source_name": r.get("source_name"),
                    }
                    for r in search_results
                ]

            # Save to memory (extracts facts via orchestrator LLM, stores in Qdrant)
            # Do this for all messages - fact extraction decides what's worth storing
            if user_text and len(user_text.strip()) > 5:
                saved = await _save_to_memory(
                    user_id=user_id,
                    messages=messages,
                    source_type="prompt",
                    source_name=model,
                )
                if saved:
                    await __event_emitter__(
                        create_status_dict(
                            "Saved to memory",
                            LogCategory.MEMORY,
                            LogLevel.SAVED,
                            done=True,
                        )
                    )
                    print(f"[aj] Memory saved for user={user_id}")

            # ALWAYS engage orchestrator - DeepSeek handles both tasks AND conversations
            # The Llama chat model is just a formatting layer, not the brain
            # Orchestrator has its own conversational handler for non-task queries
            if self.user_valves.enable_orchestrator:
                orchestrator_context, streamed, exec_occurred = await _orchestrate_task(
                    user_id,
                    messages,
                    (
                        self.user_valves.workspace_root
                        if self.user_valves.workspace_root
                        else None
                    ),
                    __event_emitter__,
                    memory_context=context,  # Pass user context (name, preferences)
                    model=model,  # Pass selected model from Open-WebUI
                )
                # Store streamed content to prepend to LLM response in outlet
                self._streamed_content = streamed
                # IRON GATE: Track whether execution actually occurred
                self._execution_occurred = exec_occurred

            # Merge immediate image context
            if immediate_image_context:
                context = immediate_image_context + context

            # Determine if we have context to inject
            has_context = bool(context) or bool(orchestrator_context)

            # Emit status and inject
            # Note: orchestrator already emitted completion status, so skip for tasks
            if has_context:
                memory_count = len(context) if context else 0

                if memory_count > 0:
                    # Extract fact summaries from context for display
                    # Facts can be: string "type: value\ntype2: value2", dict, or None
                    fact_summaries = []
                    for ctx_item in (context or [])[:3]:  # Show up to 3 memory items
                        facts = ctx_item.get("facts")
                        if facts:
                            if isinstance(facts, str):
                                # String format: "type: value\ntype2: value2"
                                first_line = facts.split("\n")[0][:40]
                                fact_summaries.append(first_line)
                            elif isinstance(facts, dict):
                                # Dict format: {"type": "value"}
                                for key in list(facts.keys())[:1]:
                                    fact_summaries.append(
                                        f"{key}: {str(facts[key])[:30]}"
                                    )

                    if fact_summaries:
                        fact_summary = "; ".join(fact_summaries)
                        await __event_emitter__(
                            create_status_dict(
                                f"Found {memory_count} memories ({fact_summary})",
                                LogCategory.MEMORY,
                                LogLevel.CONTEXT,
                                done=True,  # Close status to prevent stuck display
                            )
                        )
                    else:
                        await __event_emitter__(
                            create_status_dict(
                                f"Found {memory_count} memories",
                                LogCategory.MEMORY,
                                LogLevel.CONTEXT,
                                done=True,  # Close status to prevent stuck display
                            )
                        )

                # Only emit Ready if we didn't go through orchestrator
                if not orchestrator_context:
                    await __event_emitter__(
                        create_status_dict(
                            "Ready", LogCategory.FILTER, LogLevel.READY, done=True
                        )
                    )

                body = self._inject_context(
                    body, context or [], orchestrator_context, intent=intent
                )

            else:
                await __event_emitter__(
                    create_status_dict(
                        "Ready",
                        LogCategory.FILTER,
                        LogLevel.READY,
                        done=True,
                        hidden=True,
                    )
                )
                # Still inject contextType even without memory context
                body = self._inject_context(body, [], None, intent=intent)

            print(
                f"[aj] user={user_id} intent={intent} context={len(context or [])} chunks={chunks_saved} contextType={'internal' if intent == 'task' else 'external'}"
            )
            return body

        except Exception as e:
            print(f"[aj] Error: {e}")

            await __event_emitter__(
                create_error_dict(str(e)[:40], LogCategory.FILTER, done=True)
            )

            return body

    async def outlet(
        self, body: dict, __event_emitter__, __user__: Optional[dict] = None
    ) -> dict:
        """
        Post-response hook. Formats assistant responses for consistency.

        Ensures:
        - JSON structured responses are extracted to just the "response" field
        - Streamed thinking/results are preserved before LLM response
        - User message echoes are removed (model confusion)
        - File/folder names are in `backticks`
        - Code references are in `backticks`
        - Lists and code blocks use fenced markdown
        """
        import re

        messages = body.get("messages", [])
        if not messages:
            return body

        # Get last user message to detect echo
        last_user_msg = None
        for msg in reversed(messages):
            if msg.get("role") == "user":
                user_content = msg.get("content", "")
                if isinstance(user_content, str):
                    last_user_msg = user_content.strip()
                elif isinstance(user_content, list):
                    # Extract text from multi-modal content
                    last_user_msg = " ".join(
                        item.get("text", "")
                        for item in user_content
                        if isinstance(item, dict) and item.get("type") == "text"
                    ).strip()
                break

        # Find last assistant message and process it
        for i in range(len(messages) - 1, -1, -1):
            if messages[i].get("role") == "assistant":
                content = messages[i].get("content", "")
                if isinstance(content, str):
                    # FIRST: Extract response from JSON structured output
                    content = self._extract_json_response(content)

                    # ================================================================
                    # IRON GATE: Block fabricated CLI output
                    # ================================================================

                    # Case 1: Task intent but orchestrator didn't execute at all
                    if (
                        self._task_intent
                        and not self._execution_occurred
                        and _contains_fabricated_cli_output(content)
                    ):
                        print(
                            f"[aj] IRON GATE BLOCKED: Fabricated CLI output detected. "
                            f"Intent was 'task' but orchestrator did not execute."
                        )
                        content = (
                            "⚠️ **Execution Failed**\n\n"
                            "I was unable to execute the command on the remote agent. "
                            "The orchestrator service may be unavailable or the agent "
                            "is unreachable.\n\n"
                            "Please check:\n"
                            "- Is the orchestrator service running?\n"
                            "- Is the FunnelCloud agent online?\n"
                            "- Network connectivity between services\n\n"
                            "Run `docker-compose ps` to check service status."
                        )
                        messages[i]["content"] = content
                        break

                    # Case 2: Execution occurred but LLM added MORE fabricated output
                    # This catches the pattern where real output was shown, then the
                    # model hallucinates additional CLI-style content
                    if self._execution_occurred and _contains_fabricated_cli_output(
                        content
                    ):
                        print(
                            f"[aj] IRON GATE: Stripping post-execution fabricated CLI output"
                        )
                        content = self._strip_fabricated_cli_blocks(content)

                    # CRITICAL: Detect user message echo (model confusion)
                    # If the assistant's response contains the user's exact message, remove it
                    if last_user_msg and len(last_user_msg) > 5:
                        if last_user_msg in content:
                            print(f"[aj] WARNING: Model echoed user message, removing")
                            content = content.replace(last_user_msg, "").strip()
                            # Also remove common echo patterns like "User: ..."
                            content = re.sub(
                                rf"(?:User|Human|Question):\s*{re.escape(last_user_msg)}",
                                "",
                                content,
                                flags=re.IGNORECASE,
                            )

                    # Handle orchestrator streamed content
                    if self._streamed_content:
                        # Clean streamed content - remove raw JSON planning output
                        cleaned_streamed = self._clean_streamed_content(
                            self._streamed_content
                        )
                        if cleaned_streamed.strip():
                            # CRITICAL FIX: If orchestrator executed and returned content,
                            # USE ONLY THE ORCHESTRATOR'S RESPONSE.
                            # The model's response (content) is redundant/hallucinated
                            # since orchestrator already used the same 70B model.
                            if self._execution_occurred or self._task_intent:
                                # Orchestrator handled this - use its output only
                                print(
                                    f"[aj] Using orchestrator response, discarding model output"
                                )
                                content = cleaned_streamed
                            else:
                                # Non-task: prepend context but keep model response
                                content = cleaned_streamed + "\n\n---\n\n" + content
                        # Clear for next request
                        self._streamed_content = ""
                    # Pass casual_intent flag to format response
                    # Casual intent shows Final Thought box, task intent strips think tags inline
                    casual_intent = not self._task_intent
                    messages[i]["content"] = self._format_response(
                        content, casual_intent=casual_intent
                    )
                break

        body["messages"] = messages
        return body

    def _clean_streamed_content(self, text: str) -> str:
        """
        Clean streamed content from orchestrator, formatting raw JSON plans.

        Keeps:
        - Tool output in code blocks
        - Natural language thinking
        - Formatted plans (converted from JSON)

        Converts:
        - Raw JSON plan objects → numbered blockquote steps

        Removes:
        - Follow-up suggestion blocks
        - Orphaned JSON fragments
        """
        import re

        if not text:
            return text

        # First, try to extract and format any JSON plans
        # Pattern to match {"step": N, "action": "text"}
        step_pattern = r'\{\s*"step"\s*:\s*(\d+)\s*,\s*"action"\s*:\s*"([^"]+)"\s*\}'
        matches = re.findall(step_pattern, text)

        if matches:
            # Format matched steps as a clean list
            formatted_steps = []
            for step_num, action in matches:
                formatted_steps.append(f"{step_num}. {action}")

            # Remove the raw JSON and add formatted version
            text = re.sub(
                r'\{\s*"step"\s*:\s*\d+\s*,\s*"action"\s*:\s*"[^"]*"\s*\}[,\s]*',
                "",
                text,
            )

            # If we had steps, prepend the formatted plan
            if formatted_steps and not any(f"{s[0]}." in text for s in matches):
                text = "💭 **Plan:**\n" + "\n".join(formatted_steps) + "\n\n" + text

        # Remove plan arrays (already extracted content above)
        plan_array_pattern = r'\[\s*\{\s*"step"\s*:.*?\}\s*\]'
        text = re.sub(plan_array_pattern, "", text, flags=re.DOTALL)

        # Remove "requires_confirmation" JSON fragments
        text = re.sub(r'"requires_confirmation"\s*:\s*(true|false)\s*\}?', "", text)

        # Remove "Follow up" sections with suggestions
        text = re.sub(r"Follow up\s*\n.*?(?=\n\n|\Z)", "", text, flags=re.DOTALL)

        # Remove orphaned JSON-like fragments
        text = re.sub(r"\{\s*\}", "", text)  # Empty braces
        text = re.sub(r"\[\s*\]", "", text)  # Empty brackets
        text = re.sub(
            r"^\s*\],?\s*$", "", text, flags=re.MULTILINE
        )  # Orphaned array close
        text = re.sub(
            r"^\s*\},?\s*$", "", text, flags=re.MULTILINE
        )  # Orphaned object close
        text = re.sub(r'"action"\s*:\s*"[^"]*"', "", text)  # Orphaned action fields

        # Clean up excessive whitespace
        text = re.sub(r"\n{3,}", "\n\n", text)
        text = text.strip()

        return text

    def _extract_json_response(self, text: str) -> str:
        """
        Extract the response content from JSON structured output.

        If the LLM outputs structured JSON like:
        {"action": "casual", "response": "Hi there!", "guardrail_context": "..."}
        {"action": "provide_markdown", "markdown": "..."}
        {"action": "concepts", "answer": "...", "data": {...}}

        This extracts the content and formats any nested data as markdown.
        """
        import re

        # Skip if empty or doesn't look like JSON
        if not text or not text.strip().startswith("{"):
            return text

        try:
            # Try to parse as JSON
            data = json.loads(text.strip())

            if not isinstance(data, dict):
                return text

            # Fields that contain the main response text
            response_fields = [
                "response",
                "markdown",
                "output",
                "content",
                "text",
                "answer",
            ]
            # Fields that are metadata/internal (to skip)
            skip_fields = [
                "action",
                "guardrail_context",
                "requires_confirmation",
                "type",
            ]

            # Find the main response content
            main_response = ""
            for field in response_fields:
                if field in data and data[field]:
                    main_response = str(data[field])
                    break

            # Collect additional data fields (not response or skip fields)
            additional_data = {}
            for key, value in data.items():
                if key not in response_fields and key not in skip_fields and value:
                    additional_data[key] = value

            # If we have additional structured data, format it as markdown
            if additional_data:
                parts = [main_response] if main_response else []

                for key, value in additional_data.items():
                    if isinstance(value, dict):
                        # Format dict as a list or table
                        readable_key = key.replace("_", " ").title()
                        parts.append(f"\n**{readable_key}:**")
                        for k, v in value.items():
                            parts.append(f"- **{k}**: {v}")
                    elif isinstance(value, list):
                        readable_key = key.replace("_", " ").title()
                        parts.append(f"\n**{readable_key}:**")
                        for item in value:
                            if isinstance(item, dict):
                                # Format dict items
                                item_parts = [f"{k}: {v}" for k, v in item.items()]
                                parts.append(f"- {', '.join(item_parts)}")
                            else:
                                parts.append(f"- {item}")
                    elif isinstance(value, str):
                        # Include string fields as paragraphs
                        readable_key = key.replace("_", " ").title()
                        parts.append(f"\n**{readable_key}:** {value}")

                return "\n".join(parts)

            # Just main response, no additional data
            if main_response:
                return main_response

            # Not our structured format, return as-is
            return text

        except json.JSONDecodeError:
            # Not valid JSON, might be partial or mixed content
            # Try to extract content fields with regex as fallback
            for field in ["response", "markdown", "output", "content"]:
                match = re.search(rf'"{field}"\s*:\s*"((?:[^"\\]|\\.)*)"\s*[,}}]', text)
                if match:
                    # Unescape the JSON string
                    try:
                        return json.loads('"' + match.group(1) + '"')
                    except:
                        return match.group(1)

            return text

    def _strip_fabricated_cli_blocks(self, text: str) -> str:
        """
        IRON GATE: Strip fabricated CLI output from LLM response.

        When execution occurred (real output was streamed), the LLM sometimes
        still adds fabricated CLI-style content. This method removes:
        - Code blocks containing fabricated ping/ipconfig patterns
        - Inline code blocks with fabricated network output

        Preserves:
        - Natural language commentary
        - Code blocks that don't match fabrication signatures
        """
        if not text:
            return text

        import re

        # Pattern to match fenced code blocks
        code_block_pattern = r"```(?:\w*\n)?(.*?)```"

        def is_fabricated_block(block_content: str) -> bool:
            """Check if a code block contains fabricated CLI output."""
            # Check against fabrication signatures
            match_count = 0
            for pattern in FABRICATED_CLI_OUTPUT_SIGNATURES:
                if re.search(pattern, block_content, re.IGNORECASE):
                    match_count += 1
                    if match_count >= 2:
                        return True
            return False

        # Find and evaluate each code block
        def replace_fabricated(match):
            block_content = match.group(1)
            if is_fabricated_block(block_content):
                print(f"[aj] IRON GATE: Removed fabricated code block")
                return ""  # Remove the entire block
            return match.group(0)  # Keep original

        cleaned = re.sub(code_block_pattern, replace_fabricated, text, flags=re.DOTALL)

        # Also check for inline fabricated content outside code blocks
        # Pattern: looks like command output but not in a code block
        # e.g., "64 bytes from 142.250.185.142: icmp_seq=1 ttl=116 time=8.47 ms"
        inline_ping_pattern = (
            r"\n\d+\s+bytes\s+from\s+[\d.]+.*?(?:icmp_seq|time=).*(?:\n|$)"
        )
        if re.search(inline_ping_pattern, cleaned, re.IGNORECASE):
            # Remove inline fabricated ping output
            cleaned = re.sub(inline_ping_pattern, "\n", cleaned, flags=re.IGNORECASE)
            print(f"[aj] IRON GATE: Removed inline fabricated ping output")

        # Clean up any double newlines left behind
        cleaned = re.sub(r"\n{3,}", "\n\n", cleaned)

        return cleaned.strip()

    def _format_response(self, text: str, casual_intent: bool = False) -> str:
        """
        Format response text for better markdown rendering.

        - Remove leaked internal markers/instructions
        - Remove leaked tool call syntax (LLM hallucinations)
        - Remove raw JSON tool calls that leaked through
        - Remove partial/orphaned JSON fragments
        - Remove leaked chat template tokens
        - Remove echoed user messages (model confusion)
        - Wrap file/folder names in backticks
        - Wrap code references in backticks
        - Ensure code blocks are fenced
        - For casual intents: Format <think> content as Final Thought summary

        Args:
            text: The response text to format
            casual_intent: If True, format <think> content as a Final Thought box.
                          If False, strip think tags but keep content inline.
        """
        import re

        # Skip if empty
        if not text:
            return text

        # CRITICAL: Remove leaked chat template tokens FIRST
        # These indicate the model is confused and trying to generate conversation structure
        # Patterns: <|im_start, <|im_end, <|im_start|>, etc.
        chat_template_patterns = [
            r"<\|im_start[^>]*>?",  # <|im_start or <|im_start|> or <|im_start\n
            r"<\|im_end[^>]*>?",  # <|im_end or <|im_end|>
            r"<\|endoftext\|>",  # End of text token
            r"<\|assistant\|>",  # Role markers
            r"<\|user\|>",
            r"<\|system\|>",
        ]
        for pattern in chat_template_patterns:
            text = re.sub(pattern, "", text, flags=re.IGNORECASE)

        # CRITICAL: Detect model regurgitating system prompt template content
        # DeepSeek-R1 and similar models sometimes output prompt instructions
        template_regurgitation_markers = [
            "## Task Management with AJ",
            "### Getting Started",
            "**Classify Intent**:",
            "Determine if your request falls under",
            "**Utilize Memory Management**:",
            "**Run Remote Commands**:",
            "**Intent Routing**:",
            "**Output Contract**:",
            "**Do not Fabricate Data**:",
            "With practice and experimentation, you'll become proficient",
        ]

        for marker in template_regurgitation_markers:
            if marker in text:
                # Model is regurgitating template content - this is broken
                # Try to extract any valid content after the template garbage
                print(f"[aj] WARNING: Model regurgitating template content, cleaning")

                # Remove the entire "Task Management" block if present
                text = re.sub(
                    r"#+\s*Task Management with AJ.*?(?=\n\n[^#\-\*\d]|\Z)",
                    "",
                    text,
                    flags=re.DOTALL | re.IGNORECASE,
                )

                # Remove numbered list items that look like instructions
                text = re.sub(
                    r"\d+\.\s*\*\*(?:Classify Intent|Utilize Memory|Run Remote|Intent Routing|Output Contract|Do not Fabricate)\*\*[^\n]*\n?",
                    "",
                    text,
                    flags=re.IGNORECASE,
                )
                break

        # Detect and truncate at conversation loop
        # If model starts outputting "user\n" or "assistant\n" (role markers), truncate there
        loop_markers = [
            "\nuser\n",
            "\nassistant\n",
            "\nuser:",
            "\nassistant:",
        ]
        for marker in loop_markers:
            if marker in text.lower():
                # Find the marker and truncate
                idx = text.lower().find(marker)
                if idx > 100:  # Only truncate if we have substantial content before
                    text = text[:idx].rstrip()
                    break

        # SANITIZATION: Remove leaked internal markers and instructions
        # These are internal prompts the model should follow, not output

        # First, remove SUMMARIZATION instruction blocks (can be multi-line)
        # Match from ⚠️ SUMMARIZATION: until double newline or end
        text = re.sub(
            r"⚠️ SUMMARIZATION:.*?(?=\n\n[A-Z]|\n\n-|\Z)", "", text, flags=re.DOTALL
        )

        # ================================================================
        # FINAL THOUGHT: Handle <think> content based on intent
        # ================================================================
        # For casual intents: Show thinking as a "Final Thought" summary box
        # For task intents: Strip tags, keep content inline (no summary box)

        final_thought_content = None
        if casual_intent:
            # Extract think content for Final Thought formatting
            think_match = re.search(
                r"<think>(.*?)</think>", text, flags=re.DOTALL | re.IGNORECASE
            )
            if think_match:
                final_thought_content = think_match.group(1).strip()
                # Remove the think block from main text - we'll add it back formatted
                text = re.sub(
                    r"<think>.*?</think>", "", text, flags=re.DOTALL | re.IGNORECASE
                )

        # Remove various internal markers (can appear anywhere, not just at boundaries)
        internal_markers = [
            r"### End Result ###",
            r"### End Action Log ###",
            r"### Action Log ###",
            r"### End Memories ###",
            r"### Task Plan ###",
            r"<think>",
            r"</think>",
            r"</context>",
            r"<context>",
        ]
        for pattern in internal_markers:
            text = re.sub(
                re.escape(pattern) if not pattern.startswith("<") else pattern,
                "",
                text,
                flags=re.IGNORECASE,
            )

        # Remove Retrieved Memories blocks entirely (including content)
        # Match the entire block from ### Retrieved Memories ### through ### End Memories ###
        text = re.sub(
            r"### Retrieved Memories ###.*?### End Memories ###",
            "",
            text,
            flags=re.DOTALL | re.IGNORECASE,
        )
        # Also catch orphaned markers (when block is empty or malformed)
        text = re.sub(r"###\s*Retrieved Memories\s*###", "", text, flags=re.IGNORECASE)
        text = re.sub(r"###\s*End Memories\s*###", "", text, flags=re.IGNORECASE)

        # Remove context blocks
        text = re.sub(r"<context>.*?</context>", "", text, flags=re.DOTALL)

        # ================================================================
        # HALLUCINATION DETECTION: Model generating fake dialogue
        # ================================================================
        # DeepSeek-R1 sometimes generates fake Q&A sessions after its answer
        # Detect and truncate at these patterns
        fake_dialogue_patterns = [
            # Model asking itself questions
            r"\n\n(?:How do I |What is |Can you |Could you ).*?\?\s*\n\n(?:To |I |You |The )",
            # Repeated "I hope this helps" closings
            r"(I hope this helps!.*?(?:Let me know|If you have).*?questions\.?\s*){2,}",
            # Model outputting "End Memories" followed by self-generated content
            r"###\s*End Memories\s*###\s*\n+(?:How|What|Can)",
        ]

        for pattern in fake_dialogue_patterns:
            match = re.search(pattern, text, flags=re.DOTALL | re.IGNORECASE)
            if match:
                # Truncate at the start of fake dialogue
                print(
                    f"[aj] HALLUCINATION DETECTED: Truncating fake dialogue at position {match.start()}"
                )
                text = text[: match.start()].rstrip()
                break

        # LOOP DETECTION: Remove repeated lines (model hallucination)
        # If the same line appears 3+ times, it's looping - remove all but first
        lines = text.split("\n")
        line_counts = {}
        for line in lines:
            stripped = line.strip()
            if stripped and len(stripped) > 10:  # Only count substantial lines
                line_counts[stripped] = line_counts.get(stripped, 0) + 1

        # Remove lines that appear 3+ times (keep first occurrence)
        looping_lines = {line for line, count in line_counts.items() if count >= 3}
        if looping_lines:
            seen_looping = set()
            filtered_lines = []
            for line in lines:
                stripped = line.strip()
                if stripped in looping_lines:
                    if stripped not in seen_looping:
                        seen_looping.add(stripped)
                        filtered_lines.append(line)
                    # Skip duplicates
                else:
                    filtered_lines.append(line)
            text = "\n".join(filtered_lines)

        # DEDUPE: Remove duplicate Task Plan blocks
        # The plan may appear twice - once from streaming, once from LLM response
        # Match plan: "📋 **Task Plan:**\n• step\n• step"
        plan_pattern = r"📋\s*\*?\*?Task Plan:?\*?\*?\s*\n(?:[•\s\-\*\d\.]+[^\n]+\n?)+"
        plan_matches = re.findall(plan_pattern, text, flags=re.IGNORECASE)
        if len(plan_matches) > 1:
            # Keep only the first occurrence (usually the streamed blockquote version)
            for match in plan_matches[1:]:
                text = text.replace(match, "", 1)

        # Remove repeated content blocks (model looping)
        # If the same 100+ char block appears multiple times, dedupe
        lines = text.split("\n\n")
        seen_blocks = set()
        deduped_blocks = []
        for block in lines:
            block_key = (
                block.strip()[:150] if len(block.strip()) > 150 else block.strip()
            )
            if block_key and block_key not in seen_blocks:
                seen_blocks.add(block_key)
                deduped_blocks.append(block)
            elif not block_key:
                deduped_blocks.append(block)
        text = "\n\n".join(deduped_blocks)

        # AGGRESSIVE TRUNCATION: Stop at repeated closings
        # If "I hope this helps" appears, truncate anything after the FIRST occurrence
        # (Model looping generates this phrase multiple times)
        closing_pattern = r"(I hope this helps[!.]?\s*(?:Let me know if you have (?:any )?(?:more )?questions[.!]?)?\s*)"
        closing_matches = list(re.finditer(closing_pattern, text, flags=re.IGNORECASE))
        if len(closing_matches) >= 1:
            # Keep only up to the first closing
            first_closing = closing_matches[0]
            end_pos = first_closing.end()
            if end_pos < len(text) - 50:  # There's substantial content after
                # Check if what follows looks like more hallucination
                after_text = text[end_pos:].strip()
                if after_text and (
                    after_text.startswith("###")
                    or re.match(
                        r"^(How|What|Can|Could|Is|Are|Do|Does)\s",
                        after_text,
                        re.IGNORECASE,
                    )
                ):
                    print(
                        f"[aj] HALLUCINATION: Truncating after first closing at position {end_pos}"
                    )
                    text = text[:end_pos].rstrip()

        # Clean up excessive whitespace from removals
        text = re.sub(r"\n{3,}", "\n\n", text)
        text = text.strip()

        # SANITIZATION: Detect and clean partial JSON responses
        # If response starts with JSON array/object fragment (comma, bracket, brace)
        # it's likely a partial/truncated response from regeneration
        stripped = text.strip()
        if stripped and stripped[0] in ",]}":
            # Try to extract any readable content from the mess
            # Look for "output" or "markdown" field values
            for field in ["output", "markdown", "response", "content"]:
                match = re.search(rf'"{field}"\s*:\s*"((?:[^"\\]|\\.)*)"\s*[,}}]', text)
                if match:
                    try:
                        text = json.loads('"' + match.group(1) + '"')
                        break
                    except:
                        text = match.group(1)
                        break
            else:
                # No extractable content - remove JSON fragments entirely
                text = re.sub(r"[\[\]{},]", "", text)
                text = re.sub(r'"[^"]*"\s*:', "", text)  # Remove JSON keys
                text = re.sub(r"\s+", " ", text).strip()

        # SANITIZATION: Remove raw JSON tool calls that leaked through
        # Pattern matches {"tool": "...", ...} or {"action": "...", ...} JSON objects
        json_tool_pattern = r'\{\s*"(?:tool|action)":\s*"[^"]+"\s*,\s*(?:"[^"]+"\s*:\s*(?:"[^"]*"|[^,}]+)\s*,?\s*)+\}'
        if '"tool":' in text or '"action":' in text:
            text = re.sub(json_tool_pattern, "", text, flags=re.DOTALL)
            # Clean up multiple newlines
            text = re.sub(r"\n{3,}", "\n\n", text)
            text = text.strip()

        # SANITIZATION: Remove any leaked tool call syntax
        # Some models output [TOOL_CALLS] when they shouldn't
        tool_call_pattern = r"\[TOOL_CALLS\][^\[]*(?:\[ARGS\][^\[]*)?"
        if "[TOOL_CALLS]" in text:
            text = re.sub(tool_call_pattern, "", text)
            # Clean up any orphaned text that looks like tool calls
            text = re.sub(r'\{"file_path":\s*"[^"]*"\}', "", text)
            text = re.sub(r'\{"path":\s*"[^"]*"\}', "", text)
            # Remove multiple newlines created by cleanup
            text = re.sub(r"\n{3,}", "\n\n", text)
            text = text.strip()
            # If we removed everything, provide a fallback
            if not text or len(text) < 20:
                text = "I'll help you with that task. Let me process the files in your workspace."

        # Skip further formatting if already well-formatted
        if text.startswith("```"):
            return text

        # Pattern for file paths and names (with extensions or path separators)
        # Match paths like: file.py, /path/to/file, ./folder, layers/executor/main.py
        file_pattern = r"(?<![`\w])([./\\]?[\w\-]+(?:[/\\][\w\-\.]+)+\.?\w*|[\w\-]+\.\w{1,10})(?![`\w])"

        def wrap_if_not_wrapped(match):
            path = match.group(1)
            # Don't wrap if it looks like a URL or already wrapped
            if path.startswith("http") or path.startswith("`"):
                return match.group(0)
            return f"`{path}`"

        # Apply file path formatting (but not inside code blocks)
        lines = text.split("\n")
        formatted_lines = []
        in_code_block = False

        for line in lines:
            if line.strip().startswith("```"):
                in_code_block = not in_code_block
                formatted_lines.append(line)
            elif in_code_block:
                formatted_lines.append(line)
            else:
                # Format file paths outside code blocks
                formatted_line = re.sub(file_pattern, wrap_if_not_wrapped, line)
                formatted_lines.append(formatted_line)

        result = "\n".join(formatted_lines)

        # ================================================================
        # FINAL THOUGHT: Append summary box for casual intents only
        # ================================================================
        if final_thought_content and casual_intent:
            # Format the thinking content as a Final Thought summary
            # Use blockquote style similar to Open-WebUI's reasoning display
            result = result.strip()
            if result:
                result += "\n\n"
            result += "---\n\n"
            result += "💭 **Final Thought:**\n\n"
            result += f"> {final_thought_content.replace(chr(10), chr(10) + '> ')}"

        return result
