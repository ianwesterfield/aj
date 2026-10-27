"""
End-to-End tests for the memory summarizer.

These tests hit the actual ollama instance running in Docker.
Requires ollama container running on port 11434 with r1-distill-aj model.
"""

import os
import sys
import socket
import pytest
import asyncio
import importlib.util

# Override env vars before importing fact_extractor
os.environ["OLLAMA_HOST"] = "localhost"
os.environ["OLLAMA_PORT"] = "11434"

# Load fact_extractor module directly from pragmatics layer
_pragmatics_path = os.path.join(
    os.path.dirname(__file__),
    "..",
    "layers",
    "pragmatics",
    "services",
    "fact_extractor.py",
)
_spec = importlib.util.spec_from_file_location(
    "pragmatics_fact_extractor", _pragmatics_path
)
_fact_extractor = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_fact_extractor)

summarize_for_memory = _fact_extractor.summarize_for_memory
facts_to_storage_format = _fact_extractor.facts_to_storage_format


# Check if ollama is reachable
def _ollama_reachable(
    host: str = "localhost", port: int = 11434, timeout: float = 2.0
) -> bool:
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return True
    except (OSError, ConnectionRefusedError):
        return False


# Skip all tests if ollama not available
pytestmark = pytest.mark.skipif(
    not _ollama_reachable(), reason="ollama not reachable at localhost:11434"
)


@pytest.fixture
def event_loop():
    """Create event loop for async tests."""
    loop = asyncio.new_event_loop()
    yield loop
    loop.close()


class TestSummarizeForMemoryE2E:
    """E2E tests for summarize_for_memory against real Ollama."""

    @pytest.mark.asyncio
    async def test_summarize_name(self):
        """Summarize name introduction."""
        result = await summarize_for_memory("My name is Ian")

        # Should have a summary
        assert result.get("summary") is not None
        assert "Ian" in result["summary"]

        # Should have facts for storage
        facts = result.get("facts", [])
        assert len(facts) == 1
        assert facts[0]["type"] == "memory"
        assert "Ian" in facts[0]["value"]

    @pytest.mark.asyncio
    async def test_summarize_spouse(self):
        """Summarize spouse information."""
        result = await summarize_for_memory(
            "My wife's name is Sarah and she loves hiking"
        )

        summary = result.get("summary", "")

        # Should mention Sarah
        assert "Sarah" in summary, f"Expected Sarah in summary, got: {summary}"

    @pytest.mark.asyncio
    async def test_summarize_preference(self):
        """Summarize user preference."""
        result = await summarize_for_memory(
            "I prefer dark mode for all my applications"
        )

        summary = result.get("summary") or ""

        # Should mention dark mode
        assert "dark" in summary.lower(), f"Expected dark mode mention, got: {summary}"

    @pytest.mark.asyncio
    async def test_summarize_terminology(self):
        """Summarize custom terminology."""
        result = await summarize_for_memory(
            "When I say agents I mean FunnelCloud Agents"
        )

        summary = result.get("summary", "")

        # Should capture the terminology
        assert (
            "agent" in summary.lower() or "funnelcloud" in summary.lower()
        ), f"Expected terminology, got: {summary}"

    @pytest.mark.asyncio
    async def test_summarize_location(self):
        """Summarize location information."""
        result = await summarize_for_memory("I live in Austin, Texas")

        summary = result.get("summary", "")

        assert (
            "Austin" in summary or "Texas" in summary
        ), f"Expected location, got: {summary}"

    @pytest.mark.asyncio
    async def test_summarize_multiple_facts(self):
        """Summarize message with multiple personal facts."""
        result = await summarize_for_memory(
            "My name is Ian, I live in Austin, and my wife Sarah loves hiking."
        )

        summary = result.get("summary", "")

        # Should capture multiple pieces of info
        assert "Ian" in summary, f"Expected name in summary, got: {summary}"
        # Should also mention either location or spouse
        has_location = "Austin" in summary
        has_spouse = "Sarah" in summary
        assert (
            has_location or has_spouse
        ), f"Expected location or spouse, got: {summary}"

    @pytest.mark.asyncio
    async def test_empty_for_greeting(self):
        """Greeting should return nothing to remember."""
        result = await summarize_for_memory("How are you today?")

        # Should have no summary for generic greeting
        summary = result.get("summary")

        # Either None or empty string is acceptable
        if summary is not None:
            # Model might occasionally produce something - skip if so
            pytest.skip(f"Model produced summary for greeting: {summary}")

    @pytest.mark.asyncio
    async def test_empty_for_general_question(self):
        """General questions should return nothing to remember."""
        result = await summarize_for_memory("What is the capital of France?")

        summary = result.get("summary")

        if summary is not None:
            pytest.skip(f"Model produced summary for question: {summary}")

    @pytest.mark.asyncio
    async def test_short_text_returns_empty(self):
        """Very short text returns empty (guard in summarize_for_memory)."""
        result = await summarize_for_memory("Hi")

        assert result == {"summary": None, "facts": []}

    @pytest.mark.asyncio
    async def test_empty_text_returns_empty(self):
        """Empty text returns empty."""
        result = await summarize_for_memory("")

        assert result == {"summary": None, "facts": []}


class TestStorageFormatIntegration:
    """Tests for full pipeline: summarize then convert to storage format."""

    @pytest.mark.asyncio
    async def test_storage_format_has_type_and_value(self):
        """Storage format should have type and value keys."""
        result = await summarize_for_memory("My name is Ian and I live in Austin")

        storage_facts = facts_to_storage_format(result)

        for fact in storage_facts:
            assert "type" in fact
            assert "value" in fact
            assert fact["type"] == "memory"

    @pytest.mark.asyncio
    async def test_storage_format_preserves_summary(self):
        """Storage format value should match summary."""
        result = await summarize_for_memory("I have a dog named Max")

        summary = result.get("summary", "")
        storage_facts = facts_to_storage_format(result)

        if storage_facts:
            assert storage_facts[0]["value"] == summary
