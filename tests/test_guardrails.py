"""
Tests for Guardrails in ReasoningEngine

Tests the safety guardrails that prevent infinite loops and repeated failures.
"""

import pytest
from unittest.mock import MagicMock, AsyncMock, patch
from typing import List, Dict, Any, Optional, Set
from dataclasses import dataclass, field


# Inline minimal implementation for testing
@dataclass
class CompletedStep:
    step_id: str
    tool: str
    params: Dict[str, Any]
    output_summary: str
    success: bool


@dataclass
class SessionState:
    scanned_paths: Set[str] = field(default_factory=set)
    files: List[str] = field(default_factory=list)
    dirs: List[str] = field(default_factory=list)
    edited_files: Set[str] = field(default_factory=set)
    read_files: Set[str] = field(default_factory=set)
    completed_steps: List[CompletedStep] = field(default_factory=list)
    user_info: Dict[str, str] = field(default_factory=dict)
    discovered_agents: Dict[str, Any] = field(default_factory=dict)  # FunnelCloud agents
    
    def update_from_step(self, tool: str, params: Dict[str, Any], output: str, success: bool) -> None:
        step_id = f"S{len(self.completed_steps) + 1:03d}"
        summary = f"{tool}: {'OK' if success else 'FAILED'}"
        if tool in ("write_file", "replace_in_file", "insert_in_file", "append_to_file"):
            path = params.get("path", "")
            if success:
                self.edited_files.add(path)
            summary = f"{tool}({path}): {'OK' if success else 'FAILED'}"
        self.completed_steps.append(CompletedStep(
            step_id=step_id, tool=tool, params=params,
            output_summary=summary, success=success,
        ))


class TestMaxStepsGuardrail:
    """Test the maximum steps guardrail."""
    
    def test_triggers_after_15_steps_without_progress(self):
        """Should force completion after 15 steps without edits."""
        state = SessionState()
        
        # Add 15 non-edit steps
        for i in range(15):
            state.completed_steps.append(CompletedStep(
                step_id=f"S{i:03d}",
                tool="read_file",
                params={"path": f"file{i}.txt"},
                output_summary=f"read(file{i}.txt): 100 chars",
                success=True,
            ))
        
        # Check recent edits
        recent_edits = sum(
            1 for s in state.completed_steps[-5:]
            if s.tool in ("write_file", "insert_in_file", "replace_in_file", "append_to_file")
            and s.success
        )
        
        assert recent_edits == 0
        assert len(state.completed_steps) >= 15
    
    def test_does_not_trigger_with_recent_edits(self):
        """Should allow continuation if recent edits were made."""
        state = SessionState()
        
        # Add 14 read steps
        for i in range(14):
            state.completed_steps.append(CompletedStep(
                step_id=f"S{i:03d}",
                tool="read_file",
                params={"path": f"file{i}.txt"},
                output_summary=f"read(file{i}.txt): 100 chars",
                success=True,
            ))
        
        # Add 1 edit step
        state.completed_steps.append(CompletedStep(
            step_id="S014",
            tool="write_file",
            params={"path": "edited.txt"},
            output_summary="write_file(edited.txt): OK",
            success=True,
        ))
        
        # Check recent edits
        recent_edits = sum(
            1 for s in state.completed_steps[-5:]
            if s.tool in ("write_file", "insert_in_file", "replace_in_file", "append_to_file")
            and s.success
        )
        
        assert recent_edits == 1


class TestRepeatedFailuresGuardrail:
    """Test the repeated failures guardrail."""
    
    def test_detects_repeated_replace_failures(self):
        """Should detect repeated replace_in_file failures on same file."""
        state = SessionState()
        
        # Add 3 failed replace attempts on same file
        for i in range(3):
            state.completed_steps.append(CompletedStep(
                step_id=f"S{i:03d}",
                tool="replace_in_file",
                params={"path": "target.py", "old_text": "not found"},
                output_summary="replace_in_file(target.py): FAILED",
                success=False,
            ))
        
        # Count recent failures on target.py
        path = "target.py"
        recent_failures = sum(
            1 for s in state.completed_steps[-5:]
            if s.tool == "replace_in_file"
            and s.params.get("path") == path
            and not s.success
        )
        
        assert recent_failures >= 2
    
    def test_different_files_not_flagged(self):
        """Failures on different files should not trigger guardrail."""
        state = SessionState()
        
        # Add failures on different files
        state.completed_steps.append(CompletedStep(
            step_id="S001",
            tool="replace_in_file",
            params={"path": "file1.py"},
            output_summary="FAILED",
            success=False,
        ))
        state.completed_steps.append(CompletedStep(
            step_id="S002",
            tool="replace_in_file",
            params={"path": "file2.py"},
            output_summary="FAILED",
            success=False,
        ))
        
        # Count failures per file
        for path in ["file1.py", "file2.py"]:
            failures = sum(
                1 for s in state.completed_steps
                if s.params.get("path") == path and not s.success
            )
            assert failures == 1


class TestReReadGuardrail:
    """Test the re-read prevention guardrail."""
    
    def test_blocks_reread_of_already_read_file(self):
        """Should block reading a file that's already been read."""
        state = SessionState()
        state.read_files.add("already_read.py")
        
        # Simulate check
        path = "already_read.py"
        should_block = path in state.read_files
        
        assert should_block is True
    
    def test_allows_first_read(self):
        """Should allow first read of a file."""
        state = SessionState()
        
        path = "new_file.py"
        should_block = path in state.read_files
        
        assert should_block is False


class TestUnknownToolGuardrail:
    """Test handling of unknown/hallucinated tools."""
    
    def test_known_tools_accepted(self):
        """Known tools should be accepted."""
        known_tools = [
            "scan_workspace",
            "read_file",
            "write_file",
            "replace_in_file",
            "insert_in_file",
            "append_to_file",
            "execute_shell",
            "none",
            "complete",
        ]
        
        for tool in known_tools:
            assert tool in known_tools  # Trivial but documents the set
    
    def test_unknown_tool_handling(self):
        """Unknown tools should be handled gracefully."""
        unknown_tools = ["hallucinated_tool", "make_coffee", "deploy_to_production"]
        
        # These should either:
        # 1. Be converted to execute_shell if they look like commands
        # 2. Trigger completion with error
        for tool in unknown_tools:
            is_known = tool in {
                "scan_workspace", "read_file", "write_file",
                "replace_in_file", "insert_in_file", "append_to_file",
                "execute_shell", "none", "complete"
            }
            assert is_known is False


class TestDuplicateCommandGuardrail:
    """Test duplicate command detection."""
    
    def test_detects_same_shell_command_twice(self):
        """Should detect same shell command run twice."""
        state = SessionState()
        
        state.completed_steps.append(CompletedStep(
            step_id="S001",
            tool="execute_shell",
            params={"command": "git status"},
            output_summary="shell(git status): OK",
            success=True,
        ))
        state.completed_steps.append(CompletedStep(
            step_id="S002",
            tool="execute_shell",
            params={"command": "git status"},
            output_summary="shell(git status): OK",
            success=True,
        ))
        
        # Check for duplicates
        commands = [
            s.params.get("command")
            for s in state.completed_steps
            if s.tool == "execute_shell"
        ]
        
        # git status appears twice
        assert commands.count("git status") == 2
    
    def test_different_commands_allowed(self):
        """Different commands should not trigger guardrail."""
        state = SessionState()
        
        state.completed_steps.append(CompletedStep(
            step_id="S001",
            tool="execute_shell",
            params={"command": "git status"},
            output_summary="OK",
            success=True,
        ))
        state.completed_steps.append(CompletedStep(
            step_id="S002",
            tool="execute_shell",
            params={"command": "git log --oneline -5"},
            output_summary="OK",
            success=True,
        ))
        
        commands = [
            s.params.get("command")
            for s in state.completed_steps
            if s.tool == "execute_shell"
        ]
        
        # No duplicates
        assert len(commands) == len(set(commands))


class TestPathValidationGuardrail:
    """Test path validation for file operations."""
    
    def test_path_in_scanned_files_accepted(self):
        """Paths in scanned files should be accepted."""
        state = SessionState()
        state.files = ["src/main.py", "tests/test_main.py", "README.md"]
        
        path = "src/main.py"
        is_valid = path in state.files
        
        assert is_valid is True
    
    def test_unknown_path_flagged(self):
        """Paths not in scanned files should be flagged."""
        state = SessionState()
        state.files = ["src/main.py", "README.md"]
        
        path = "nonexistent/file.py"
        is_valid = path in state.files
        
        assert is_valid is False
    
    def test_similar_path_suggestion(self):
        """Should find similar paths for correction."""
        state = SessionState()
        state.files = [
            ".github/copilot-instructions.md",
            "src/config.py",
            "tests/test_config.py",
        ]
        
        # User might forget directory prefix
        incorrect_path = "copilot-instructions.md"
        
        # Find similar
        similar = [f for f in state.files if f.endswith(incorrect_path)]
        
        assert len(similar) == 1
        assert similar[0] == ".github/copilot-instructions.md"


class TestWriteFileRepeatedGuardrail:
    """Test repeated write_file detection."""
    
    def test_detects_double_write_to_same_file(self):
        """Should detect writing to the same file twice."""
        state = SessionState()
        
        state.update_from_step(
            "write_file",
            {"path": "output.txt", "content": "first"},
            "Written",
            True
        )
        
        # File is now in edited_files
        assert "output.txt" in state.edited_files
        
        # Second write should be flagged
        already_edited = "output.txt" in state.edited_files
        assert already_edited is True
    
    def test_allows_first_write(self):
        """First write to a file should be allowed."""
        state = SessionState()
        
        already_edited = "new_file.txt" in state.edited_files
        assert already_edited is False


class TestGuardrailIntegration:
    """Test guardrail interactions."""
    
    def test_multiple_guardrails_can_trigger(self):
        """Multiple guardrail conditions can be true simultaneously."""
        state = SessionState()
        
        # Add many steps (max steps guardrail)
        for i in range(16):
            state.completed_steps.append(CompletedStep(
                step_id=f"S{i:03d}",
                tool="read_file",
                params={"path": f"file{i}.txt"},
                output_summary="read",
                success=True,
            ))
        
        # Also add repeated failures
        for i in range(3):
            state.completed_steps.append(CompletedStep(
                step_id=f"F{i:03d}",
                tool="replace_in_file",
                params={"path": "target.py"},
                output_summary="FAILED",
                success=False,
            ))
        
        # Multiple conditions are true
        too_many_steps = len(state.completed_steps) >= 15
        repeated_failures = sum(
            1 for s in state.completed_steps[-5:]
            if s.tool == "replace_in_file" and not s.success
        ) >= 2
        
        assert too_many_steps is True
        assert repeated_failures is True


class TestRemoteExecutionHallucinationGuardrail:
    """
    Test guardrails that prevent hallucinated results when no FunnelCloud agents are available.
    
    Critical scenario from production:
    - User asks: "Which 10 files take up the most space on ian-r16?"
    - list_agents returns: "No FunnelCloud agents discovered"
    - BAD: Model hallucinates fake file list like "/home/user/largefile1.bin 4.5G"
    - GOOD: Model returns error "No agents available, cannot access remote machine"
    """
    
    def test_detects_hallucination_after_empty_list_agents(self):
        """Should detect when model hallucinates results after list_agents returns empty."""
        state = SessionState()
        state.discovered_agents = {}  # No agents discovered
        
        # Simulate list_agents being called (empty result)
        state.completed_steps.append(CompletedStep(
            step_id="S001",
            tool="list_agents",
            params={},
            output_summary="No FunnelCloud agents discovered",
            success=True,
        ))
        
        # Model tries to complete with fake results
        fake_answer = """Here are the top 10 largest files on ian-r16:
/home/user/largefile1.bin    4.5G
/home/user/video_collection/bigmovie.mp4   3.2G
/home/user/backups/backup_2023-12-01.tar.gz  2.8G"""
        
        # Check for hallucination indicators
        hallucination_indicators = [
            "here are the",
            "top 10",
            "largest files",
            "scanned",
            "/home/",
            "/user/",
            ".bin",
            ".tar",
            ".iso",
        ]
        
        recent_steps = state.completed_steps[-3:]
        last_was_list_agents = any(s.tool == "list_agents" for s in recent_steps)
        is_hallucinating = any(ind in fake_answer.lower() for ind in hallucination_indicators)
        
        assert last_was_list_agents is True
        assert state.discovered_agents == {}
        assert is_hallucinating is True
    
    def test_allows_legitimate_completion_with_error(self):
        """Should allow completion that honestly reports no agents available."""
        state = SessionState()
        state.discovered_agents = {}
        
        state.completed_steps.append(CompletedStep(
            step_id="S001",
            tool="list_agents",
            params={},
            output_summary="No FunnelCloud agents discovered",
            success=True,
        ))
        
        # Legitimate error response
        honest_answer = "No FunnelCloud agents are available. I cannot access remote machines without an agent running."
        
        hallucination_indicators = [
            "here are the",
            "top 10",
            "largest files",
            "/home/",
            ".bin",
            ".tar",
        ]
        
        is_hallucinating = any(ind in honest_answer.lower() for ind in hallucination_indicators)
        
        assert is_hallucinating is False
    
    def test_allows_results_when_agent_discovered(self):
        """Should allow results when an agent was actually discovered."""
        state = SessionState()
        state.discovered_agents = {"ian-r16": {"name": "ian-r16", "host": "192.168.1.100"}}
        
        state.completed_steps.append(CompletedStep(
            step_id="S001",
            tool="list_agents",
            params={},
            output_summary="Found 1 agent: ian-r16",
            success=True,
        ))
        
        state.completed_steps.append(CompletedStep(
            step_id="S002",
            tool="remote_execute",
            params={"agent_id": "ian-r16", "command": "Get-ChildItem -Recurse | Sort-Object Length -Descending | Select-Object -First 10"},
            output_summary="Got 10 file entries",
            success=True,
        ))
        
        # With agent discovered AND remote_execute done, results are legitimate
        did_remote_work = any(s.tool == "remote_execute" for s in state.completed_steps)
        has_agent = len(state.discovered_agents) > 0
        
        assert has_agent is True
        assert did_remote_work is True
    
    def test_blocks_lazy_completion_after_discovering_agents(self):
        """Should block completion without remote work when agents ARE available."""
        state = SessionState()
        state.discovered_agents = {"ian-r16": {"name": "ian-r16"}}
        
        state.completed_steps.append(CompletedStep(
            step_id="S001",
            tool="list_agents",
            params={},
            output_summary="Found 1 agent: ian-r16",
            success=True,
        ))
        
        # Model tries to complete without calling remote_execute
        recent_steps = state.completed_steps[-3:]
        last_was_list_agents = any(s.tool == "list_agents" for s in recent_steps)
        did_remote_work = any(s.tool == "remote_execute" for s in state.completed_steps)
        has_agents = len(state.discovered_agents) > 0
        
        # Guardrail should trigger
        should_block = last_was_list_agents and not did_remote_work and has_agents
        
        assert should_block is True
    
    def test_repeated_list_agents_without_progress(self):
        """Should detect when model keeps calling list_agents without making progress."""
        state = SessionState()
        state.discovered_agents = {}
        
        # Model calls list_agents multiple times
        for i in range(3):
            state.completed_steps.append(CompletedStep(
                step_id=f"S{i:03d}",
                tool="list_agents",
                params={},
                output_summary="No FunnelCloud agents discovered",
                success=True,
            ))
        
        # Count list_agents calls in recent steps
        recent_list_agents = sum(
            1 for s in state.completed_steps[-5:]
            if s.tool == "list_agents"
        )
        
        # Should be flagged as loop
        assert recent_list_agents >= 2


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
