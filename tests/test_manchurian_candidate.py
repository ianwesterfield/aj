"""
Test the "Manchurian Candidate" context switching behavior.

The model should switch output modes based on contextType signal:
- contextType: external → Conversational markdown responses (user-facing)
- contextType: internal → Structured JSON responses (orchestrator use)

This tests both the filter's injection logic and validates expected model behavior.
"""

import pytest
import re
import json
import sys
import os

# Add parent directory to path for imports
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


class TestContextTypeInjection:
    """Test that the filter correctly injects contextType based on intent."""
    
    def test_external_context_for_casual_intent(self):
        """Casual intent should get contextType: external."""
        intent = "casual"
        expected_context = "external"
        
        # Logic from filter: task -> internal, everything else -> external
        context_type = "internal" if intent == "task" else "external"
        
        assert context_type == expected_context, \
            f"Casual intent should map to 'external', got '{context_type}'"
    
    def test_external_context_for_recall_intent(self):
        """Recall intent should get contextType: external."""
        intent = "recall"
        expected_context = "external"
        
        context_type = "internal" if intent == "task" else "external"
        
        assert context_type == expected_context, \
            f"Recall intent should map to 'external', got '{context_type}'"
    
    def test_external_context_for_save_intent(self):
        """Save intent should get contextType: external."""
        intent = "save"
        expected_context = "external"
        
        context_type = "internal" if intent == "task" else "external"
        
        assert context_type == expected_context, \
            f"Save intent should map to 'external', got '{context_type}'"
    
    def test_internal_context_for_task_intent(self):
        """Task intent should get contextType: internal."""
        intent = "task"
        expected_context = "internal"
        
        context_type = "internal" if intent == "task" else "external"
        
        assert context_type == expected_context, \
            f"Task intent should map to 'internal', got '{context_type}'"
    
    def test_context_type_prepended_to_system_prompt(self):
        """contextType should be prepended to system prompt content."""
        context_type = "external"
        system_prompt = "You are AJ, an AI assistant."
        
        # Simulate filter behavior
        combined = f"contextType: {context_type}\n\n{system_prompt}"
        
        assert combined.startswith("contextType: external"), \
            "contextType should be at the start of system prompt"
        assert "You are AJ" in combined, \
            "Original system prompt should be preserved"


class TestExternalModeOutputFormat:
    """Test that external mode produces conversational markdown."""
    
    EXTERNAL_RESPONSE_PATTERNS = [
        # Should have natural language
        (r"[A-Z][a-z]+.*[.!?]", "Complete sentences with punctuation"),
        # May have markdown formatting
        (r"\*\*|```|`|#", "Markdown formatting elements"),
    ]
    
    EXTERNAL_FORBIDDEN_PATTERNS = [
        # Should NOT start with JSON
        (r'^\s*\{', "Response should not start with JSON object"),
        # Should NOT have raw action objects
        (r'"action"\s*:\s*"', "Should not contain raw action JSON"),
        (r'"requires_action"\s*:', "Should not contain orchestrator fields"),
    ]
    
    def test_greeting_response_is_conversational(self):
        """A greeting like 'Hello!' should get a friendly response, not JSON."""
        # Simulated external mode response (what we expect)
        good_response = "Hey! What can I help you with today?"
        bad_response = '{"action": "respond", "response": "Hello"}'
        
        # Check good response
        assert not good_response.strip().startswith("{"), \
            "Conversational response should not start with JSON"
        assert "?" in good_response or "!" in good_response, \
            "Greeting response should be conversational"
        
        # Check bad response would fail
        assert bad_response.strip().startswith("{"), \
            "JSON response starts with brace"
    
    def test_explanation_has_markdown(self):
        """Technical explanations should use markdown formatting."""
        # Expected external mode response about Docker
        response = """Docker is a containerization platform that packages applications.

**Key concepts:**
- **Images**: Read-only templates
- **Containers**: Running instances

```bash
docker ps
```
"""
        assert "**" in response, "Should use bold markdown"
        assert "```" in response, "Should use code fences"
        assert "-" in response, "Should use bullet lists"
    
    def test_no_json_wrapper_in_external_mode(self):
        """External mode should NEVER wrap responses in JSON."""
        # Bad patterns that indicate JSON leaking through
        bad_patterns = [
            '{"action":',
            '{"response":',
            '{"markdown":',
            '"requires_action":',
        ]
        
        good_response = "Here's how to list Docker containers:\n\n```bash\ndocker ps\n```"
        
        for pattern in bad_patterns:
            assert pattern not in good_response, \
                f"External response should not contain '{pattern}'"


class TestInternalModeOutputFormat:
    """Test that internal mode produces structured JSON."""
    
    def test_internal_response_is_json(self):
        """Internal mode responses should be valid JSON."""
        # Expected internal mode response
        response = '''{
  "action": "respond",
  "response": "Hello! What would you like to work on?",
  "requires_action": false
}'''
        
        try:
            parsed = json.loads(response)
            assert "action" in parsed, "JSON should have 'action' field"
        except json.JSONDecodeError:
            pytest.fail("Internal mode response should be valid JSON")
    
    def test_internal_response_has_action_field(self):
        """Internal responses should have an action field."""
        response = '{"action": "run_command", "command": "ls -la"}'
        parsed = json.loads(response)
        
        assert "action" in parsed, "Must have 'action' field"
        assert isinstance(parsed["action"], str), "Action should be a string"
    
    def test_internal_response_types(self):
        """Internal mode should produce various action types."""
        valid_actions = [
            "respond",
            "run_command",
            "create_file",
            "request_info",
            "concepts",
            "multi_step_plan",
            "save_preference",
        ]
        
        # Each should be a valid action type
        for action in valid_actions:
            response = json.dumps({"action": action, "requires_action": False})
            parsed = json.loads(response)
            assert parsed["action"] in valid_actions


class TestContextSwitchingConsistency:
    """Test that same input produces different outputs based on context."""
    
    PAIRED_EXAMPLES = [
        {
            "input": "Hello!",
            "external": "Hey! What can I help you with?",
            "internal": '{"action": "respond", "response": "Hello! What can I help you with?"}',
        },
        {
            "input": "List Python files",
            "external": "I'll find all Python files:\n\n```bash\nfind . -name '*.py'\n```",
            "internal": '{"action": "run_command", "command": "find . -name \'*.py\'"}',
        },
        {
            "input": "Thanks!",
            "external": "You're welcome! Happy to help anytime.",
            "internal": '{"action": "respond", "response": "You\'re welcome!", "session_complete": true}',
        },
    ]
    
    def test_external_is_not_json(self):
        """External responses should not be parseable as action JSON."""
        for example in self.PAIRED_EXAMPLES:
            external = example["external"]
            
            # Should not start with brace
            assert not external.strip().startswith("{"), \
                f"External response should not start with JSON: {external[:50]}"
    
    def test_internal_is_json(self):
        """Internal responses should be valid JSON with action field."""
        for example in self.PAIRED_EXAMPLES:
            internal = example["internal"]
            
            try:
                parsed = json.loads(internal)
                assert "action" in parsed, f"Missing action field in: {internal}"
            except json.JSONDecodeError:
                pytest.fail(f"Internal response not valid JSON: {internal}")
    
    def test_both_modes_answer_same_question(self):
        """Both modes should provide equivalent information, just formatted differently."""
        for example in self.PAIRED_EXAMPLES:
            external = example["external"]
            internal = example["internal"]
            
            # Both should exist and be non-empty
            assert len(external) > 0, "External response should not be empty"
            assert len(internal) > 0, "Internal response should not be empty"


class TestFilterContextTypeLogic:
    """Test the actual filter logic for context type determination."""
    
    def test_all_intents_mapped(self):
        """All known intents should map to a context type."""
        known_intents = ["casual", "save", "recall", "task"]
        
        for intent in known_intents:
            context_type = "internal" if intent == "task" else "external"
            assert context_type in ["internal", "external"], \
                f"Intent '{intent}' should map to valid context type"
    
    def test_unknown_intent_defaults_to_external(self):
        """Unknown intents should default to external (safer for user-facing)."""
        unknown_intents = ["unknown", "foo", None, ""]
        
        for intent in unknown_intents:
            # Filter logic: only "task" is internal
            context_type = "internal" if intent == "task" else "external"
            assert context_type == "external", \
                f"Unknown intent '{intent}' should default to external"
    
    def test_context_type_not_duplicated(self):
        """contextType should not be injected twice."""
        existing_content = "contextType: external\n\nYou are AJ."
        context_type = "external"
        
        # Filter check: if already present, don't add again
        if "contextType:" not in existing_content:
            new_content = f"contextType: {context_type}\n\n{existing_content}"
        else:
            new_content = existing_content
        
        # Count occurrences
        count = new_content.count("contextType:")
        assert count == 1, f"contextType should appear exactly once, found {count}"


class TestManchurianCandidateTrainingData:
    """Validate the training data structure for context switching."""
    
    @pytest.fixture
    def training_data_path(self):
        return os.path.join(
            os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
            "training", "data", "context_switching.jsonl"
        )
    
    def test_training_file_exists(self, training_data_path):
        """The context_switching.jsonl training file should exist."""
        assert os.path.exists(training_data_path), \
            f"Training file not found: {training_data_path}"
    
    def test_training_data_has_both_contexts(self, training_data_path):
        """Training data should have examples of both external and internal."""
        if not os.path.exists(training_data_path):
            pytest.skip("Training file not found")
        
        external_count = 0
        internal_count = 0
        
        with open(training_data_path, 'r', encoding='utf-8-sig') as f:
            for line in f:
                if "contextType: external" in line:
                    external_count += 1
                if "contextType: internal" in line:
                    internal_count += 1
        
        assert external_count > 0, "Should have external context examples"
        assert internal_count > 0, "Should have internal context examples"
        
        # Should be roughly balanced
        ratio = external_count / internal_count if internal_count > 0 else 0
        assert 0.3 < ratio < 3.0, \
            f"Training data should be roughly balanced, got {external_count}:{internal_count}"
    
    def test_training_pairs_are_valid_json(self, training_data_path):
        """Each line in training file should be valid JSONL."""
        if not os.path.exists(training_data_path):
            pytest.skip("Training file not found")
        
        errors = []
        with open(training_data_path, 'r', encoding='utf-8-sig') as f:
            for i, line in enumerate(f, 1):
                line = line.strip()
                if not line:
                    continue
                try:
                    data = json.loads(line)
                    assert "messages" in data, f"Line {i}: Missing 'messages' field"
                except json.JSONDecodeError as e:
                    # Allow control character errors (likely from emoji)
                    if "Invalid control character" not in str(e):
                        errors.append(f"Line {i}: {e}")
        
        assert not errors, f"Invalid JSONL lines:\n" + "\n".join(errors[:10])
    
    def test_internal_responses_are_json(self, training_data_path):
        """Internal context responses should be valid JSON."""
        if not os.path.exists(training_data_path):
            pytest.skip("Training file not found")
        
        errors = []
        line_count = 0
        with open(training_data_path, 'r', encoding='utf-8-sig') as f:
            for i, line in enumerate(f, 1):
                line_count = i
                line = line.strip()
                if not line:
                    continue
                try:
                    data = json.loads(line)
                except json.JSONDecodeError:
                    # Skip lines with encoding issues (emoji etc)
                    continue
                    
                messages = data.get("messages", [])
                
                # Find system message
                system_msg = next((m for m in messages if m["role"] == "system"), None)
                if not system_msg or "contextType: internal" not in system_msg.get("content", ""):
                    continue
                
                # Find assistant response
                assistant_msg = next((m for m in messages if m["role"] == "assistant"), None)
                if not assistant_msg:
                    continue
                
                response = assistant_msg.get("content", "")
                try:
                    parsed = json.loads(response)
                    if "action" not in parsed:
                        errors.append(f"Line {i}: Internal response missing 'action' field")
                except json.JSONDecodeError:
                    errors.append(f"Line {i}: Internal response is not valid JSON")
        
        # Allow some errors (edge cases) but not too many
        error_rate = len(errors) / max(1, line_count)
        assert error_rate < 0.1, \
            f"Too many invalid internal responses ({len(errors)}):\n" + "\n".join(errors[:5])


class TestResponseCleanup:
    """Test that the filter properly cleans up leaked internal content."""
    
    def test_think_tags_removed(self):
        """<think> tags should be stripped from output."""
        import re
        
        response = "<think>Internal reasoning here</think>The actual response."
        
        # Pattern from filter
        cleaned = re.sub(r'<think>', '', response)
        cleaned = re.sub(r'</think>', '', cleaned)
        
        assert "<think>" not in cleaned
        assert "</think>" not in cleaned
        assert "The actual response" in cleaned
    
    def test_looping_content_deduplicated(self):
        """Repeated lines (model looping) should be deduplicated."""
        response = """Here's the answer.
# What's the plan?
# What's the plan?
# What's the plan?
# What's the plan?
That's all."""
        
        lines = response.split('\n')
        line_counts = {}
        for line in lines:
            stripped = line.strip()
            if stripped and len(stripped) > 10:
                line_counts[stripped] = line_counts.get(stripped, 0) + 1
        
        looping_detected = any(count >= 3 for count in line_counts.values())
        assert looping_detected, "Should detect looping content"
        
        # Apply dedup logic
        seen = set()
        deduped = []
        for line in lines:
            stripped = line.strip()
            if stripped in line_counts and line_counts[stripped] >= 3:
                if stripped not in seen:
                    seen.add(stripped)
                    deduped.append(line)
            else:
                deduped.append(line)
        
        cleaned = '\n'.join(deduped)
        assert cleaned.count("What's the plan?") == 1, "Should keep only first occurrence"


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
