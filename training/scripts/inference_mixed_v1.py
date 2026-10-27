#!/usr/bin/env python3
"""
Quick inference script for AJ Mixed-V1 LoRA model
Supports loading the trained LoRA adapter with the base model
"""

import torch
from transformers import AutoModelForCausalLM, AutoTokenizer
from peft import PeftModel
import os
from pathlib import Path

# Configuration
BASE_MODEL = "deepseek-ai/DeepSeek-R1-Distill-Qwen-32B"
ADAPTER_PATH = (
    "./models/AJ-DeepSeekR1Qwen32B-v2.1.0-lora"  # Local path (update if moved)
)
DEVICE = "cuda" if torch.cuda.is_available() else "cpu"


class AJModel:
    def __init__(self, adapter_path=ADAPTER_PATH, base_model=BASE_MODEL, device=DEVICE):
        """Initialize the model with LoRA adapter"""
        self.device = device
        self.adapter_path = adapter_path
        self.base_model_name = base_model

        print(f"Loading base model: {base_model}")
        print(f"Loading adapter: {adapter_path}")

        # Load base model
        self.base_model = AutoModelForCausalLM.from_pretrained(
            base_model,
            dtype=torch.bfloat16,
            device_map="auto",
            trust_remote_code=True,
            low_cpu_mem_usage=True,
        )

        # Load tokenizer
        self.tokenizer = AutoTokenizer.from_pretrained(
            base_model, trust_remote_code=True
        )

        # Load LoRA adapter
        self.model = PeftModel.from_pretrained(
            self.base_model, adapter_path, trust_remote_code=True
        )

        # Set to inference mode
        self.model.eval()
        print("✓ Model loaded successfully!")
        print(f"  Device: {device}")
        print(
            f"  Model size: {sum(p.numel() for p in self.model.parameters()) / 1e9:.1f}B parameters"
        )

    def generate(self, prompt, max_new_tokens=256, temperature=0.4, top_p=0.9):
        """Generate text from prompt"""
        with torch.no_grad():
            inputs = self.tokenizer(prompt, return_tensors="pt").to(self.device)

            outputs = self.model.generate(
                **inputs,
                max_new_tokens=max_new_tokens,
                temperature=temperature,
                top_p=top_p,
                do_sample=True,
                pad_token_id=self.tokenizer.eos_token_id,
            )

        generated_text = self.tokenizer.decode(outputs[0], skip_special_tokens=True)
        return generated_text

    def chat(self, messages, max_new_tokens=512, temperature=0.7):
        """Chat interface using the model's chat template"""
        # Format messages for the model
        formatted_prompt = self.tokenizer.apply_chat_template(
            messages, tokenize=False, add_generation_prompt=True
        )

        return self.generate(formatted_prompt, max_new_tokens, temperature)


def main():
    # Initialize model
    model = AJModel()

    # Test examples
    test_prompts = [
        "Explain quantum computing in simple terms.",
        "Write a Python function to sort a list using merge sort.",
        "What are the benefits of container orchestration?",
    ]

    print("\n" + "=" * 60)
    print("Testing Mixed-V1 LoRA Model")
    print("=" * 60 + "\n")

    for i, prompt in enumerate(test_prompts, 1):
        print(f"Test {i}: {prompt}")
        print("-" * 60)
        response = model.generate(prompt, max_new_tokens=200)
        print(response)
        print("\n")

    # Interactive chat
    print("=" * 60)
    print("Interactive Chat (type 'quit' to exit)")
    print("=" * 60 + "\n")

    while True:
        user_input = input("You: ").strip()
        if user_input.lower() in ["quit", "exit", "q"]:
            break

        if not user_input:
            continue

        messages = [{"role": "user", "content": user_input}]
        response = model.chat(messages)

        # Extract just the assistant's response
        if "Assistant:" in response:
            response = response.split("Assistant:")[-1].strip()

        print(f"Assistant: {response}\n")


if __name__ == "__main__":
    main()
