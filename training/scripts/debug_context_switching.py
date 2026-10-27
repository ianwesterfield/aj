#!/usr/bin/env python3
"""
Test Context Switching Model

Validates that the fine-tuned model correctly switches output modes
based on contextType in the system prompt.

Usage:
    python test_context_switching.py --adapter ../models/context-switching-validation
"""

import argparse
import torch
from pathlib import Path

try:
    from transformers import AutoModelForCausalLM, AutoTokenizer
    from peft import PeftModel
except ImportError as e:
    print(f"Missing dependency: {e}")
    print("Install with: pip install transformers peft")
    exit(1)


def parse_args():
    parser = argparse.ArgumentParser(description="Test context switching model")
    parser.add_argument(
        "--adapter", type=str, required=True, help="Path to the LoRA adapter"
    )
    parser.add_argument(
        "--base-model",
        type=str,
        default=None,
        help="Base model (auto-detected from adapter if not specified)",
    )
    parser.add_argument(
        "--quantize", action="store_true", help="Use 4-bit quantization"
    )
    return parser.parse_args()


def load_model(adapter_path: str, base_model: str = None, quantize: bool = False):
    """Load the base model with LoRA adapter."""
    adapter_path = Path(adapter_path)

    # Auto-detect base model from training info
    if base_model is None:
        info_path = adapter_path / "training_info.json"
        if info_path.exists():
            import json

            with open(info_path) as f:
                info = json.load(f)
                base_model = info.get("base_model", "Qwen/Qwen2.5-1.5B-Instruct")
        else:
            base_model = "Qwen/Qwen2.5-1.5B-Instruct"

    print(f"Loading base model: {base_model}")
    print(f"Loading adapter: {adapter_path}")

    model_kwargs = {
        "trust_remote_code": True,
        "torch_dtype": torch.bfloat16 if torch.cuda.is_available() else torch.float32,
        "device_map": "auto",
    }

    if quantize:
        try:
            from transformers import BitsAndBytesConfig

            model_kwargs["quantization_config"] = BitsAndBytesConfig(
                load_in_4bit=True,
                bnb_4bit_compute_dtype=torch.bfloat16,
            )
        except ImportError:
            pass

    # Load base model
    model = AutoModelForCausalLM.from_pretrained(base_model, **model_kwargs)

    # Load adapter
    model = PeftModel.from_pretrained(model, str(adapter_path))

    # Load tokenizer
    tokenizer = AutoTokenizer.from_pretrained(str(adapter_path))
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token

    return model, tokenizer


def generate_response(
    model, tokenizer, messages: list, max_new_tokens: int = 512
) -> str:
    """Generate a response given messages."""
    # Format using chat template
    try:
        prompt = tokenizer.apply_chat_template(
            messages, tokenize=False, add_generation_prompt=True
        )
    except Exception:
        # Fallback
        parts = []
        for msg in messages:
            parts.append(f"<|im_start|>{msg['role']}\n{msg['content']}<|im_end|>")
        parts.append("<|im_start|>assistant\n")
        prompt = "\n".join(parts)

    inputs = tokenizer(prompt, return_tensors="pt").to(model.device)

    with torch.no_grad():
        outputs = model.generate(
            **inputs,
            max_new_tokens=max_new_tokens,
            do_sample=True,
            temperature=0.7,
            top_p=0.9,
            pad_token_id=tokenizer.pad_token_id,
        )

    response = tokenizer.decode(
        outputs[0][inputs["input_ids"].shape[1] :], skip_special_tokens=True
    )
    return response.strip()


def run_test_suite(model, tokenizer):
    """Run a suite of tests comparing external vs internal responses."""

    test_cases = [
        # Greetings
        "Good morning!",
        "Hey, how are you?",
        # Questions
        "What is Docker?",
        "How do I create a Python virtual environment?",
        # Tasks
        "List all Python files in the current directory",
        "Run the test suite",
        # Troubleshooting
        "I'm getting a 'module not found' error",
        "My Docker container won't start",
    ]

    print("\n" + "=" * 80)
    print("CONTEXT SWITCHING VALIDATION TEST")
    print("=" * 80)

    results = {"pass": 0, "fail": 0}

    for user_input in test_cases:
        print(f"\n{'─' * 80}")
        print(f"USER: {user_input}")
        print("─" * 80)

        # Test EXTERNAL (conversational)
        external_messages = [
            {
                "role": "system",
                "content": "You are AJ, an AI assistant. contextType: external",
            },
            {"role": "user", "content": user_input},
        ]
        external_response = generate_response(model, tokenizer, external_messages)

        # Test INTERNAL (JSON)
        internal_messages = [
            {
                "role": "system",
                "content": "You are AJ, an AI assistant. contextType: internal",
            },
            {"role": "user", "content": user_input},
        ]
        internal_response = generate_response(model, tokenizer, internal_messages)

        # Display results
        print(f"\n📤 EXTERNAL (conversational):")
        print(
            f"   {external_response[:200]}{'...' if len(external_response) > 200 else ''}"
        )

        print(f"\n📥 INTERNAL (JSON):")
        print(
            f"   {internal_response[:200]}{'...' if len(internal_response) > 200 else ''}"
        )

        # Check if responses are appropriately different
        external_looks_conversational = not external_response.strip().startswith("{")
        internal_looks_json = internal_response.strip().startswith("{")

        if external_looks_conversational and internal_looks_json:
            print(f"\n✅ PASS: Correctly differentiated output modes")
            results["pass"] += 1
        else:
            print(f"\n❌ FAIL: Output modes not differentiated")
            if not external_looks_conversational:
                print(
                    f"   - External response looks like JSON (should be conversational)"
                )
            if not internal_looks_json:
                print(f"   - Internal response doesn't look like JSON")
            results["fail"] += 1

    # Summary
    print("\n" + "=" * 80)
    print("TEST SUMMARY")
    print("=" * 80)
    total = results["pass"] + results["fail"]
    print(f"Passed: {results['pass']}/{total} ({100*results['pass']/total:.0f}%)")
    print(f"Failed: {results['fail']}/{total}")

    if results["pass"] / total >= 0.8:
        print("\n🎉 SUCCESS: Context switching is working!")
        print("   The model correctly switches between conversational and JSON output")
        print("   based on the contextType signal in the system prompt.")
    elif results["pass"] / total >= 0.5:
        print("\n⚠️  PARTIAL: Context switching shows some effect")
        print("   May need more training data or epochs.")
    else:
        print("\n❌ FAILURE: Context switching not working")
        print("   The model is not responding to the contextType signal.")
        print(
            "   Consider: more training data, more epochs, or different learning rate."
        )

    return results


def interactive_mode(model, tokenizer):
    """Interactive testing mode."""
    print("\n" + "=" * 80)
    print("INTERACTIVE MODE")
    print("Commands: 'external', 'internal', 'quit'")
    print("=" * 80)

    context_type = "external"

    while True:
        user_input = input(f"\n[{context_type}] You: ").strip()

        if not user_input:
            continue
        if user_input.lower() == "quit":
            break
        if user_input.lower() == "external":
            context_type = "external"
            print(f"Switched to contextType: {context_type}")
            continue
        if user_input.lower() == "internal":
            context_type = "internal"
            print(f"Switched to contextType: {context_type}")
            continue

        messages = [
            {
                "role": "system",
                "content": f"You are AJ, an AI assistant. contextType: {context_type}",
            },
            {"role": "user", "content": user_input},
        ]

        response = generate_response(model, tokenizer, messages)
        print(f"\nAJ: {response}")


def main():
    args = parse_args()

    # Load model
    model, tokenizer = load_model(args.adapter, args.base_model, args.quantize)

    # Run test suite
    results = run_test_suite(model, tokenizer)

    # Offer interactive mode
    try:
        choice = input("\nEnter interactive mode? [y/N]: ").strip().lower()
        if choice == "y":
            interactive_mode(model, tokenizer)
    except KeyboardInterrupt:
        pass

    print("\nDone!")


if __name__ == "__main__":
    main()
