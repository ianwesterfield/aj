"""
Unit tests for the memory summarizer module (formerly fact extractor).

Tests the helper functions and response formatting without network calls.
"""

import sys
import os
import pytest
import importlib.util

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

_empty_result = _fact_extractor._empty_result
_clean_summary = _fact_extractor._clean_summary
facts_to_storage_format = _fact_extractor.facts_to_storage_format


class TestEmptyResult:
    """Tests for _empty_result."""

    def test_returns_empty_structure(self):
        """Empty result has None summary and empty facts list."""
        result = _empty_result()
        assert result == {"summary": None, "facts": []}

    def test_summary_is_none(self):
        """Empty result has None summary."""
        result = _empty_result()
        assert result["summary"] is None

    def test_facts_is_empty_list(self):
        """Empty result has empty facts list."""
        result = _empty_result()
        assert result["facts"] == []


class TestCleanSummary:
    """Tests for _clean_summary helper."""

    def test_strips_whitespace(self):
        """Whitespace is trimmed."""
        assert _clean_summary("  User lives in Austin  ") == "User lives in Austin"

    def test_removes_summary_prefix(self):
        """Removes 'Summary:' prefix if present."""
        result = _clean_summary("Summary: User prefers dark mode")
        assert result == "User prefers dark mode"

    def test_removes_summary_prefix_case_insensitive(self):
        """Removes 'summary:' prefix case-insensitively."""
        result = _clean_summary("SUMMARY: User is named Ian")
        assert result == "User is named Ian"

    def test_removes_quotes(self):
        """Removes surrounding quotes."""
        result = _clean_summary('"User has a wife named Sarah"')
        assert result == "User has a wife named Sarah"

    def test_rejects_too_short(self):
        """Rejects summaries shorter than 10 chars."""
        assert _clean_summary("Short") == ""
        assert _clean_summary("123456789") == ""

    def test_accepts_exact_minimum(self):
        """Accepts summaries at exactly 10 chars."""
        result = _clean_summary("1234567890")
        assert result == "1234567890"

    def test_rejects_nothing_to_remember(self):
        """Rejects generic 'nothing to remember' responses."""
        assert _clean_summary("There is nothing to remember here") == ""
        assert _clean_summary("No personal information was found") == ""

    def test_rejects_general_question_note(self):
        """Rejects 'general question' responses."""
        assert _clean_summary("This is a general question") == ""

    def test_rejects_no_preferences(self):
        """Rejects 'no preferences' responses."""
        assert _clean_summary("No preferences were mentioned") == ""

    def test_accepts_valid_summary(self):
        """Accepts valid summaries with real content."""
        summary = "User's name is Ian, lives in Austin, has wife Sarah"
        result = _clean_summary(summary)
        assert result == summary


class TestFactsToStorageFormat:
    """Tests for facts_to_storage_format conversion."""

    def test_extracts_facts_list(self):
        """Extracts facts from summary result."""
        result = {
            "summary": "User is named Ian",
            "facts": [{"type": "memory", "value": "User is named Ian"}],
        }
        storage = facts_to_storage_format(result)

        assert len(storage) == 1
        assert storage[0] == {"type": "memory", "value": "User is named Ian"}

    def test_empty_facts(self):
        """Empty facts returns empty list."""
        result = {"summary": None, "facts": []}
        storage = facts_to_storage_format(result)
        assert storage == []

    def test_missing_facts_key(self):
        """Missing facts key returns empty list."""
        result = facts_to_storage_format({})
        assert result == []

    def test_multiple_facts(self):
        """Multiple facts are all returned."""
        result = {
            "summary": "Multiple items",
            "facts": [
                {"type": "memory", "value": "Fact 1"},
                {"type": "memory", "value": "Fact 2"},
            ],
        }
        storage = facts_to_storage_format(result)

        assert len(storage) == 2


# NOTE: summarize_for_memory() is async and makes network calls.
# See test_e2e_fact_extractor.py for integration tests of that function.
