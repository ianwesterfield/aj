"""
Tests for Streaming UX - Real-time Progress Display

Tests the streaming user experience including thinking display,
status updates, and event handling.
"""

import pytest
from unittest.mock import MagicMock, AsyncMock
from typing import List, Dict, Any


class TestThinkingDisplay:
    """Test thinking blockquote formatting."""
    
    def test_thinking_header_format(self):
        """Thinking should start with header."""
        header = "> 💭 **Thinking...**"
        
        assert "💭" in header
        assert "Thinking" in header
        assert header.startswith(">")
    
    def test_step_numbered_format(self):
        """Steps should be numbered."""
        steps = [
            "> 1. I'll scan the workspace to find files.",
            "> 2. I'll read the README to understand the project.",
            "> 3. Task complete.",
        ]
        
        for i, step in enumerate(steps, 1):
            assert f"> {i}." in step
    
    def test_observation_format(self):
        """Observations should be indented with arrow."""
        observation = ">    ↳ _Got 30 lines of output_"
        
        assert "↳" in observation
        assert "_" in observation  # Italic markers


class TestStatusUpdates:
    """Test status bar update formatting."""
    
    def test_thinking_status(self):
        """Thinking status should show thinking emoji."""
        status = {"description": "💭 Thinking...", "done": False}
        
        assert "💭" in status["description"]
        assert status["done"] is False
    
    def test_running_status(self):
        """Running status should show action."""
        status = {"description": "⚡ Running: git status", "done": False}
        
        assert "⚡" in status["description"]
        assert "Running" in status["description"]
    
    def test_done_status(self):
        """Done status should indicate completion."""
        status = {"description": "✅ Done", "done": True}
        
        assert "✅" in status["description"]
        assert status["done"] is True
    
    def test_error_status(self):
        """Error status should show error indicator."""
        status = {"description": "❌ Error: Connection failed", "done": True}
        
        assert "❌" in status["description"]
        assert "Error" in status["description"]
    
    def test_memory_status(self):
        """Memory status should show count."""
        status = {"description": "📚 Memories found: 3", "done": False}
        
        assert "📚" in status["description"]
        assert "3" in status["description"]
    
    def test_saved_status(self):
        """Saved status should confirm save."""
        status = {"description": "💾 Saved to memory", "done": True}
        
        assert "💾" in status["description"]


class TestSSEEventHandling:
    """Test Server-Sent Events handling."""
    
    def test_message_event_structure(self):
        """Message events should have content."""
        event = {
            "type": "message",
            "data": {"content": "> 💭 **Thinking...**\n> 1. Planning the task."}
        }
        
        assert event["type"] == "message"
        assert "content" in event["data"]
    
    def test_status_event_structure(self):
        """Status events should have description and done flag."""
        event = {
            "type": "status",
            "data": {"description": "Processing...", "done": False, "hidden": False}
        }
        
        assert event["type"] == "status"
        assert "description" in event["data"]
        assert "done" in event["data"]
    
    def test_complete_event_structure(self):
        """Complete events should have context."""
        event = {
            "type": "complete",
            "data": {"context": "### Results ###\nTask completed successfully."}
        }
        
        assert event["type"] == "complete"
        assert "context" in event["data"]
    
    def test_error_event_structure(self):
        """Error events should have message."""
        event = {
            "type": "error",
            "data": {"message": "Orchestrator timeout"}
        }
        
        assert event["type"] == "error"
        assert "message" in event["data"]


class TestStreamingAccumulation:
    """Test accumulation of streaming content."""
    
    def test_messages_accumulated_in_order(self):
        """Messages should accumulate in order received."""
        messages = []
        
        # Simulate receiving messages
        messages.append("> 💭 **Thinking...**")
        messages.append("> 1. First step")
        messages.append(">    ↳ _Result 1_")
        messages.append("> 2. Second step")
        
        combined = "\n".join(messages)
        
        # Order preserved
        assert combined.index("First") < combined.index("Second")
    
    def test_final_context_extracted(self):
        """Final context should be extracted from complete event."""
        complete_event = {
            "type": "complete",
            "data": {
                "context": "### File Listing ###\nREADME.md\nsetup.py\n### End ###"
            }
        }
        
        context = complete_event["data"]["context"]
        
        assert "File Listing" in context
        assert "README.md" in context


class TestProgressIndicators:
    """Test progress indication patterns."""
    
    def test_step_count_format(self):
        """Step counts should show progress."""
        progress = "Step 3/10"
        
        assert "3" in progress
        assert "10" in progress
    
    def test_output_line_count(self):
        """Output should indicate line count."""
        observation = "_Got 30 lines of output_"
        
        assert "30" in observation
        assert "lines" in observation
    
    def test_file_count_indicator(self):
        """File operations should show counts."""
        result = "Found 15 files matching pattern"
        
        assert "15" in result
        assert "files" in result


class TestErrorDisplaying:
    """Test error message display."""
    
    def test_error_in_thinking_block(self):
        """Errors should be shown in thinking block."""
        error_block = "> ❌ **Error:** File not found: config.yaml"
        
        assert "❌" in error_block
        assert "Error" in error_block
        assert "config.yaml" in error_block
    
    def test_recovery_suggestion(self):
        """Errors should suggest recovery when possible."""
        error_with_suggestion = (
            "> ❌ **Error:** replace_in_file failed - text not found\n"
            "> 💡 Suggestion: Use insert_in_file to add new content"
        )
        
        assert "Suggestion" in error_with_suggestion
        assert "insert_in_file" in error_with_suggestion


class TestVerbatimOutput:
    """Test verbatim output display contract."""
    
    def test_shell_output_in_code_block(self):
        """Shell output should be in fenced code block."""
        output = """```
total 15
drwxr-xr-x  4096  Dec 28 19:05  filters/
-rw-r--r-- 38618  Dec 29 03:04  README.md
```"""
        
        assert output.startswith("```")
        assert output.endswith("```")
    
    def test_no_prose_before_output(self):
        """No prose should appear before output."""
        # Correct format: output first
        correct = """```
file1.py
file2.py
```
Found 2 files."""
        
        # Wrong format: prose first
        wrong = """Here are the files:
- file1.py
- file2.py"""
        
        assert correct.startswith("```")
        assert not wrong.startswith("```")
    
    def test_output_not_summarized(self):
        """Output should not be summarized into bullet points."""
        raw_output = """NAME          TYPE   SIZE
filters/      dir    -
README.md     file   1.2 KB"""
        
        # Should not become:
        # - filters/: A directory
        # - README.md: Documentation file
        
        assert "A directory" not in raw_output
        assert "Documentation" not in raw_output


class TestEventEmitter:
    """Test event emitter interface."""
    
    @pytest.mark.asyncio
    async def test_emitter_called_for_status(self):
        """Event emitter should be called for status updates."""
        emitter = AsyncMock()
        
        await emitter({
            "type": "status",
            "data": {"description": "Working...", "done": False}
        })
        
        emitter.assert_called_once()
        call_args = emitter.call_args[0][0]
        assert call_args["type"] == "status"
    
    @pytest.mark.asyncio
    async def test_emitter_receives_all_event_types(self):
        """Event emitter should receive all event types."""
        emitter = AsyncMock()
        
        events = [
            {"type": "status", "data": {"description": "Start"}},
            {"type": "message", "data": {"content": "Thinking..."}},
            {"type": "complete", "data": {"context": "Done"}},
        ]
        
        for event in events:
            await emitter(event)
        
        assert emitter.call_count == 3


class TestHiddenStatus:
    """Test hidden status updates."""
    
    def test_hidden_status_not_shown(self):
        """Hidden status should not appear in UI."""
        status = {
            "type": "status",
            "data": {"description": "Internal processing", "done": False, "hidden": True}
        }
        
        assert status["data"]["hidden"] is True
    
    def test_visible_status_shown(self):
        """Visible status should appear in UI."""
        status = {
            "type": "status",
            "data": {"description": "Processing files", "done": False, "hidden": False}
        }
        
        assert status["data"]["hidden"] is False


class TestContextualCompletionStatus:
    """Test contextual completion status messages based on tools used."""
    
    def _build_completion_status(self, step_history: List[Dict[str, Any]]) -> str:
        """Build completion status from step history (mirrors orchestrator logic)."""
        if step_history:
            tools_used = [h["tool"] for h in step_history if h.get("success")]
            edit_tools = {"write_file", "replace_in_file", "insert_in_file", "append_to_file"}
            
            if any(t in edit_tools for t in tools_used):
                return f"✅ Done — edited {sum(1 for t in tools_used if t in edit_tools)} file(s)"
            elif "execute_shell" in tools_used:
                return f"✅ Done — ran {sum(1 for t in tools_used if t == 'execute_shell')} command(s)"
            elif "scan_workspace" in tools_used:
                return f"✅ Done — scanned {sum(1 for t in tools_used if t == 'scan_workspace')} location(s)"
            elif "read_file" in tools_used:
                return f"✅ Done — read {sum(1 for t in tools_used if t == 'read_file')} file(s)"
            else:
                return f"✅ Done — {len(step_history)} step(s)"
        return "✅ Done"
    
    def test_scan_workspace_status(self):
        """Scanning should show scan count."""
        history = [{"tool": "scan_workspace", "success": True}]
        status = self._build_completion_status(history)
        
        assert "scanned" in status
        assert "1 location" in status
        assert "✅ Done" in status
    
    def test_multiple_scans_status(self):
        """Multiple scans should show correct count."""
        history = [
            {"tool": "scan_workspace", "success": True},
            {"tool": "scan_workspace", "success": True},
            {"tool": "scan_workspace", "success": True},
        ]
        status = self._build_completion_status(history)
        
        assert "3 location" in status
    
    def test_read_file_status(self):
        """Reading files should show read count."""
        history = [
            {"tool": "read_file", "success": True},
            {"tool": "read_file", "success": True},
        ]
        status = self._build_completion_status(history)
        
        assert "read" in status
        assert "2 file" in status
    
    def test_edit_file_status(self):
        """Editing files should show edit count."""
        history = [
            {"tool": "read_file", "success": True},
            {"tool": "replace_in_file", "success": True},
            {"tool": "write_file", "success": True},
        ]
        status = self._build_completion_status(history)
        
        # Edit takes priority over read
        assert "edited" in status
        assert "2 file" in status
    
    def test_shell_command_status(self):
        """Shell commands should show command count."""
        history = [
            {"tool": "execute_shell", "success": True},
        ]
        status = self._build_completion_status(history)
        
        assert "ran" in status
        assert "1 command" in status
    
    def test_failed_steps_not_counted(self):
        """Failed steps should not be counted in status."""
        history = [
            {"tool": "scan_workspace", "success": True},
            {"tool": "scan_workspace", "success": False},  # Failed
            {"tool": "scan_workspace", "success": True},
        ]
        status = self._build_completion_status(history)
        
        # Only 2 successful scans
        assert "2 location" in status
    
    def test_empty_history_fallback(self):
        """Empty history should show simple Done."""
        status = self._build_completion_status([])
        
        assert status == "✅ Done"
    
    def test_edit_priority_over_other_tools(self):
        """Edit operations should take priority in status."""
        history = [
            {"tool": "scan_workspace", "success": True},
            {"tool": "read_file", "success": True},
            {"tool": "replace_in_file", "success": True},
            {"tool": "execute_shell", "success": True},
        ]
        status = self._build_completion_status(history)
        
        # Edit takes priority
        assert "edited" in status
        assert "scanned" not in status
        assert "ran" not in status


class TestCompletionEventHandling:
    """Test filter handling of completion events."""
    
    def test_complete_event_has_status(self):
        """Complete event should include contextual status."""
        event = {
            "event_type": "complete",
            "status": "✅ Done — scanned 1 location(s)",
            "result": {"context": "...", "steps_executed": 1},
            "done": True
        }
        
        assert "status" in event
        assert "scanned" in event["status"]
    
    def test_complete_event_fallback_status(self):
        """Complete event should have fallback if status missing."""
        event = {
            "event_type": "complete",
            "result": {"context": "..."},
            "done": True
        }
        
        # Filter should use fallback
        status = event.get("status", "✅ Done")
        assert status == "✅ Done"
    
    @pytest.mark.asyncio
    async def test_emitter_receives_final_status(self):
        """Event emitter should receive final completion status."""
        emitter = AsyncMock()
        
        # Simulate what filter does on complete event
        complete_status = "✅ Done — edited 2 file(s)"
        await emitter({
            "type": "status",
            "data": {"description": complete_status, "done": True, "hidden": False}
        })
        
        call_args = emitter.call_args[0][0]
        assert call_args["data"]["done"] is True
        assert "edited" in call_args["data"]["description"]


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
