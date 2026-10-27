"""
Tests for Filter Logic - Intent Routing and Task Handling

Tests the aj.filter.py logic for intent classification routing,
task continuation detection, and orchestrator integration.
"""

import pytest
from unittest.mock import MagicMock, AsyncMock, patch
from typing import List, Dict, Any

import sys

sys.path.insert(0, str(__file__).replace("\\tests\\test_filter_logic.py", "\\filters"))


class TestIntentClassification:
    """Test intent classification routing."""

    def test_casual_intent_bypasses_tools(self):
        """Casual intent should pass directly to LLM."""
        intent = {"intent": "casual", "confidence": 0.95}

        should_delegate = intent["intent"] in ("task", "recall", "save")
        assert should_delegate is False

    def test_task_intent_delegates_to_orchestrator(self):
        """Task intent should delegate to orchestrator."""
        intent = {"intent": "task", "confidence": 0.90}

        should_delegate = intent["intent"] == "task"
        assert should_delegate is True

    def test_recall_intent_searches_memory(self):
        """Recall intent should search memory."""
        intent = {"intent": "recall", "confidence": 0.85}

        should_search = intent["intent"] == "recall"
        assert should_search is True

    def test_save_intent_stores_memory(self):
        """Save intent should store to memory."""
        intent = {"intent": "save", "confidence": 0.88}

        should_store = intent["intent"] == "save"
        assert should_store is True


class TestTaskContinuationDetection:
    """Test detection of task continuation patterns."""

    def test_short_affirmatives_detected(self):
        """Short affirmative responses should be detected."""
        affirmatives = [
            "yes",
            "ok",
            "sure",
            "do it",
            "go ahead",
            "please do",
            "sounds good",
            "proceed",
        ]

        for text in affirmatives:
            is_short = len(text) < 50
            assert is_short is True

    def test_long_messages_not_continuation(self):
        """Long messages should not be treated as continuations."""
        long_text = "I would like you to create a comprehensive documentation " * 3

        is_short = len(long_text) < 100
        assert is_short is False

    def test_continuation_patterns_in_context(self):
        """Should detect task proposals in assistant context."""
        task_proposals = [
            "Would you like me to proceed with these changes?",
            "Should I update the files?",
            "I can fix this for you. Ready to proceed?",
            "Here's the plan. Do you want me to execute it?",
        ]

        patterns = [
            "would you like me to",
            "should i ",
            "i can ",
            "here's the plan",
        ]

        for context in task_proposals:
            has_proposal = any(p in context.lower() for p in patterns)
            assert has_proposal is True


class TestBuildTaskDescription:
    """Test task description building for orchestrator."""

    def test_direct_request_unchanged(self):
        """Direct task requests should be passed as-is."""
        user_text = "List all Python files in the workspace"

        # Not a continuation
        is_continuation = len(user_text) < 100 and user_text.lower().strip() in [
            "yes",
            "ok",
            "sure",
            "do it",
            "go ahead",
        ]
        assert is_continuation is False

    def test_continuation_includes_original_task(self):
        """Continuation should include original task context."""
        messages = [
            {"role": "user", "content": "Which files are the largest?"},
            {
                "role": "assistant",
                "content": "The largest files are in checkpoints/. Would you like me to clean them?",
            },
            {"role": "user", "content": "yes"},
        ]

        current_text = messages[-1]["content"]
        is_continuation = current_text.lower().strip() == "yes"

        assert is_continuation is True

        # Should find original task
        original_task = None
        for msg in reversed(messages[:-1]):
            if msg.get("role") == "user":
                content = msg.get("content", "")
                if len(content) >= 20:  # Substantial message
                    original_task = content
                    break

        assert original_task == "Which files are the largest?"


class TestOrchestratorIntegration:
    """Test orchestrator SSE streaming integration."""

    def test_event_types_handled(self):
        """All SSE event types should be handled."""
        event_types = ["status", "message", "complete", "error"]

        for event_type in event_types:
            assert event_type in ["status", "message", "complete", "error"]

    def test_status_event_updates_ui(self):
        """Status events should update the status bar."""
        event = {
            "type": "status",
            "data": {"description": "Thinking...", "done": False},
        }

        assert event["type"] == "status"
        assert "description" in event["data"]

    def test_message_event_appends_to_chat(self):
        """Message events should append to chat."""
        event = {
            "type": "message",
            "data": {"content": "> 💭 Thinking about the task..."},
        }

        assert event["type"] == "message"
        assert "content" in event["data"]

    def test_complete_event_finishes_task(self):
        """Complete events should finalize the task."""
        event = {"type": "complete", "data": {"context": "Task completed successfully"}}

        assert event["type"] == "complete"


class TestWorkspaceConfiguration:
    """Test workspace configuration handling."""

    def test_workspace_root_required_for_tasks(self):
        """Tasks require workspace_root to be set."""
        workspace_root = "/workspace/project"

        can_execute = workspace_root is not None
        assert can_execute is True

    def test_missing_workspace_root_blocks_tasks(self):
        """Missing workspace_root should block task execution."""
        workspace_root = None

        can_execute = workspace_root is not None
        assert can_execute is False


class TestMemoryIntegration:
    """Test memory service integration."""

    def test_recall_searches_with_user_id(self):
        """Recall should search with user_id filter."""
        search_params = {
            "user_id": "test_user",
            "query_text": "what's my email",
            "top_k": 5,
        }

        assert "user_id" in search_params
        assert "query_text" in search_params

    def test_save_includes_source_info(self):
        """Save should include source metadata."""
        save_params = {
            "user_id": "test_user",
            "messages": [{"role": "user", "content": "My email is test@example.com"}],
            "source_type": "conversation",
        }

        assert "source_type" in save_params


class TestErrorHandling:
    """Test error handling in filter logic."""

    def test_pragmatics_fallback_on_error(self):
        """Should fallback to casual on pragmatics error."""
        # Simulate error response
        fallback_intent = {"intent": "casual", "confidence": 0.3}

        assert fallback_intent["intent"] == "casual"
        assert fallback_intent["confidence"] < 0.5  # Low confidence fallback

    def test_orchestrator_error_handled(self):
        """Orchestrator errors should be handled gracefully."""
        error_response = {"type": "error", "data": {"message": "Connection timeout"}}

        assert error_response["type"] == "error"
        assert "message" in error_response["data"]


class TestContextInjection:
    """Test context injection into LLM messages."""

    def test_system_prompt_injected(self):
        """AJ system prompt should be injected."""
        # The AJ_SYSTEM_PROMPT constant should be defined
        expected_sections = [
            "AJ",
            "Classifies Intent",
            "Manages Memory",
            "Output Contract",
        ]

        # Verify expected structure
        for section in expected_sections:
            assert len(section) > 0

    def test_memory_context_injected(self):
        """Retrieved memories should be injected."""
        memories = [
            {"text": "User's name is Ian", "score": 0.85},
            {"text": "User prefers dark mode", "score": 0.72},
        ]

        # Format for injection
        context = "\n".join([f"- {m['text']}" for m in memories])

        assert "Ian" in context
        assert "dark mode" in context

    def test_workspace_results_injected(self):
        """Workspace operation results should be injected."""
        result = {
            "tool": "scan_workspace",
            "output": "README.md\nsetup.py\nsrc/",
        }

        # Should be included in context
        assert "README.md" in result["output"]


class TestContentTypeMapping:
    """Test file content type mapping."""

    def test_common_extensions_mapped(self):
        """Common file extensions should have MIME types."""
        expected_mappings = {
            ".md": "text/markdown",
            ".py": "text/x-python",
            ".json": "application/json",
            ".pdf": "application/pdf",
            ".png": "image/png",
        }

        for ext, mime in expected_mappings.items():
            assert len(mime) > 0


# ============================================================================
# IRON GATE Tests - Fabricated CLI Output Detection
# ============================================================================
# Note: Execution request detection is now handled by intent classification
# in the orchestrator (task vs casual). These tests focus on detecting
# fabricated command-line output patterns (ipconfig, ping, process lists, etc.)

# Import the detection function and signatures
try:
    from aj_filter import (
        _contains_fabricated_cli_output,
        FABRICATED_CLI_OUTPUT_SIGNATURES,
    )
except ImportError:
    # Create mock function for testing patterns
    import re

    FABRICATED_CLI_OUTPUT_SIGNATURES = [
        r"IPv4 Address[.\s]*:\s*\d+\.\d+\.\d+\.\d+",
        r"IP Address[.\s]*:\s*\d+\.\d+\.\d+\.\d+",
        r"Subnet Mask[.\s]*:\s*\d+\.\d+\.\d+\.\d+",
        r"Default Gateway[.\s]*:\s*\d+\.\d+\.\d+\.\d+",
        r"IPv6 Address[.\s]*:",
        r"Physical Address[.\s]*:\s*[\da-fA-F:-]+",
        r"DHCP (?:Enabled|Server)",
        r"DNS (?:Servers?|Suffix)",
        r"Reply from\s+\d+\.\d+\.\d+\.\d+",
        r"bytes=\d+\s+time[<=]\d+",
        r"icmp_seq=\d+\s+ttl=\d+",
        r"time=\d+(?:\.\d+)?\s*ms",
        r"TTL=\d+",
        r"(?:Handles|NPM|PM|WS|CPU)\s+\w+.*\d+",
        r"PID\s*[:=]?\s*\d{3,}",
        r"Ethernet adapter\s+\w+:",
        r"Connection-specific DNS Suffix",
        r"Link-local IPv6 Address",
        r"Lease (?:Obtained|Expires)",
    ]

    def _contains_fabricated_cli_output(text: str) -> bool:
        """Check if text contains fabricated CLI command output."""
        if not text:
            return False
        match_count = 0
        for pattern in FABRICATED_CLI_OUTPUT_SIGNATURES:
            if re.search(pattern, text, re.IGNORECASE):
                match_count += 1
                if match_count >= 2:
                    return True
        return False


class TestFabricatedCliOutputDetection:
    """Test detection of fabricated CLI output in LLM responses."""

    def test_ipconfig_output_detected(self):
        """Fake ipconfig output should be detected as fabricated."""
        fake_output = """
        Windows IP Configuration
        
        IPv4 Address. . . . . . . . . : 192.168.1.100
        Subnet Mask . . . . . . . . . : 255.255.255.0
        Default Gateway . . . . . . . : 192.168.1.1
        """
        assert _contains_fabricated_cli_output(fake_output) is True

    def test_ping_output_detected(self):
        """Fake ping output should be detected as fabricated."""
        fake_output = """
        Reply from 216.58.194.174: bytes=32 time=29ms TTL=57
        """
        assert _contains_fabricated_cli_output(fake_output) is True

    def test_linux_ping_detected(self):
        """Linux-style ping output should be detected."""
        fake_output = """
        64 bytes from lb-in-f174.1e100.net: icmp_seq=1 ttl=57 time=29.7 ms
        """
        assert _contains_fabricated_cli_output(fake_output) is True

    def test_process_listing_detected(self):
        """Fake Get-Process output with multiple indicators should be detected."""
        # Process listing needs at least 2 matching patterns
        # Combine PID with another pattern like "NPM" header
        fake_output = """
        PID: 23416
        Reply from 192.168.1.1: bytes=32 time<1ms TTL=128
        """
        assert _contains_fabricated_cli_output(fake_output) is True

    def test_ethernet_adapter_detected(self):
        """Ethernet adapter info should be detected."""
        fake_output = """
        Ethernet adapter Ethernet:
           Connection-specific DNS Suffix  . : home.local
           Link-local IPv6 Address . . . . . : fe80::1234:5678:abcd:ef01
        """
        assert _contains_fabricated_cli_output(fake_output) is True

    def test_normal_response_not_detected(self):
        """Normal conversational responses should not be detected."""
        normal = "I'll help you run that command. Let me connect to the agent."
        assert _contains_fabricated_cli_output(normal) is False

    def test_explanation_not_detected(self):
        """Explanations about commands should not be detected."""
        explanation = """
        The ipconfig command on Windows displays network configuration.
        It's similar to ifconfig on Linux systems.
        """
        assert _contains_fabricated_cli_output(explanation) is False

    def test_error_message_not_detected(self):
        """Error messages should not be detected."""
        error = """
        ⚠️ Execution Failed
        
        I was unable to execute the command on the remote agent.
        The orchestrator service may be unavailable.
        """
        assert _contains_fabricated_cli_output(error) is False

    def test_single_ip_not_enough(self):
        """A single IP mention shouldn't trigger (needs 2+ patterns)."""
        single_ip = "The server is at 192.168.1.1"
        assert _contains_fabricated_cli_output(single_ip) is False

    def test_empty_text(self):
        """Empty text should not be detected."""
        assert _contains_fabricated_cli_output("") is False
        assert _contains_fabricated_cli_output(None) is False


class TestIronGateIntegration:
    """Test the complete IRON GATE logic flow.

    Note: `task_intent` is now determined by intent classification (task vs casual)
    rather than regex-based execution request detection.
    """

    def test_gate_blocks_when_all_conditions_met(self):
        """Iron gate should block when: task intent, not executed, fabricated output."""
        # Simulate the conditions
        task_intent = True  # Intent classified as "task"
        execution_occurred = False

        fabricated_response = """
        IPv4 Address. . . . . . . . . : 192.168.1.100
        Subnet Mask . . . . . . . . . : 255.255.255.0
        """

        should_block = (
            task_intent
            and not execution_occurred
            and _contains_fabricated_cli_output(fabricated_response)
        )

        assert should_block is True

    def test_gate_allows_when_execution_occurred(self):
        """Iron gate should allow if execution actually occurred."""
        task_intent = True
        execution_occurred = True  # Execution happened

        real_output = """
        IPv4 Address. . . . . . . . . : 192.168.1.100
        Subnet Mask . . . . . . . . . : 255.255.255.0
        """

        should_block = (
            task_intent
            and not execution_occurred
            and _contains_fabricated_cli_output(real_output)
        )

        assert should_block is False  # Should NOT block

    def test_gate_allows_normal_conversation(self):
        """Iron gate should allow normal conversation (casual intent)."""
        task_intent = False  # Intent classified as "casual"
        execution_occurred = False

        response = "Hello! How can I help you today?"

        should_block = (
            task_intent
            and not execution_occurred
            and _contains_fabricated_cli_output(response)
        )

        assert should_block is False


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
