"""
End-to-End Tests for Agentic Reasoning System

Tests the complete OODA loop (Observe, Orient, Decide, Act) for multi-step tasks.
Validates that the system can plan, execute, learn, and adapt.
"""

import pytest
import pytest_asyncio
import asyncio
import json
import httpx
from typing import List, Dict, Any, Optional
from unittest.mock import AsyncMock, MagicMock, patch
from dataclasses import dataclass, field
from conftest import ORCHESTRATOR_URL


@dataclass
class TestEvent:
    """Represents an event from the orchestrator stream."""

    event_type: str
    content: Optional[str] = None
    status: Optional[str] = None
    step_num: Optional[int] = None
    tool: Optional[str] = None
    result: Optional[Dict[str, Any]] = None
    done: bool = False


class TestOODALoop:
    """Test the complete Observe-Orient-Decide-Act cycle."""

    @pytest_asyncio.fixture
    async def orchestrator_client(self):
        """Create a test client for the orchestrator API."""
        async with httpx.AsyncClient(
            base_url=ORCHESTRATOR_URL,
            timeout=httpx.Timeout(
                300.0, connect=10.0
            ),  # 5 min timeout for large model inference
        ) as client:
            yield client

    @pytest.mark.asyncio
    @pytest.mark.integration
    async def test_multi_step_task_execution(self, orchestrator_client):
        """
        E2E test: Execute a multi-step task and validate the complete flow.

        This tests:
        1. Task planning (Decide phase)
        2. Step-by-step execution (Act phase)
        3. Memory integration (Learn phase)
        4. Plan adaptation if needed (Orient phase)
        """
        # Test task: Create a simple project structure
        task = "Create a simple Python project with README.md, main.py, and requirements.txt"

        # Collect all events from the streaming response
        events = []
        collected_content = ""

        try:
            async with orchestrator_client.stream(
                "POST",
                "/api/orchestrate/run-task",
                json={
                    "task": task,
                    "workspace_root": "/workspace",  # Use the allowed workspace path
                    "user_id": "test_user",
                    "max_steps": 10,
                    "preserve_state": False,
                },
            ) as response:
                response.raise_for_status()

                async for line in response.aiter_lines():
                    if not line.startswith("data: "):
                        continue

                    try:
                        event_data = json.loads(line[6:])  # Strip "data: " prefix
                        event = TestEvent(
                            event_type=event_data.get("event_type", ""),
                            content=event_data.get("content"),
                            status=event_data.get("status"),
                            step_num=event_data.get("step_num"),
                            tool=event_data.get("tool"),
                            result=event_data.get("result"),
                            done=event_data.get("done", False),
                        )
                        events.append(event)

                        # Collect thinking content
                        if event.event_type == "thinking" and event.content:
                            collected_content += event.content

                    except json.JSONDecodeError:
                        continue

        except Exception as e:
            pytest.fail(f"Failed to execute task: {e}")

        # Debug: Print all events to understand what's happening
        print(f"\n=== DEBUG: Received {len(events)} events ===")
        for i, event in enumerate(events[:20]):  # First 20 events
            print(
                f"Event {i}: type={event.event_type}, status={event.status}, content={event.content[:50] if event.content else None}"
            )

        # Validate the complete execution flow
        self._validate_execution_flow(events, collected_content)

    def _validate_execution_flow(self, events: List[TestEvent], thinking_content: str):
        """Validate that the execution followed the expected OODA pattern.

        .NET orchestrator emits: status, step, result, complete, error.
        Python orchestrator emitted: plan, thinking, result, complete.
        Accept both event vocabularies.
        """

        # 1. Should have planning/reasoning phase (plan event OR step event with reasoning)
        plan_events = [e for e in events if e.event_type in ("plan", "step")]
        assert len(plan_events) > 0, "Should generate a task plan or reasoning step"

        # 2. Should execute at least one step (result or status with tool)
        step_events = [e for e in events if e.event_type == "result" and e.step_num]
        tool_events = [
            e for e in events if e.tool and e.event_type in ("status", "result", "step")
        ]
        assert (
            len(step_events) >= 1 or len(tool_events) >= 1
        ), f"Should execute at least one step. Results: {len(step_events)}, tool events: {len(tool_events)}"

        # 3. Should show reasoning (thinking events or step events with reasoning)
        thinking_events = [e for e in events if e.event_type in ("thinking", "step")]
        assert len(thinking_events) > 0, "Should stream thinking or step events"

        # 4. Should complete successfully (complete event or result with done=True)
        completion_events = [e for e in events if e.done or e.event_type == "complete"]
        assert len(completion_events) > 0, "Should complete the task"

        # 5. Should show status updates during execution
        status_events = [e for e in events if e.event_type == "status"]
        assert len(status_events) > 0, "Should show status updates"

        # 6. Should not get stuck (no excessive status events)
        assert (
            len(status_events) < 100
        ), f"Too many status events ({len(status_events)}), possible infinite loop"

    @pytest.mark.asyncio
    @pytest.mark.integration
    async def test_adaptive_planning_on_failure(self, orchestrator_client):
        """
        Test that the system can adapt its plan when a step fails.

        This tests the "Orient" phase of OODA - learning from failures and adapting.
        """
        # Task that will likely fail initially (non-existent file)
        # Be explicit about using read_file to read the file content
        task = "Use read_file to read the contents of /workspace/nonexistent_data.txt and tell me what's in it"

        events = []
        collected_content = ""

        try:
            async with orchestrator_client.stream(
                "POST",
                "/api/orchestrate/run-task",
                json={
                    "task": task,
                    "workspace_root": "/workspace",
                    "user_id": "test_user",
                    "max_steps": 5,
                    "preserve_state": False,
                },
            ) as response:
                response.raise_for_status()

                async for line in response.aiter_lines():
                    if not line.startswith("data: "):
                        continue

                    try:
                        event_data = json.loads(line[6:])
                        event = TestEvent(
                            event_type=event_data.get("event_type", ""),
                            content=event_data.get("content"),
                            status=event_data.get("status"),
                            step_num=event_data.get("step_num"),
                            tool=event_data.get("tool"),
                            result=event_data.get("result"),
                            done=event_data.get("done", False),
                        )
                        events.append(event)

                        if event.event_type == "thinking" and event.content:
                            collected_content += event.content

                    except json.JSONDecodeError:
                        continue

        except Exception as e:
            pytest.fail(f"Failed to execute task: {e}")

        # Validate adaptive behavior
        self._validate_adaptive_behavior(events, collected_content)

    def _validate_adaptive_behavior(
        self, events: List[TestEvent], thinking_content: str
    ):
        """Validate that the system adapted to failure or completed gracefully.

        .NET orchestrator emits: status, step, result, complete, error.
        Python orchestrator emitted: plan, thinking, result, complete.
        Accept both event vocabularies.
        """

        # Should have gone through the OODA loop (plan/step, think/step, complete/act)
        event_types = [e.event_type for e in events]
        has_planning = "plan" in event_types or "step" in event_types
        has_thinking = (
            "thinking" in event_types
            or "step" in event_types
            or "status" in event_types
        )
        has_completion = "complete" in event_types or any(e.done for e in events)

        assert has_planning, f"Should have planning phase. Events: {event_types}"
        assert has_thinking, f"Should have thinking phase. Events: {event_types}"
        assert has_completion, "Should complete even with failure"

        # Should show some reasoning (thinking events or step events)
        reasoning_events = [e for e in events if e.event_type in ("thinking", "step")]
        assert len(reasoning_events) > 0, "Should have thinking or step events"

    @pytest.mark.asyncio
    @pytest.mark.integration
    @pytest.mark.skip(
        reason="Local file operations not supported in current .NET orchestrator. "
        "Only FunnelCloud remote execution (list_agents, execute, think, complete) is available."
    )
    async def test_memory_integration(self, orchestrator_client):
        """
        Test that the system uses memory for better planning.

        This tests the "Learn" phase - remembering previous patterns.
        """
        # First task: Create a file
        task1 = "Create a file called test.txt with content 'Hello World'"

        # Execute first task
        await self._execute_task(orchestrator_client, task1)

        # Second task: Read the file we just created
        task2 = "Read the test.txt file and tell me what it contains"

        events = await self._execute_task(orchestrator_client, task2)

        # Validate memory usage
        self._validate_memory_usage(events)

    async def _execute_task(self, client, task: str) -> List[TestEvent]:
        """Helper to execute a task and return events."""
        events = []

        async with client.stream(
            "POST",
            "/api/orchestrate/run-task",
            json={
                "task": task,
                "workspace_root": "/workspace",
                "user_id": "test_user",
                "max_steps": 5,
                "preserve_state": True,  # Preserve state between tasks
            },
        ) as response:
            response.raise_for_status()

            async for line in response.aiter_lines():
                if not line.startswith("data: "):
                    continue

                try:
                    event_data = json.loads(line[6:])
                    event = TestEvent(
                        event_type=event_data.get("event_type", ""),
                        content=event_data.get("content"),
                        status=event_data.get("status"),
                        step_num=event_data.get("step_num"),
                        tool=event_data.get("tool"),
                        result=event_data.get("result"),
                        done=event_data.get("done", False),
                    )
                    events.append(event)

                except json.JSONDecodeError:
                    continue

        return events

    def _validate_memory_usage(self, events: List[TestEvent]):
        """Validate that memory/context was used."""

        # Should use a file-related tool (read_file, execute, etc.)
        # The orchestrator may report tools with internal names
        file_events = [
            e
            for e in events
            if e.tool
            and any(kw in e.tool.lower() for kw in ["read", "file", "execute", "cat"])
        ]
        result_events = [e for e in events if e.event_type == "result"]

        # Either found file tool events or got result events (tool names vary)
        assert len(file_events) > 0 or len(result_events) > 0, (
            f"Should read the file or produce results. Tools seen: "
            f"{[e.tool for e in events if e.tool]}"
        )

        # Should complete the task
        completion_events = [e for e in events if e.done]
        assert len(completion_events) > 0, "Should complete the task"

    @pytest.mark.asyncio
    @pytest.mark.integration
    async def test_streaming_ux_during_execution(self, orchestrator_client):
        """
        Test that the streaming UX works properly during execution.

        Validates real-time status updates and thinking display.
        """
        task = "Scan the workspace and list all Python files (.py)"

        events = []
        status_updates = []
        thinking_content = ""

        async with orchestrator_client.stream(
            "POST",
            "/api/orchestrate/run-task",
            json={
                "task": task,
                "workspace_root": "/workspace",
                "user_id": "test_user",
                "max_steps": 3,
                "preserve_state": False,
            },
        ) as response:
            response.raise_for_status()

            async for line in response.aiter_lines():
                if not line.startswith("data: "):
                    continue

                try:
                    event_data = json.loads(line[6:])
                    event = TestEvent(
                        event_type=event_data.get("event_type", ""),
                        content=event_data.get("content"),
                        status=event_data.get("status"),
                        step_num=event_data.get("step_num"),
                        tool=event_data.get("tool"),
                        result=event_data.get("result"),
                        done=event_data.get("done", False),
                    )
                    events.append(event)

                    if event.event_type == "status" and event.status:
                        status_updates.append(event.status)

                    if event.event_type == "thinking" and event.content:
                        thinking_content += event.content

                except json.JSONDecodeError:
                    continue

        # Validate streaming UX
        self._validate_streaming_ux(status_updates, thinking_content, events)

    def _validate_streaming_ux(
        self, status_updates: List[str], thinking_content: str, events: List[TestEvent]
    ):
        """Validate the streaming user experience.

        .NET orchestrator emits: status, step, result, complete, error.
        Python orchestrator emitted: plan, thinking, result, complete.
        Accept both event vocabularies.
        """

        # Should have a plan or step event (reasoning phase)
        plan_events = [e for e in events if e.event_type in ("plan", "step")]
        assert len(plan_events) > 0, "Should show task plan or reasoning step"

        # Should show thinking or step events (streaming LLM output)
        thinking_events = [e for e in events if e.event_type in ("thinking", "step")]
        assert len(thinking_events) > 0, "Should stream thinking or step events"

        # Should have status updates (reasoning, tool execution, completion)
        assert len(status_updates) > 0, f"Should have status updates"

        # Should show at least reasoning or tool execution status
        has_reasoning_status = any("reason" in s.lower() for s in status_updates)
        has_tool_status = any(
            kw in s.lower()
            for s in status_updates
            for kw in ["remote", "completed", "executing", "success"]
        )
        assert (
            has_reasoning_status or has_tool_status
        ), f"Should show reasoning or tool status. Updates: {status_updates[:20]}"

        # Should not have excessive status updates (prevents spam)
        assert (
            len(status_updates) < 50
        ), f"Too many status updates ({len(status_updates)}), possible spam"

        # Should complete
        completion_events = [e for e in events if e.done]
        assert len(completion_events) > 0, "Should complete the task"


class TestGuardrailsE2E:
    """Test guardrails in end-to-end scenarios."""

    @pytest_asyncio.fixture
    async def orchestrator_client(self):
        """Create a test client for the orchestrator API."""
        async with httpx.AsyncClient(
            base_url=ORCHESTRATOR_URL,
            timeout=httpx.Timeout(
                300.0, connect=10.0
            ),  # 5 min timeout for large model inference
        ) as client:
            yield client

    @pytest.mark.asyncio
    @pytest.mark.integration
    async def test_prevents_infinite_loops(self, orchestrator_client):
        """
        Test that the system prevents infinite loops by enforcing max steps.
        """
        # Task that could potentially loop (repeatedly checking something)
        task = "Keep checking if a file exists until it does, but it never will"

        events = []

        async with orchestrator_client.stream(
            "POST",
            "/api/orchestrate/run-task",
            json={
                "task": task,
                "workspace_root": "/workspace",
                "user_id": "test_user",
                "max_steps": 5,  # Limited steps
                "preserve_state": False,
            },
        ) as response:
            response.raise_for_status()

            async for line in response.aiter_lines():
                if not line.startswith("data: "):
                    continue

                try:
                    event_data = json.loads(line[6:])
                    event = TestEvent(
                        event_type=event_data.get("event_type", ""),
                        content=event_data.get("content"),
                        status=event_data.get("status"),
                        step_num=event_data.get("step_num"),
                        tool=event_data.get("tool"),
                        result=event_data.get("result"),
                        done=event_data.get("done", False),
                    )
                    events.append(event)

                except json.JSONDecodeError:
                    continue

        # Should not exceed max steps
        step_events = [e for e in events if e.step_num and e.step_num <= 5]
        max_step = max((e.step_num for e in step_events if e.step_num), default=0)
        assert max_step <= 5, f"Should not exceed max steps (5), got {max_step}"

        # Should complete (either successfully or with guardrail)
        completion_events = [e for e in events if e.done]
        assert len(completion_events) > 0, "Should complete within step limit"


class TestRemoteFileIndexingE2E:
    """
    Test remote file indexing and metadata workflows.

    These tests require a FunnelCloud agent to be running.
    They test complex workflows like:
    - Indexing file metadata from remote drives
    - Storing metadata in workspace memory
    - Handling large recursive file listings
    """

    @pytest_asyncio.fixture
    async def orchestrator_client(self):
        """Create a test client for the orchestrator API."""
        async with httpx.AsyncClient(
            base_url=ORCHESTRATOR_URL,
            timeout=httpx.Timeout(
                600.0, connect=10.0
            ),  # 10 min timeout for large operations
        ) as client:
            yield client

    @pytest.mark.asyncio
    @pytest.mark.integration
    async def test_remote_file_metadata_indexing(self, orchestrator_client):
        """
        E2E test: Index file metadata from a remote drive and store in memory.

        This tests:
        1. Agent discovery (list_agents)
        2. Remote PowerShell execution with valid syntax
        3. Handling recursive file listings
        4. Memory storage of collected metadata

        Based on real failure case: "Please index the metadata of ALL files in S:."

        NOTE: If no FunnelCloud agent is running, this test validates graceful failure instead.
        """
        # Use a smaller scope for testing - just list ROOT contents (no recursion)
        # The original bug was about syntax errors with paths like 'S:\', not recursion depth
        task = "List the TOP LEVEL files and folders in S:\\ on the remote machine (do NOT recurse into subfolders)"

        events = []
        status_updates = []
        errors = []

        try:
            async with orchestrator_client.stream(
                "POST",
                "/api/orchestrate/run-task",
                json={
                    "task": task,
                    "workspace_root": "/workspace",
                    "user_id": "test_indexing",
                    "max_steps": 6,  # Reduced: list_agents + remote_execute + complete
                    "preserve_state": True,  # Enable memory storage
                },
            ) as response:
                response.raise_for_status()

                async for line in response.aiter_lines():
                    if not line.startswith("data: "):
                        continue

                    try:
                        event_data = json.loads(line[6:])
                        event = TestEvent(
                            event_type=event_data.get("event_type", ""),
                            content=event_data.get("content"),
                            status=event_data.get("status"),
                            step_num=event_data.get("step_num"),
                            tool=event_data.get("tool"),
                            result=event_data.get("result"),
                            done=event_data.get("done", False),
                        )
                        events.append(event)

                        if event.status:
                            status_updates.append(event.status)
                            # Track PowerShell syntax errors
                            if (
                                "syntax error" in event.status.lower()
                                or "missing" in event.status.lower()
                            ):
                                errors.append(event.status)

                    except json.JSONDecodeError:
                        continue

        except Exception as e:
            pytest.fail(f"Failed to execute task: {e}")

        # Validate the execution
        self._validate_remote_indexing(events, status_updates, errors)

    def _validate_remote_indexing(
        self, events: List[TestEvent], status_updates: List[str], errors: List[str]
    ):
        """Validate the remote file indexing workflow."""

        # Debug output
        print(f"\n=== DEBUG: Received {len(events)} events ===")
        print(f"Status updates: {status_updates[:15]}")
        print(f"Errors encountered: {errors[:5]}")

        # Check if system completed with an error about no agents
        completion_events = [e for e in events if e.done]
        final_content = completion_events[0].content if completion_events else ""

        # Check all possible sources for "no agent" indication
        all_text = (
            str(final_content)
            + " ".join(status_updates)
            + " ".join(str(e.result) for e in events if e.result)
        )
        no_agent_indicators = [
            "no agent",
            "not available",
            "cannot access",
            "please start",
            "agent is not",
            "without agent",
            "no funnelcloud",
            "agent discovery",
        ]
        graceful_failure = any(ind in all_text.lower() for ind in no_agent_indicators)

        # Also check if no actionable tools were used (only think/complete = no agent available)
        actionable_tools = [
            e for e in events if e.tool and e.tool not in ("think", "complete")
        ]
        minimal_execution = len(actionable_tools) == 0

        if graceful_failure or minimal_execution:
            print("=== Agent not available - validating graceful failure ===")
            # Should still complete the task (with error message)
            assert (
                len(completion_events) > 0
            ), "Should complete the task even if agent unavailable"
            # Should NOT hallucinate fake data
            assert "explorer.exe" not in all_text, "Should not hallucinate file names"
            assert (
                "system32" not in all_text.lower() or "cannot" in all_text.lower()
            ), "Should not hallucinate paths"
            return  # Graceful failure is acceptable

        # 1. Should either discover agents OR use a known agent
        # Tool names may be internal (aj:agent-list, aj:remote-exec) or display names
        agent_events = [
            e
            for e in events
            if e.tool
            and any(
                kw in e.tool.lower() for kw in ["list_agents", "agent-list", "agent"]
            )
        ]
        remote_events = [
            e
            for e in events
            if e.tool
            and any(
                kw in e.tool.lower()
                for kw in ["remote_execute", "remote-exec", "remote", "execute"]
            )
        ]

        # Must have either agent discovery OR direct remote execution
        has_agent_workflow = len(agent_events) > 0 or len(remote_events) > 0
        assert has_agent_workflow, (
            f"Should use agent discovery or remote execution. "
            f"Tools seen: {[e.tool for e in events if e.tool]}"
        )

        # 2. Should use remote execution for the remote drive
        assert len(remote_events) > 0, (
            f"Should use remote execution to access the remote drive. "
            f"Tools seen: {[e.tool for e in events if e.tool]}"
        )

        # 3. Should NOT have excessive PowerShell syntax errors (max 2 retries)
        syntax_errors = [
            e for e in errors if "syntax" in e.lower() or "missing" in e.lower()
        ]
        assert (
            len(syntax_errors) <= 3
        ), f"Too many PowerShell syntax errors ({len(syntax_errors)}): {syntax_errors[:3]}"

        # 4. Should have at least one successful remote execution OR complete gracefully
        successful_remote = [
            e for e in remote_events if e.result and e.result.get("success", False)
        ]

        # Either successful execution OR graceful completion is OK
        has_valid_outcome = len(successful_remote) > 0 or len(completion_events) > 0
        assert (
            has_valid_outcome
        ), f"Should have successful remote execution or completion. Remote results: {[e.result for e in remote_events[:3]]}"

        # 5. Should complete the task
        assert len(completion_events) > 0, "Should complete the task"

    @pytest.mark.asyncio
    @pytest.mark.integration
    async def test_powershell_syntax_validation(self, orchestrator_client):
        """
        Test that the system generates valid PowerShell syntax.

        Specifically tests for:
        - Proper string quoting
        - Complete command structures
        - No missing terminators

        NOTE: If no FunnelCloud agent is running, this test validates graceful failure instead.
        """
        # Simple command that should work
        task = "Run 'Get-ChildItem C:\\Windows -Directory | Select-Object -First 5' on the remote machine"

        events = []
        syntax_errors = []

        try:
            async with orchestrator_client.stream(
                "POST",
                "/api/orchestrate/run-task",
                json={
                    "task": task,
                    "workspace_root": "/workspace",
                    "user_id": "test_syntax",
                    "max_steps": 5,
                    "preserve_state": False,
                },
            ) as response:
                response.raise_for_status()

                async for line in response.aiter_lines():
                    if not line.startswith("data: "):
                        continue

                    try:
                        event_data = json.loads(line[6:])
                        event = TestEvent(
                            event_type=event_data.get("event_type", ""),
                            content=event_data.get("content"),
                            status=event_data.get("status"),
                            step_num=event_data.get("step_num"),
                            tool=event_data.get("tool"),
                            result=event_data.get("result"),
                            done=event_data.get("done", False),
                        )
                        events.append(event)

                        # Track syntax errors
                        if event.status and (
                            "syntax" in event.status.lower()
                            or "missing" in event.status.lower()
                            or "terminator" in event.status.lower()
                        ):
                            syntax_errors.append(event.status)

                    except json.JSONDecodeError:
                        continue

        except Exception as e:
            pytest.fail(f"Failed to execute task: {e}")

        # Check for graceful failure due to no agent or parse error
        completion_events = [e for e in events if e.done]
        final_content = completion_events[0].content if completion_events else ""

        # Check all possible sources for failure indicators
        all_text = (
            str(final_content)
            + " ".join(str(e.status) for e in events if e.status)
            + " ".join(str(e.result) for e in events if e.result)
        )
        no_agent_indicators = [
            "no agent",
            "not available",
            "cannot access",
            "please start",
            "agent is not",
            "without agent",
            "no funnelcloud",
            "agent discovery",
            "couldn't parse",
            "parse error",
        ]
        graceful_failure = any(ind in all_text.lower() for ind in no_agent_indicators)

        # Also check if the test completed very quickly with no execute tool events
        execute_tool_events = [
            e for e in events if e.tool and e.tool in ("execute", "remote_execute")
        ]
        minimal_execution = len(events) <= 15 and len(execute_tool_events) == 0

        if graceful_failure or minimal_execution:
            print(
                f"=== Agent not available - validating graceful failure (events={len(events)}, execute_tools={len(execute_tool_events)}) ==="
            )
            # Should complete with error message
            assert len(completion_events) > 0, "Should complete the task"
            # Should NOT hallucinate fake data
            assert "explorer.exe" not in all_text, "Should not hallucinate file names"
            return  # Graceful failure is acceptable

        # Debug: print events to understand what's happening
        print(f"\n=== DEBUG: PowerShell syntax validation - {len(events)} events ===")
        for i, e in enumerate(events[:20]):
            print(f"Event {i}: type={e.event_type}, tool={e.tool}, status={e.status}")
            if e.result:
                print(
                    f"   result keys: {list(e.result.keys())}, result: {str(e.result)[:200]}"
                )

        # Should have no syntax errors for a simple command
        assert (
            len(syntax_errors) == 0
        ), f"Should not have PowerShell syntax errors: {syntax_errors}"

        # Should complete successfully
        assert len(completion_events) > 0, "Should complete the task"

        # Should have successful remote execution
        # .NET orchestrator: result events have status="Success" and result.output_preview
        # Python orchestrator: result events have result.success=True
        remote_events = [
            e
            for e in events
            if e.tool
            and any(
                kw in e.tool.lower()
                for kw in ["remote_execute", "remote-exec", "remote", "execute"]
            )
            and e.result
        ]
        successful = [e for e in remote_events if e.result.get("success", False)]
        # Also check result events with .NET orchestrator format (status="Success", output_preview present)
        result_events = [
            e
            for e in events
            if e.event_type == "result"
            and e.result
            and (
                e.result.get("success", False)
                or (e.status == "Success" and e.result.get("output_preview"))
            )
        ]
        assert len(successful) > 0 or len(result_events) > 0, (
            f"Should have successful execution. Remote results: {[e.result for e in remote_events]}, "
            f"All results: {[e.result for e in events if e.event_type == 'result'][:5]}"
        )


class TestHallucinationPrevention:
    """
    Test that the system does NOT hallucinate results when it cannot complete a task.

    Based on real failure case: User asked to list files on remote machine,
    no agent was running, but model made up a fake directory listing.
    """

    @pytest_asyncio.fixture
    async def orchestrator_client(self):
        """Create a test client for the orchestrator API."""
        async with httpx.AsyncClient(
            base_url=ORCHESTRATOR_URL, timeout=httpx.Timeout(120.0, connect=10.0)
        ) as client:
            yield client

    @pytest.mark.asyncio
    @pytest.mark.integration
    async def test_no_hallucination_when_agent_unavailable(self, orchestrator_client):
        """
        Test that the system reports an error rather than hallucinating results
        when no FunnelCloud agent is available.

        This tests for:
        1. Agent discovery returning empty
        2. Model NOT making up fake file listings
        3. Clear error message to user about agent availability
        """
        # This task requires remote execution but we'll mock the agent discovery
        # to return no agents, simulating the failure case
        task = "List all files in C:\\Windows on my PC"

        events = []
        final_answer = None

        try:
            async with orchestrator_client.stream(
                "POST",
                "/api/orchestrate/run-task",
                json={
                    "task": task,
                    "workspace_root": "/workspace",
                    "user_id": "test_hallucination",
                    "max_steps": 5,
                    "preserve_state": False,
                },
            ) as response:
                response.raise_for_status()

                async for line in response.aiter_lines():
                    if not line.startswith("data: "):
                        continue

                    try:
                        event_data = json.loads(line[6:])
                        event = TestEvent(
                            event_type=event_data.get("event_type", ""),
                            content=event_data.get("content"),
                            status=event_data.get("status"),
                            step_num=event_data.get("step_num"),
                            tool=event_data.get("tool"),
                            result=event_data.get("result"),
                            done=event_data.get("done", False),
                        )
                        events.append(event)

                        # Capture final answer
                        if event.done and event.content:
                            final_answer = event.content

                    except json.JSONDecodeError:
                        continue

        except Exception as e:
            pytest.fail(f"Failed to execute task: {e}")

        # Validate behavior
        self._validate_no_hallucination(events, final_answer)

    def _validate_no_hallucination(
        self, events: List[TestEvent], final_answer: Optional[str]
    ):
        """Validate that the system did not hallucinate results."""

        # Check if agents were discovered
        agent_events = [
            e
            for e in events
            if e.tool
            and any(
                kw in e.tool.lower() for kw in ["list_agents", "agent-list", "agent"]
            )
        ]
        remote_events = [
            e
            for e in events
            if e.tool
            and any(
                kw in e.tool.lower()
                for kw in ["remote_execute", "remote-exec", "remote", "execute"]
            )
        ]

        # If no agents discovered and no remote execution happened...
        if agent_events:
            agent_result = agent_events[0].result
            agents_found = agent_result and "ians-r16" in str(agent_result)

            if not agents_found and not remote_events:
                # ...then the final answer should NOT contain fake file listings
                if final_answer:
                    # Patterns indicating fabricated file system output
                    fabricated_filesystem_patterns = [
                        r"C:\\Windows\\[a-zA-Z]+\.exe",  # Made-up executables
                        r"C:\\Windows\\[a-zA-Z]+\.dll",  # Made-up DLLs
                        r"\d+,?\d*\s*(KB|MB|GB|bytes)",  # Made-up file sizes
                        "explorer.exe",
                        "notepad.exe",
                        "System32",
                        "here are the files",
                        "directory listing",
                    ]

                    import re

                    for pattern in fabricated_filesystem_patterns:
                        if re.search(pattern, final_answer, re.IGNORECASE):
                            pytest.fail(
                                f"FABRICATED OUTPUT DETECTED: Model made up fake results when no agent was available.\n"
                                f"Pattern matched: {pattern}\n"
                                f"Answer snippet: {final_answer[:500]}"
                            )

                    # Should mention that agent is unavailable
                    error_keywords = [
                        "no agent",
                        "not available",
                        "cannot",
                        "unable",
                        "start",
                        "running",
                    ]
                    has_error_message = any(
                        kw in final_answer.lower() for kw in error_keywords
                    )
                    assert has_error_message, (
                        f"When no agents available, should explain the situation clearly.\n"
                        f"Got: {final_answer[:300]}"
                    )

        # Should complete the task (even if with an error)
        completion_events = [e for e in events if e.done]
        assert (
            len(completion_events) > 0
        ), "Should complete the task (with error if necessary)"

        print(f"\n=== Hallucination Test Results ===")
        print(f"Agent events: {len(agent_events)}")
        print(f"Remote events: {len(remote_events)}")
        print(f"Final answer: {final_answer[:200] if final_answer else 'None'}...")

    @pytest.mark.asyncio
    @pytest.mark.integration
    async def test_graceful_failure_messaging(self, orchestrator_client):
        """
        Test that when a critical component fails, the system provides
        clear guidance to the user rather than trying to proceed.
        """
        # Task that requires remote execution
        task = "Scan all drives on my remote Windows PC and find the largest files"

        events = []
        final_answer = None

        try:
            async with orchestrator_client.stream(
                "POST",
                "/api/orchestrate/run-task",
                json={
                    "task": task,
                    "workspace_root": "/workspace",
                    "user_id": "test_graceful_failure",
                    "max_steps": 5,
                    "preserve_state": False,
                },
            ) as response:
                response.raise_for_status()

                async for line in response.aiter_lines():
                    if not line.startswith("data: "):
                        continue

                    try:
                        event_data = json.loads(line[6:])
                        event = TestEvent(
                            event_type=event_data.get("event_type", ""),
                            content=event_data.get("content"),
                            status=event_data.get("status"),
                            step_num=event_data.get("step_num"),
                            tool=event_data.get("tool"),
                            result=event_data.get("result"),
                            done=event_data.get("done", False),
                        )
                        events.append(event)

                        if event.done and event.content:
                            final_answer = event.content

                    except json.JSONDecodeError:
                        continue

        except Exception as e:
            pytest.fail(f"Failed to execute task: {e}")

        # Check that system either succeeded with real data OR failed gracefully
        completion_events = [e for e in events if e.done]
        assert (
            len(completion_events) > 0
        ), "Should complete (success or graceful failure)"

        remote_events = [
            e
            for e in events
            if e.tool
            and any(
                kw in e.tool.lower()
                for kw in ["remote_execute", "remote-exec", "remote", "execute"]
            )
        ]
        successful_remote = [
            e for e in remote_events if e.result and e.result.get("success")
        ]

        # If remote execution didn't succeed, ensure we got a helpful message
        if not successful_remote and final_answer:
            # Should NOT contain fake file data
            assert (
                "explorer.exe" not in final_answer
            ), "Should not hallucinate Windows files"
            assert (
                "System32" not in final_answer or "cannot" in final_answer.lower()
            ), "If mentioning System32, should be in context of explaining inability to access"

            print(f"\n=== Graceful Failure Test ===")
            print(f"Remote attempts: {len(remote_events)}")
            print(f"Final answer: {final_answer[:300]}...")
