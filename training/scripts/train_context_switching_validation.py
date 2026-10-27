#!/usr/bin/env python3
"""
Context Switching Validation Training

Train a small model (Qwen2.5-1.5B) to validate the "Manchurian Candidate" approach:
- contextType: external → conversational output
- contextType: internal → JSON output

This is a quick validation before committing to expensive H200 training.
Can run on consumer GPU (8GB+ VRAM) or even CPU with patience.

Usage:
    python train_context_switching_validation.py
    python train_context_switching_validation.py --epochs 3 --batch-size 2
"""

import argparse
import json
import os
import torch
from pathlib import Path
from datetime import datetime

# Check for required packages
try:
    from datasets import Dataset
    from transformers import AutoModelForCausalLM, AutoTokenizer
    from peft import LoraConfig, get_peft_model, TaskType
    from trl import SFTTrainer, SFTConfig
except ImportError as e:
    print(f"Missing dependency: {e}")
    print(
        "Install with: pip install transformers datasets peft trl accelerate bitsandbytes"
    )
    exit(1)


def parse_args():
    parser = argparse.ArgumentParser(
        description="Validation training for context switching"
    )
    parser.add_argument(
        "--model",
        type=str,
        default="Qwen/Qwen2.5-1.5B-Instruct",
        help="Base model to fine-tune",
    )
    parser.add_argument(
        "--data",
        type=str,
        default="../data/context_switching.jsonl",
        help="Path to context_switching.jsonl",
    )
    parser.add_argument(
        "--output",
        type=str,
        default="../models/context-switching-validation",
        help="Output directory for the adapter",
    )
    parser.add_argument(
        "--epochs", type=int, default=2, help="Number of training epochs"
    )
    parser.add_argument(
        "--batch-size", type=int, default=4, help="Batch size (reduce if OOM)"
    )
    parser.add_argument(
        "--learning-rate", type=float, default=2e-4, help="Learning rate"
    )
    parser.add_argument(
        "--max-samples",
        type=int,
        default=None,
        help="Max samples to use (for quick testing)",
    )
    parser.add_argument(
        "--lora-r",
        type=int,
        default=16,
        help="LoRA rank (smaller = faster, less capacity)",
    )
    parser.add_argument(
        "--quantize", action="store_true", help="Use 4-bit quantization (saves VRAM)"
    )
    return parser.parse_args()


def load_training_data(data_path: str, max_samples: int = None) -> list:
    """Load and format training data."""
    data = []
    with open(data_path, "r", encoding="utf-8") as f:
        for line in f:
            try:
                example = json.loads(line)
                if "messages" in example:
                    data.append(example)
            except json.JSONDecodeError:
                continue

    if max_samples:
        data = data[:max_samples]

    print(f"Loaded {len(data)} examples")

    # Check balance
    external = sum(1 for d in data if "external" in d["messages"][0].get("content", ""))
    internal = sum(1 for d in data if "internal" in d["messages"][0].get("content", ""))
    print(f"  contextType: external = {external}")
    print(f"  contextType: internal = {internal}")

    return data


def format_for_training(example, tokenizer):
    """Convert messages to chat format string."""
    messages = example["messages"]

    # Use the tokenizer's chat template if available
    try:
        text = tokenizer.apply_chat_template(
            messages, tokenize=False, add_generation_prompt=False
        )
    except Exception:
        # Fallback: manual ChatML format
        parts = []
        for msg in messages:
            role = msg["role"]
            content = msg["content"]
            parts.append(f"<|im_start|>{role}\n{content}<|im_end|>")
        text = "\n".join(parts)

    return {"text": text}


def main():
    args = parse_args()

    # Resolve paths
    script_dir = Path(__file__).parent
    data_path = (script_dir / args.data).resolve()
    output_dir = (script_dir / args.output).resolve()

    print("=" * 60)
    print("Context Switching Validation Training")
    print("=" * 60)
    print(f"Model: {args.model}")
    print(f"Data: {data_path}")
    print(f"Output: {output_dir}")
    print(f"Epochs: {args.epochs}")
    print(f"Batch size: {args.batch_size}")
    print(f"LoRA rank: {args.lora_r}")
    print(f"Quantize: {args.quantize}")
    print("=" * 60)

    # Check GPU
    if torch.cuda.is_available():
        gpu_name = torch.cuda.get_device_name(0)
        gpu_mem = torch.cuda.get_device_properties(0).total_memory / 1e9
        print(f"GPU: {gpu_name} ({gpu_mem:.1f} GB)")
    else:
        print("WARNING: No GPU detected, training will be slow!")

    # Load data
    print("\nLoading training data...")
    raw_data = load_training_data(str(data_path), args.max_samples)

    # Load tokenizer
    print(f"\nLoading tokenizer from {args.model}...")
    tokenizer = AutoTokenizer.from_pretrained(args.model, trust_remote_code=True)
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token

    # Format data
    print("Formatting data for training...")
    formatted_data = [format_for_training(ex, tokenizer) for ex in raw_data]
    dataset = Dataset.from_list(formatted_data)

    # Split train/eval (95/5)
    split = dataset.train_test_split(test_size=0.05, seed=42)
    train_dataset = split["train"]
    eval_dataset = split["test"]
    print(f"Train: {len(train_dataset)}, Eval: {len(eval_dataset)}")

    # Load model
    print(f"\nLoading model {args.model}...")

    model_kwargs = {
        "trust_remote_code": True,
        "torch_dtype": torch.bfloat16 if torch.cuda.is_available() else torch.float32,
        "device_map": "auto",
    }

    if args.quantize:
        try:
            from transformers import BitsAndBytesConfig

            model_kwargs["quantization_config"] = BitsAndBytesConfig(
                load_in_4bit=True,
                bnb_4bit_compute_dtype=torch.bfloat16,
                bnb_4bit_use_double_quant=True,
                bnb_4bit_quant_type="nf4",
            )
            print("Using 4-bit quantization")
        except ImportError:
            print("bitsandbytes not available, skipping quantization")

    model = AutoModelForCausalLM.from_pretrained(args.model, **model_kwargs)

    # Configure LoRA
    lora_config = LoraConfig(
        r=args.lora_r,
        lora_alpha=args.lora_r * 2,  # Common practice: alpha = 2 * r
        target_modules=[
            "q_proj",
            "k_proj",
            "v_proj",
            "o_proj",
            "gate_proj",
            "up_proj",
            "down_proj",
        ],
        lora_dropout=0.05,
        bias="none",
        task_type=TaskType.CAUSAL_LM,
    )

    model = get_peft_model(model, lora_config)

    trainable_params = sum(p.numel() for p in model.parameters() if p.requires_grad)
    total_params = sum(p.numel() for p in model.parameters())
    print(
        f"Trainable parameters: {trainable_params:,} ({100*trainable_params/total_params:.2f}%)"
    )

    # SFT Config (trl 0.24+ uses SFTConfig instead of TrainingArguments)
    sft_config = SFTConfig(
        output_dir=str(output_dir),
        num_train_epochs=args.epochs,
        per_device_train_batch_size=args.batch_size,
        per_device_eval_batch_size=args.batch_size,
        gradient_accumulation_steps=4,
        learning_rate=args.learning_rate,
        weight_decay=0.01,
        warmup_steps=50,
        logging_steps=10,
        eval_strategy="steps",
        eval_steps=100,
        save_strategy="steps",
        save_steps=200,
        save_total_limit=2,
        bf16=torch.cuda.is_available(),
        report_to="none",
        gradient_checkpointing=True,
        optim="adamw_torch",
        max_grad_norm=1.0,
        max_length=1024,
        dataset_text_field="text",
        packing=False,
    )

    # Create trainer
    trainer = SFTTrainer(
        model=model,
        args=sft_config,
        train_dataset=train_dataset,
        eval_dataset=eval_dataset,
        processing_class=tokenizer,
    )

    # Train!
    print("\n" + "=" * 60)
    print("Starting training...")
    print("=" * 60)

    trainer.train()

    # Save the adapter
    print(f"\nSaving adapter to {output_dir}...")
    trainer.save_model(str(output_dir))
    tokenizer.save_pretrained(str(output_dir))

    # Save training info
    info = {
        "base_model": args.model,
        "training_date": datetime.now().isoformat(),
        "epochs": args.epochs,
        "batch_size": args.batch_size,
        "lora_r": args.lora_r,
        "total_examples": len(raw_data),
        "train_examples": len(train_dataset),
        "eval_examples": len(eval_dataset),
    }
    with open(output_dir / "training_info.json", "w") as f:
        json.dump(info, f, indent=2)

    print("\n" + "=" * 60)
    print("Training complete!")
    print(f"Adapter saved to: {output_dir}")
    print("=" * 60)
    print("\nTo test the model, run:")
    print(f"  python test_context_switching.py --adapter {output_dir}")


if __name__ == "__main__":
    main()
