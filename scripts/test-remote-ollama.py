#!/usr/bin/env python3
"""
Test script to validate LLM tool-calling capabilities on a remote Ollama instance.
Run this on a rented GPU to compare model performance before committing to training.

Usage:
    # On rented GPU:
    pip install requests
    OLLAMA_HOST=http://localhost:11434 python test-remote-ollama.py

    # Or from local machine pointing to remote:
    OLLAMA_HOST=http://<remote-ip>:11434 python test-remote-ollama.py

Models to test (in order of recommendation):
    - qwen2.5:72b      (Best structured output, 72B)
    - llama3.1:70b     (Good general purpose, 70B)
    - command-r:104b   (Built for agents, 104B - needs more VRAM)
"""

import json
import os
import sys
import requests
from typing import Any

OLLAMA_HOST = os.environ.get("OLLAMA_HOST", "http://localhost:11434")
MODEL = os.environ.get("OLLAMA_MODEL", "qwen2.5:72b")

# The exact system prompt from AJ orchestrator
SYSTEM_PROMPT = """You are AJ, an AI assistant with access to real infrastructure through remote agents.

## Available Tools

You have access to these tools for interacting with remote systems:

### list_agents
Discovers available agents in the network. ALWAYS call this first.
Returns: List of agents with their agent_id, hostname, capabilities.

### execute
Executes a command on a specific agent.
Parameters:
- agent_id: The ID of the agent (from list_agents)
- command: The shell command to execute

## Response Format

You MUST respond with valid JSON in this exact format:
{
    "reasoning": "Brief explanation of your approach",
    "tool": "tool_name",
    "parameters": { ... }
}

## Critical Rules

1. ALWAYS call list_agents FIRST before any execute command
2. Use the exact agent_id returned by list_agents
3. Never guess file paths - use standard system paths
4. Output ONLY valid JSON, no markdown, no explanation outside JSON
"""

# Test cases - the exact prompts that failed on 32B
TEST_CASES = [
    {
        "name": "Basic Tool Discovery",
        "prompt": "What agents are available?",
        "expected_tool": "list_agents",
        "must_not_contain": ["execute", "action"],
    },
    {
        "name": "Spam Blocking (The Original Failure)",
        "prompt": "Block the domain jojoscarinsurance.com on postfix01",
        "expected_tool": "list_agents",  # Should discover agents FIRST
        "must_not_contain": ["execute", "action", "spam_domains"],
        "note": "Model should call list_agents first, not jump to execute",
    },
    {
        "name": "Multi-step Task",
        "prompt": "Check disk space on all linux servers",
        "expected_tool": "list_agents",
        "must_not_contain": ["action"],
    },
    {
        "name": "Format Compliance",
        "prompt": "Restart the nginx service on the web server",
        "expected_tool": "list_agents",
        "must_contain": ["tool", "reasoning"],
        "must_not_contain": ["action", "```"],
    },
]


def test_model(model: str) -> dict[str, Any]:
    """Run all test cases against the specified model."""
    print(f"\n{'='*60}")
    print(f"Testing model: {model}")
    print(f"Ollama host: {OLLAMA_HOST}")
    print(f"{'='*60}\n")

    results = {"model": model, "passed": 0, "failed": 0, "tests": []}

    for i, test in enumerate(TEST_CASES, 1):
        print(f"\n[Test {i}/{len(TEST_CASES)}] {test['name']}")
        print(f"  Prompt: {test['prompt'][:60]}...")

        try:
            response = requests.post(
                f"{OLLAMA_HOST}/api/generate",
                json={
                    "model": model,
                    "prompt": test["prompt"],
                    "system": SYSTEM_PROMPT,
                    "stream": False,
                    "options": {
                        "temperature": 0.1,
                        "num_predict": 500,
                    },
                },
                timeout=120,
            )
            response.raise_for_status()

            output = response.json().get("response", "")
            print(f"  Response: {output[:200]}...")

            # Validate response
            issues = []

            # Check if response is valid JSON
            try:
                parsed = json.loads(output.strip())

                # Check for expected tool
                if test.get("expected_tool"):
                    actual_tool = parsed.get("tool", parsed.get("action", "MISSING"))
                    if actual_tool != test["expected_tool"]:
                        issues.append(
                            f"Expected tool '{test['expected_tool']}', got '{actual_tool}'"
                        )

                # Check must_contain
                for must_have in test.get("must_contain", []):
                    if must_have not in parsed:
                        issues.append(f"Missing required field: '{must_have}'")

            except json.JSONDecodeError as e:
                issues.append(f"Invalid JSON: {e}")

            # Check must_not_contain (in raw output)
            for must_not in test.get("must_not_contain", []):
                if must_not.lower() in output.lower():
                    issues.append(f"Contains forbidden term: '{must_not}'")

            # Record result
            passed = len(issues) == 0
            result = {
                "name": test["name"],
                "passed": passed,
                "issues": issues,
                "output": output[:500],
            }
            results["tests"].append(result)

            if passed:
                results["passed"] += 1
                print(f"  ✅ PASSED")
            else:
                results["failed"] += 1
                print(f"  ❌ FAILED: {', '.join(issues)}")

        except requests.exceptions.RequestException as e:
            results["failed"] += 1
            results["tests"].append(
                {
                    "name": test["name"],
                    "passed": False,
                    "issues": [f"Request failed: {e}"],
                }
            )
            print(f"  ❌ REQUEST FAILED: {e}")

    return results


def main():
    """Run tests and print summary."""
    print("=" * 60)
    print("AJ Orchestrator - Remote Ollama Tool-Calling Test")
    print("=" * 60)

    # Check if Ollama is reachable
    try:
        r = requests.get(f"{OLLAMA_HOST}/api/tags", timeout=10)
        r.raise_for_status()
        available_models = [m["name"] for m in r.json().get("models", [])]
        print(
            f"\nAvailable models: {', '.join(available_models) or 'None (pull one first)'}"
        )
    except requests.exceptions.RequestException as e:
        print(f"\n❌ Cannot reach Ollama at {OLLAMA_HOST}: {e}")
        print("\nMake sure Ollama is running with: OLLAMA_HOST=0.0.0.0 ollama serve")
        sys.exit(1)

    # Check if target model is available
    if MODEL not in available_models and f"{MODEL}:latest" not in available_models:
        print(f"\n⚠️  Model {MODEL} not found. Pull it first:")
        print(f"   ollama pull {MODEL}")

        # Offer to test with an available model
        if available_models:
            alt = available_models[0]
            print(f"\n   Or set OLLAMA_MODEL={alt} to test with available model")
        sys.exit(1)

    # Run tests
    results = test_model(MODEL)

    # Print summary
    print("\n" + "=" * 60)
    print("SUMMARY")
    print("=" * 60)
    print(f"Model: {results['model']}")
    print(f"Passed: {results['passed']}/{len(TEST_CASES)}")
    print(f"Failed: {results['failed']}/{len(TEST_CASES)}")

    if results["failed"] == 0:
        print("\n✅ ALL TESTS PASSED - This model handles tool-calling correctly!")
        print("   You can likely skip fine-tuning and use this model directly.")
    elif results["passed"] >= len(TEST_CASES) // 2:
        print("\n⚠️  PARTIAL SUCCESS - Model needs some tuning")
        print("   Consider targeted fine-tuning on tool-calling format only.")
    else:
        print("\n❌ SIGNIFICANT FAILURES - Model struggles with tool-calling")
        print("   Fine-tuning recommended, or try a larger model.")

    # Save results to file
    output_file = f"test_results_{MODEL.replace(':', '_').replace('/', '_')}.json"
    with open(output_file, "w") as f:
        json.dump(results, f, indent=2)
    print(f"\nDetailed results saved to: {output_file}")

    return 0 if results["failed"] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
