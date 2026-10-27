#!/usr/bin/env python3
"""
LoRA Training for Llama-3.3-70B-Instruct.

Supports two hardware targets via config:
  1. Single-GPU quantized (RTX PRO 6000 Blackwell 96GB): load_in_4bit or load_in_8bit
  2. Multi-GPU bf16 no-quant DDP (B200x4 191GB each): load_in_4bit=false, load_in_8bit=false
     Launch with: accelerate launch --config_file configs/accelerate_4gpu.yaml scripts/train_llama33_70b.py ...
     The script auto-detects WORLD_SIZE/LOCAL_RANK from accelerate.

AJ-Llama33-70B-v3.0.0-lora

Key differences vs train_mixed_h200.py:
  - Base model is Llama-3.3-70B (not DeepSeek-R1-Distill-Qwen-32B)
  - Supports both bnb quantized single-GPU path and bf16 no-quant DDP path
  - Applies the TOKENIZER'S native Llama-3.3 chat template (no hand-rolled ChatML)
    * Llama-3.3 format: <|begin_of_text|><|start_header_id|>role<|end_header_id|>\\n\\ncontent<|eot_id|>
    * Using tokenizer.apply_chat_template keeps us in lock-step with Llama's template
      and avoids the <|im_start|>/<|im_end|> mismatch that would tank fine-tuning quality.
  - Paged 8-bit AdamW (fits alongside 8-bit weights)
  - Single-GPU only (Blackwell workstation has 1x RTX PRO 6000, no DDP)

Usage:
    python scripts/train_llama33_70b.py --config configs/llama33_70b_blackwell6000.yaml

Resume:
    python scripts/train_llama33_70b.py --config configs/llama33_70b_blackwell6000.yaml \\
        --resume ./AJ-Llama33-70B-v3.0.0-lora/checkpoint-1500
"""

import argparse
import json
import os
from pathlib import Path

import numpy as np
import torch
import yaml

# Fix for PyTorch 2.6+ where weights_only=True is default.
# Allows numpy arrays/dtypes in checkpoint RNG state files.
# NOTE: we pass (obj, "full.qualname") tuples because checkpoints pickled under
# numpy 2.x reference "numpy._core.multiarray._reconstruct" while numpy 1.x
# reports the same object as "numpy.core.multiarray._reconstruct" — the tuple
# form overrides the matcher so either pickled spelling is accepted.
def _resolve_np_multiarray():
    import importlib

    for modname in ("numpy._core.multiarray", "numpy.core.multiarray"):
        try:
            return importlib.import_module(modname)
        except Exception:
            continue
    raise ImportError("Could not import numpy multiarray module")


_np_multiarray = _resolve_np_multiarray()
_np_reconstruct = _np_multiarray._reconstruct
_safe = [
    (_np_reconstruct, "numpy._core.multiarray._reconstruct"),
    (_np_reconstruct, "numpy.core.multiarray._reconstruct"),
    (np.ndarray, "numpy.ndarray"),
    (np.dtype, "numpy.dtype"),
]
# Add every scalar dtype class numpy exposes (UInt32DType etc.) — the RNG state
# for the python/cpu/cuda/numpy RNGs pickles at least UInt32DType, and different
# numpy patch versions may pickle others.
try:
    import numpy.dtypes as _np_dtypes

    for _name in dir(_np_dtypes):
        if _name.endswith("DType"):
            _cls = getattr(_np_dtypes, _name)
            _safe.append((_cls, f"numpy.dtypes.{_name}"))
except ImportError:
    pass
torch.serialization.add_safe_globals(_safe)
from datasets import Dataset
from peft import LoraConfig, TaskType, get_peft_model, prepare_model_for_kbit_training
from transformers import (
    AutoModelForCausalLM,
    AutoTokenizer,
    BitsAndBytesConfig,
)
from trl.trainer.sft_config import SFTConfig
from trl.trainer.sft_trainer import SFTTrainer


def parse_args():
    parser = argparse.ArgumentParser(
        description="Train Llama-3.3-70B LoRA on Blackwell"
    )
    parser.add_argument(
        "--config",
        type=str,
        default="configs/llama33_70b_blackwell6000.yaml",
        help="Path to training config",
    )
    parser.add_argument(
        "--resume",
        type=str,
        default=None,
        help="Resume from checkpoint path",
    )
    return parser.parse_args()


def load_jsonl(path: str) -> list:
    data = []
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                data.append(json.loads(line))
            except json.JSONDecodeError:
                continue
    return data


def build_chat_template_mapper(tokenizer):
    """Return a batched .map() function that renders `messages` -> `text` using
    the tokenizer's native Llama-3.3 chat template.

    Expects each example to have a `messages` key: list of {role, content}.
    Uses add_generation_prompt=False so the final assistant turn is included as
    training target (we train on the full conversation tokens).
    """

    def _map(examples):
        texts = []
        for messages in examples["messages"]:
            if not isinstance(messages, list) or not messages:
                texts.append("")
                continue
            try:
                rendered = tokenizer.apply_chat_template(
                    messages,
                    tokenize=False,
                    add_generation_prompt=False,
                )
            except Exception:
                # Skip malformed examples; they'll be filtered by len==0 later.
                rendered = ""
            texts.append(rendered)
        return {"text": texts}

    return _map


def main():
    args = parse_args()

    with open(args.config, "r") as f:
        config = yaml.safe_load(f)

    version = config.get("version", "v3.0.0-lora")
    dataset_version = config.get("dataset_version", "mixed_v1")

    print(f"\n{'=' * 60}")
    print(f"Single-GPU LoRA Training (Blackwell 96GB, 8-bit)")
    print(f"AJ-Llama33-70B-{version}")
    print(f"Dataset: {dataset_version}")
    print(f"{'=' * 60}\n")

    if not torch.cuda.is_available():
        raise RuntimeError("CUDA not available. Blackwell training requires GPU.")
    gpu_name = torch.cuda.get_device_name(0)
    gpu_mem = torch.cuda.get_device_properties(0).total_memory / 1e9
    print(f"GPU: {gpu_name}")
    print(f"VRAM: {gpu_mem:.1f} GB")

    model_name = config.get("model_name", "meta-llama/Llama-3.3-70B-Instruct")
    output_dir = config.get("output_dir", "./AJ-Llama33-70B-v3.0.0-lora")

    # --- Tokenizer -------------------------------------------------------
    print(f"\nLoading tokenizer from {model_name}...")
    tokenizer = AutoTokenizer.from_pretrained(
        model_name,
        trust_remote_code=True,
        padding_side="right",
    )
    if tokenizer.pad_token is None:
        # Llama-3 uses <|end_of_text|> as EOS; reuse it for padding during training.
        tokenizer.pad_token = tokenizer.eos_token

    # --- Model (bf16 no-quant for DDP, or bitsandbytes 4-bit/8-bit single-GPU) ---
    load_in_4bit = bool(config.get("load_in_4bit", False))
    load_in_8bit = bool(config.get("load_in_8bit", False)) and not load_in_4bit
    quantized = load_in_4bit or load_in_8bit

    # Detect multi-GPU launch (accelerate/torchrun sets LOCAL_RANK/WORLD_SIZE)
    is_distributed = int(os.environ.get("WORLD_SIZE", "1")) > 1
    local_rank = int(os.environ.get("LOCAL_RANK", "0"))

    if quantized:
        if load_in_4bit:
            compute_dtype_str = str(
                config.get("bnb_4bit_compute_dtype", "bfloat16")
            ).lower()
        else:
            compute_dtype_str = str(
                config.get("bnb_8bit_compute_dtype", "bfloat16")
            ).lower()
        compute_dtype = (
            torch.bfloat16 if compute_dtype_str == "bfloat16" else torch.float16
        )

        if load_in_4bit:
            bnb_config = BitsAndBytesConfig(
                load_in_4bit=True,
                bnb_4bit_quant_type=str(config.get("bnb_4bit_quant_type", "nf4")),
                bnb_4bit_use_double_quant=bool(
                    config.get("bnb_4bit_use_double_quant", True)
                ),
                bnb_4bit_compute_dtype=compute_dtype,
            )
            print(f"\nLoading model in 4-bit NF4 (compute_dtype={compute_dtype})...")
        else:
            bnb_config = BitsAndBytesConfig(
                load_in_8bit=True,
                bnb_8bit_compute_dtype=compute_dtype,
            )
            print(f"\nLoading model in 8-bit (compute_dtype={compute_dtype})...")

        # Quantized path: bnb requires device_map pinning to a specific GPU per rank.
        device_map = {"": local_rank}

        # Try flash_attention_2 first, fall back to sdpa.
        try:
            model = AutoModelForCausalLM.from_pretrained(
                model_name,
                quantization_config=bnb_config,
                torch_dtype=compute_dtype,
                trust_remote_code=True,
                device_map=device_map,
                low_cpu_mem_usage=True,
                use_cache=False,
                attn_implementation="flash_attention_2",
            )
            print("Using Flash Attention 2")
        except (ImportError, ValueError):
            print("Flash Attention 2 not available, using SDPA")
            model = AutoModelForCausalLM.from_pretrained(
                model_name,
                quantization_config=bnb_config,
                torch_dtype=compute_dtype,
                trust_remote_code=True,
                device_map=device_map,
                low_cpu_mem_usage=True,
                use_cache=False,
                attn_implementation="sdpa",
            )

        total_params = sum(p.numel() for p in model.parameters())
        print(f"Model loaded! Total parameters: {total_params:,}")

        # Prepare k-bit model: casts LayerNorm to fp32, enables input grads, etc.
        model = prepare_model_for_kbit_training(
            model,
            use_gradient_checkpointing=bool(config.get("gradient_checkpointing", True)),
            gradient_checkpointing_kwargs={"use_reentrant": False},
        )
    else:
        # bf16 no-quant path — for DDP across multi-GPU big-VRAM (e.g. B200 191GB x4).
        torch_dtype_str = str(config.get("torch_dtype", "bfloat16")).lower()
        compute_dtype = (
            torch.bfloat16 if torch_dtype_str == "bfloat16" else torch.float16
        )

        if is_distributed:
            print(
                f"\nLoading model in {torch_dtype_str} (no quantization, "
                f"DDP world_size={os.environ.get('WORLD_SIZE')} local_rank={local_rank})..."
            )
        else:
            print(
                f"\nLoading model in {torch_dtype_str} (no quantization, single-GPU)..."
            )

        # Under accelerate/DDP do NOT pass device_map — each rank loads its own
        # replica and accelerate places it on cuda:local_rank. Single-GPU bf16
        # still pins to cuda:local_rank (which is 0 when not distributed).
        from_pretrained_kwargs = dict(
            torch_dtype=compute_dtype,
            trust_remote_code=True,
            low_cpu_mem_usage=True,
            use_cache=False,
        )
        if not is_distributed:
            from_pretrained_kwargs["device_map"] = {"": local_rank}

        try:
            model = AutoModelForCausalLM.from_pretrained(
                model_name,
                attn_implementation="flash_attention_2",
                **from_pretrained_kwargs,
            )
            print("Using Flash Attention 2")
        except (ImportError, ValueError):
            print("Flash Attention 2 not available, using SDPA")
            model = AutoModelForCausalLM.from_pretrained(
                model_name,
                attn_implementation="sdpa",
                **from_pretrained_kwargs,
            )

        total_params = sum(p.numel() for p in model.parameters())
        print(f"Model loaded! Total parameters: {total_params:,}")

        # No bnb kbit prep in bf16 path, but gradient checkpointing +
        # input grads must still be enabled for LoRA over a frozen base.
        if bool(config.get("gradient_checkpointing", True)):
            model.gradient_checkpointing_enable(
                gradient_checkpointing_kwargs={"use_reentrant": False}
            )
        model.enable_input_require_grads()

    # --- LoRA ------------------------------------------------------------
    lora_config = LoraConfig(
        r=int(config.get("lora_r", 64)),
        lora_alpha=int(config.get("lora_alpha", 128)),
        target_modules=config.get(
            "target_modules",
            [
                "q_proj",
                "k_proj",
                "v_proj",
                "o_proj",
                "gate_proj",
                "up_proj",
                "down_proj",
            ],
        ),
        lora_dropout=float(config.get("lora_dropout", 0.05)),
        bias="none",
        task_type=TaskType.CAUSAL_LM,
    )
    print(f"\nApplying LoRA r={lora_config.r} alpha={lora_config.lora_alpha}")
    model = get_peft_model(model, lora_config)

    trainable = sum(p.numel() for p in model.parameters() if p.requires_grad)
    all_params = sum(p.numel() for p in model.parameters())
    print(f"Trainable parameters: {trainable:,} ({100 * trainable / all_params:.2f}%)")

    # --- Dataset ---------------------------------------------------------
    train_file = config.get("train_file", "datasets/processed/mixed_v1.train.jsonl")
    eval_file = config.get("eval_file", "datasets/processed/mixed_v1.eval.jsonl")

    print(f"\nLoading dataset...")
    print(f"  Train: {train_file}")
    print(f"  Eval:  {eval_file}")

    train_data = load_jsonl(train_file)
    eval_data = load_jsonl(eval_file)
    train_dataset = Dataset.from_list(train_data)
    eval_dataset = Dataset.from_list(eval_data)
    print(f"Loaded - Train: {len(train_dataset):,}, Eval: {len(eval_dataset):,}")

    # Render messages -> text using Llama-3.3's native chat template.
    print(f"\nRendering with Llama-3.3 chat template...")
    map_fn = build_chat_template_mapper(tokenizer)
    train_dataset = train_dataset.map(
        map_fn,
        batched=True,
        remove_columns=train_dataset.column_names,
        desc="Applying chat template (train)",
    )
    eval_dataset = eval_dataset.map(
        map_fn,
        batched=True,
        remove_columns=eval_dataset.column_names,
        desc="Applying chat template (eval)",
    )

    train_dataset = train_dataset.filter(lambda x: len(x["text"]) > 0)
    eval_dataset = eval_dataset.filter(lambda x: len(x["text"]) > 0)
    print(
        f"After filtering - Train: {len(train_dataset):,}, Eval: {len(eval_dataset):,}"
    )

    # --- Trainer config --------------------------------------------------
    num_epochs = int(config.get("num_epochs", 2))
    max_steps = int(config.get("max_steps", -1))

    training_config = SFTConfig(
        output_dir=output_dir,
        # Batch
        per_device_train_batch_size=int(config.get("batch_size", 1)),
        per_device_eval_batch_size=1,
        gradient_accumulation_steps=int(config.get("gradient_accumulation_steps", 16)),
        # Duration
        num_train_epochs=num_epochs,
        max_steps=max_steps,
        # Optimizer
        learning_rate=float(config.get("learning_rate", 1.0e-4)),
        weight_decay=float(config.get("weight_decay", 0.0)),
        warmup_ratio=float(config.get("warmup_ratio", 0.03)),
        lr_scheduler_type=str(config.get("lr_scheduler_type", "cosine")),
        optim=str(config.get("optim", "paged_adamw_8bit")),
        # Precision
        bf16=True,
        tf32=True,
        # Memory
        gradient_checkpointing=bool(config.get("gradient_checkpointing", True)),
        gradient_checkpointing_kwargs={"use_reentrant": False},
        # Logging
        logging_steps=int(config.get("logging_steps", 10)),
        logging_first_step=True,
        report_to="tensorboard",
        # Checkpointing
        save_strategy="steps",
        save_steps=int(config.get("save_steps", 500)),
        save_total_limit=3,
        # Evaluation
        eval_strategy="steps",
        eval_steps=int(config.get("eval_steps", 500)),
        # Perf
        dataloader_pin_memory=True,
        dataloader_num_workers=4,
        # SFT
        max_length=int(config.get("max_seq_length", 10240)),
        packing=False,
        dataset_text_field="text",
    )

    trainer = SFTTrainer(
        model=model,  # type: ignore[arg-type]
        args=training_config,
        train_dataset=train_dataset,
        eval_dataset=eval_dataset,
        processing_class=tokenizer,
    )

    eff_batch = (
        training_config.per_device_train_batch_size
        * training_config.gradient_accumulation_steps
    )
    print(f"\n{'=' * 60}")
    print(f"Starting Training")
    print(f"AJ-Llama33-70B-{version}")
    print(f"{'=' * 60}")
    print(f"Effective batch size: {eff_batch}")
    print(f"Max length:           {training_config.max_length}")
    print(f"Learning rate:        {training_config.learning_rate}")
    print(f"Epochs:               {num_epochs}")
    print(f"Optim:                {training_config.optim}")
    print(f"Output:               {output_dir}")
    print(f"{'=' * 60}\n")

    if args.resume:
        print(f"Resuming from checkpoint: {args.resume}")
        trainer.train(resume_from_checkpoint=args.resume)
    else:
        trainer.train()

    print(f"\nTraining complete. Saving adapter...")
    trainer.save_model()
    tokenizer.save_pretrained(output_dir)

    version_info = {
        "version": version,
        "dataset_version": dataset_version,
        "base_model": model_name,
        "lora_r": config.get("lora_r"),
        "lora_alpha": config.get("lora_alpha"),
        "num_epochs": num_epochs,
        "max_seq_length": config.get("max_seq_length"),
        "load_in_8bit": config.get("load_in_8bit"),
        "train_examples": len(train_dataset),
        "eval_examples": len(eval_dataset),
    }
    with open(os.path.join(output_dir, "version_info.json"), "w") as f:
        json.dump(version_info, f, indent=2)

    print(f"Adapter saved to {output_dir}")
    print(f"\nNext steps:")
    print(
        f"  1. Merge LoRA: python scripts/merge_adapters.py --adapter-path {output_dir}"
    )
    print(f"  2. Convert merged model to GGUF (llama.cpp convert_hf_to_gguf.py)")
    print(f"  3. Quantize to Q8_0 (~75GB) or Q5_K_M (~48GB) for Ollama")
    print(
        f"  4. Deploy to Ollama on Blackwell host; point orchestrator OLLAMA_BASE_URL at it"
    )


if __name__ == "__main__":
    main()
