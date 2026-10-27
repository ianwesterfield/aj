#!/usr/bin/env python3
"""Quick test to debug merge issues."""
import os
import sys

# Set HF token from file
token_file = "/mnt/c/Code/aj.westerfield.cloud/secrets/huggingface_pat.txt"
if os.path.exists(token_file):
    with open(token_file) as f:
        os.environ["HF_TOKEN"] = f.read().strip()
    print(f"HF_TOKEN loaded: {os.environ['HF_TOKEN'][:10]}...")

from peft import PeftModel
from transformers import AutoModelForCausalLM
import torch

ADAPTER_PATH = (
    "/mnt/c/Models/AJ-DeepSeekR1Qwen32B-v2.1.0/AJ-DeepSeekR1Qwen32B-v2.1.0-lora"
)
BASE_MODEL = "deepseek-ai/DeepSeek-R1-Distill-Qwen-32B"

print(f"Loading base model: {BASE_MODEL}")
try:
    base_model = AutoModelForCausalLM.from_pretrained(
        BASE_MODEL,
        torch_dtype=torch.bfloat16,
        device_map="cpu",
        trust_remote_code=True,
        low_cpu_mem_usage=True,
    )
    print("✅ Base model loaded!")
except Exception as e:
    print(f"❌ Base model load failed: {e}")
    sys.exit(1)

print(f"Loading adapter: {ADAPTER_PATH}")
try:
    model = PeftModel.from_pretrained(base_model, ADAPTER_PATH, device_map="cpu")
    print("✅ Adapter loaded!")
except Exception as e:
    print(f"❌ Adapter load failed: {e}")
    import traceback

    traceback.print_exc()
    sys.exit(1)

print("Merging adapter into base model...")
try:
    merged = model.merge_and_unload()
    print("✅ Merge complete!")
except Exception as e:
    print(f"❌ Merge failed: {e}")
    import traceback

    traceback.print_exc()
    sys.exit(1)

print("\n🎉 All steps succeeded!")
