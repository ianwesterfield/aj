#!/usr/bin/env python3
"""Upload GGUFs to HuggingFace."""
from huggingface_hub import HfApi, create_repo
from pathlib import Path

REPO_ID = "ianwesterfield/AJ-Llama33-70B-v3.0.0-GGUF"
GGUF_DIR = Path("/workspace/aj/training/deployments/llama33-70b-final")

api = HfApi()

# Create repo if needed
try:
    create_repo(REPO_ID, repo_type="model", exist_ok=True)
    print(f"Repo ready: {REPO_ID}")
except Exception as e:
    print(f"Repo exists or error: {e}")

# Upload each GGUF
for gguf in sorted(GGUF_DIR.glob("*.gguf")):
    size_gb = gguf.stat().st_size / 1e9
    print(f"\nUploading {gguf.name} ({size_gb:.1f} GB)...")
    api.upload_file(
        path_or_fileobj=str(gguf),
        path_in_repo=gguf.name,
        repo_id=REPO_ID,
        repo_type="model",
    )
    print(f"  Done: {gguf.name}")

# Create README
readme = """---
license: llama3.3
base_model: meta-llama/Llama-3.3-70B-Instruct
tags:
- llama
- gguf
- fine-tuned
---

# AJ-Llama33-70B-v3.0.0-GGUF

GGUF quantizations of AJ v3.0.0, fine-tuned from Llama-3.3-70B-Instruct.

## Available Quantizations

| File | Size | Description |
|------|------|-------------|
| AJ-Llama33-70B-v3.0.0.Q8_0.gguf | ~70 GB | 8-bit quantization, highest quality |
| AJ-Llama33-70B-v3.0.0.Q6_K.gguf | ~54 GB | 6-bit K-quant, good balance |

## Training Details

- **Base Model**: meta-llama/Llama-3.3-70B-Instruct
- **Training**: LoRA fine-tuning (r=64, alpha=128)
- **Dataset**: mixed_v1 (279K samples, 2 epochs)
- **Final Step**: 34,930

## Usage

Load with llama.cpp, Ollama, or any GGUF-compatible runtime.
"""

api.upload_file(
    path_or_fileobj=readme.encode(),
    path_in_repo="README.md",
    repo_id=REPO_ID,
    repo_type="model",
)
print("\nREADME uploaded.")
print(f"\nDone! https://huggingface.co/{REPO_ID}")
