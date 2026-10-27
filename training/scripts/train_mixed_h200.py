#!/usr/bin/env python3
"""
LoRA Training for DeepSeek-R1-Distill-Qwen-32B
Mixed Dataset v1 (Conversational + Plant/Farming + Apothecary + Strategic)

AJ-DeepSeekR1Qwen32B-v2.1.0-lora

Supports both Single-GPU and Multi-GPU (DDP) training.
Optimized for NVIDIA H200 (141GB VRAM).

=== MULTI-GPU (DDP) REQUIREMENTS ===
When using `accelerate launch` for multi-GPU training, these settings are CRITICAL:

1. device_map=None (NOT 'auto')
   - Accelerate handles device placement, not AutoModelForCausalLM
   - 'auto' causes: "ValueError: You can't train a model that has been loaded with device_map='auto'"

2. use_reentrant=False in gradient_checkpointing_kwargs
   - Prevents: "RuntimeError: Expected to have finished reduction in the prior iteration"
   - Required when combining gradient checkpointing + DDP

3. ddp_find_unused_parameters=False in SFTConfig
   - LoRA only trains ~1.6% of params; base model params are frozen but still in compute graph
   - Setting True causes unnecessary overhead and potential gradient sync issues

Launch DDP training:
    accelerate launch --config_file accelerate_config.yaml scripts/train_mixed_h200.py --config configs/mixed_v1_2xh200.yaml
"""

import os
import yaml
import json
import argparse
import torch
from datasets import Dataset, DatasetDict
from transformers import (
    AutoModelForCausalLM,
    AutoTokenizer,
)
from peft import LoraConfig, get_peft_model, TaskType
from trl.trainer.sft_trainer import SFTTrainer
from trl.trainer.sft_config import SFTConfig
from pathlib import Path


def parse_args():
    parser = argparse.ArgumentParser(description="Train DeepSeek-R1 on Mixed Dataset v1 (Single GPU)")
    parser.add_argument("--config", type=str, default="configs/mixed_v1_h200.yaml",
                        help="Path to training config")
    parser.add_argument("--resume", type=str, default=None,
                        help="Resume from checkpoint path")
    return parser.parse_args()


def load_jsonl(path: str) -> list:
    """Load JSONL file."""
    data = []
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            try:
                data.append(json.loads(line))
            except json.JSONDecodeError:
                continue
    return data


def convert_to_chatml(examples):
    """Convert messages to ChatML format"""
    texts = []
    messages_list = examples['messages']
    
    for item in messages_list:
        if isinstance(item, str):
            texts.append(item)
            continue
            
        if isinstance(item, list):
            text_parts = []
            for msg in item:
                if isinstance(msg, dict):
                    role = msg.get('role', 'user')
                    content = msg.get('content', '')
                    if role == 'user':
                        text_parts.append(f"<|im_start|>user\n{content}<|im_end|>")
                    elif role == 'assistant':
                        text_parts.append(f"<|im_start|>assistant\n{content}<|im_end|>")
                    elif role == 'system':
                        text_parts.append(f"<|im_start|>system\n{content}<|im_end|>")
            texts.append("\n".join(text_parts))
        else:
            texts.append(str(item))
    
    return {"text": texts}


def main():
    args = parse_args()
    
    # Load config
    with open(args.config, 'r') as f:
        config = yaml.safe_load(f)
    
    version = config.get('version', 'v2.1.0-lora')
    dataset_version = config.get('dataset_version', 'mixed_v1')
    
    print(f"\n{'='*60}")
    print(f"Single-GPU LoRA Training (H200)")
    print(f"AJ-DeepSeekR1Qwen32B-{version}")
    print(f"Dataset: {dataset_version}")
    print(f"{'='*60}\n")
    
    # Check GPU
    if not torch.cuda.is_available():
        raise RuntimeError("CUDA not available! This script requires GPU.")
    
    gpu_name = torch.cuda.get_device_name(0)
    gpu_mem = torch.cuda.get_device_properties(0).total_memory / 1e9
    print(f"GPU: {gpu_name}")
    print(f"VRAM: {gpu_mem:.1f} GB")
    
    model_name = config.get('model_name', 'deepseek-ai/DeepSeek-R1-Distill-Qwen-32B')
    output_dir = config.get('output_dir', './AJ-DeepSeekR1Qwen32B-v2.1.0-lora')
    
    # Load tokenizer
    print(f"\nLoading tokenizer from {model_name}...")
    tokenizer = AutoTokenizer.from_pretrained(
        model_name,
        trust_remote_code=True,
        padding_side="right"
    )
    
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token
    
    # Load model - H200 has enough VRAM for full bf16 model
    print(f"\nLoading model in bf16...")
    print(f"H200's 141GB VRAM can hold the full 32B model + LoRA")
    
    # Try flash_attention_2 first, fall back to sdpa if not available
    try:
        model = AutoModelForCausalLM.from_pretrained(
            model_name,
            torch_dtype=torch.bfloat16,
            trust_remote_code=True,
            device_map=None,  # Must be None for DDP multi-GPU
            low_cpu_mem_usage=True,
            use_cache=False,
            attn_implementation="flash_attention_2",
        )
        print("Using Flash Attention 2")
    except ImportError:
        print("Flash Attention 2 not available, using SDPA (PyTorch native)")
        model = AutoModelForCausalLM.from_pretrained(
            model_name,
            torch_dtype=torch.bfloat16,
            trust_remote_code=True,
            device_map=None,  # Must be None for DDP multi-GPU
            low_cpu_mem_usage=True,
            use_cache=False,
            attn_implementation="sdpa",
        )
    
    total_params = sum(p.numel() for p in model.parameters())
    print(f"Model loaded! Total parameters: {total_params:,}")
    
    # Configure LoRA
    lora_config = LoraConfig(
        r=int(config.get('lora_r', 64)),
        lora_alpha=int(config.get('lora_alpha', 128)),
        target_modules=config.get('target_modules', [
            "q_proj", "k_proj", "v_proj", "o_proj",
            "gate_proj", "up_proj", "down_proj"
        ]),
        lora_dropout=float(config.get('lora_dropout', 0.05)),
        bias="none",
        task_type=TaskType.CAUSAL_LM,
    )
    
    print(f"\nApplying LoRA with r={lora_config.r}, alpha={lora_config.lora_alpha}")
    model = get_peft_model(model, lora_config)
    
    trainable_params = sum(p.numel() for p in model.parameters() if p.requires_grad)
    all_params = sum(p.numel() for p in model.parameters())
    print(f"Trainable parameters: {trainable_params:,} ({100*trainable_params/all_params:.2f}%)")
    
    # Enable gradient checkpointing (use_reentrant=False required for DDP)
    model.gradient_checkpointing_enable(gradient_checkpointing_kwargs={"use_reentrant": False})
    model.enable_input_require_grads()
    
    # Load local dataset
    train_file = config.get('train_file', 'datasets/processed/mixed_v1.train.jsonl')
    eval_file = config.get('eval_file', 'datasets/processed/mixed_v1.eval.jsonl')
    
    print(f"\nLoading Mixed Dataset v1...")
    print(f"  Train: {train_file}")
    print(f"  Eval:  {eval_file}")
    
    # Load from JSONL files
    train_data = load_jsonl(train_file)
    eval_data = load_jsonl(eval_file)
    
    train_dataset = Dataset.from_list(train_data)
    eval_dataset = Dataset.from_list(eval_data)
    
    print(f"Loaded - Train: {len(train_dataset):,}, Eval: {len(eval_dataset):,}")
    
    # Convert to ChatML format
    print(f"\nConverting to ChatML format...")
    train_dataset = train_dataset.map(
        convert_to_chatml,
        batched=True,
        remove_columns=train_dataset.column_names,
        desc="Converting train to ChatML"
    )
    
    eval_dataset = eval_dataset.map(
        convert_to_chatml,
        batched=True,
        remove_columns=eval_dataset.column_names,
        desc="Converting eval to ChatML"
    )
    
    # Filter out empty examples
    train_dataset = train_dataset.filter(lambda x: len(x['text']) > 0)
    eval_dataset = eval_dataset.filter(lambda x: len(x['text']) > 0)
    
    print(f"After filtering - Train: {len(train_dataset):,}, Eval: {len(eval_dataset):,}")
    
    # Training configuration
    training_config = SFTConfig(
        output_dir=output_dir,
        
        # Batch sizes
        per_device_train_batch_size=int(config.get('batch_size', 2)),
        per_device_eval_batch_size=1,
        gradient_accumulation_steps=int(config.get('gradient_accumulation_steps', 8)),
        
        # Training duration
        num_train_epochs=int(config.get('num_epochs', 1)),
        max_steps=int(config.get('max_steps', 5000)),
        
        # Optimizer
        learning_rate=float(config.get('learning_rate', 2e-5)),
        weight_decay=float(config.get('weight_decay', 0.01)),
        warmup_steps=int(config.get('warmup_steps', 100)),
        lr_scheduler_type="cosine",
        
        # Precision
        bf16=True,
        tf32=True,
        
        # Memory optimization
        gradient_checkpointing=True,
        gradient_checkpointing_kwargs={"use_reentrant": False},
        
        # DDP settings (for multi-GPU)
        ddp_find_unused_parameters=False,
        
        # Logging
        logging_steps=int(config.get('logging_steps', 10)),
        logging_first_step=True,
        report_to="none",
        
        # Checkpointing
        save_strategy="steps",
        save_steps=int(config.get('save_steps', 500)),
        save_total_limit=3,
        
        # Evaluation
        eval_strategy="steps",
        eval_steps=int(config.get('eval_steps', 500)),
        
        # Performance
        dataloader_pin_memory=True,
        dataloader_num_workers=4,
        
        # SFT specific
        max_length=int(config.get('max_seq_length', 2048)),
        packing=False,
        dataset_text_field="text",
    )
    
    # Create trainer
    trainer = SFTTrainer(
        model=model,  # type: ignore[arg-type]
        args=training_config,
        train_dataset=train_dataset,
        eval_dataset=eval_dataset,
        processing_class=tokenizer,
    )
    
    print(f"\n{'='*60}")
    print(f"Starting Training!")
    print(f"AJ-DeepSeekR1Qwen32B-{version}")
    print(f"{'='*60}")
    eff_batch = training_config.per_device_train_batch_size * training_config.gradient_accumulation_steps
    print(f"Effective batch size: {eff_batch}")
    print(f"Max length: {training_config.max_length}")
    print(f"Learning rate: {training_config.learning_rate}")
    print(f"Max steps: {training_config.max_steps}")
    print(f"Output: {output_dir}")
    print(f"{'='*60}\n")
    
    # Train
    if args.resume:
        print(f"Resuming from checkpoint: {args.resume}")
        trainer.train(resume_from_checkpoint=args.resume)
    else:
        trainer.train()
    
    # Save final model
    print(f"\nTraining complete! Saving model...")
    trainer.save_model()
    
    # Save tokenizer
    tokenizer.save_pretrained(output_dir)
    
    # Save version info
    version_info = {
        "version": version,
        "dataset_version": dataset_version,
        "base_model": model_name,
        "lora_r": config.get('lora_r'),
        "lora_alpha": config.get('lora_alpha'),
        "max_steps": config.get('max_steps'),
        "train_examples": len(train_dataset),
        "eval_examples": len(eval_dataset),
    }
    with open(os.path.join(output_dir, "version_info.json"), "w") as f:
        json.dump(version_info, f, indent=2)
    
    print(f"Model saved to {output_dir}")
    print(f"\nNext steps:")
    print(f"1. Merge LoRA adapters: python scripts/merge_adapters.py --adapter-path {output_dir}")
    print(f"2. Convert to GGUF with llama.cpp")
    print(f"3. Quantize to Q4_K_M for RTX 4090 deployment")
    print(f"\nDone!")


if __name__ == "__main__":
    main()
