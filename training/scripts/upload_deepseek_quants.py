#!/usr/bin/env python3
"""Upload AJ-DeepSeekR1Qwen32B-v2.1.0 GGUFs + LoRA adapter to HuggingFace.

Reads HF token from secrets/huggingface_pat.txt. Uploads:
  - Q4_K_M and Q8_0 GGUFs to public GGUF repo
  - LoRA adapter folder to private adapter repo

Safe to re-run: upload_file/upload_folder skip unchanged files.
"""
from __future__ import annotations

import os
import sys
from pathlib import Path

from huggingface_hub import HfApi, create_repo

# ---------------------------------------------------------------------------
# Paths (WSL view of C:\Models)
# ---------------------------------------------------------------------------
MODELS_DIR = Path("/mnt/c/Models/AJ-DeepSeekR1Qwen32B-v2.1.0")
TOKEN_FILE = Path("/mnt/c/Code/aj/secrets/huggingface_pat.txt")

GGUFS = [
    MODELS_DIR / "AJ-DeepSeekR1Qwen32B-v2.1.0-Q4_K_M.gguf",
    MODELS_DIR / "AJ-DeepSeekR1Qwen32B-v2.1.0-Q8_0.gguf",
]
LORA_DIR = MODELS_DIR / "AJ-DeepSeekR1Qwen32B-v2.1.0-lora"

GGUF_REPO = "ianwesterfield/AJ-DeepSeekR1Qwen32B-v2.1.0-GGUF"
LORA_REPO = "ianwesterfield/AJ-DeepSeekR1Qwen32B-v2.1.0-lora"

GGUF_README = """---
license: mit
base_model: deepseek-ai/DeepSeek-R1-Distill-Qwen-32B
tags:
- gguf
- deepseek
- qwen2
- fine-tuned
- aj
- llama.cpp
---

# AJ-DeepSeekR1Qwen32B-v2.1.0 — GGUF

GGUF quantizations of **AJ v2.1.0**, a LoRA fine-tune of
[deepseek-ai/DeepSeek-R1-Distill-Qwen-32B](https://huggingface.co/deepseek-ai/DeepSeek-R1-Distill-Qwen-32B)
for the AJ orchestrator / agent system.

## Files

| File                                         | Quant   | Size    | Notes                                                            |
| -------------------------------------------- | ------- | ------- | ---------------------------------------------------------------- |
| `AJ-DeepSeekR1Qwen32B-v2.1.0-Q4_K_M.gguf`    | Q4_K_M  | ~19 GB  | **Recommended for 24 GB GPUs (RTX 4090).** Good quality / speed. |
| `AJ-DeepSeekR1Qwen32B-v2.1.0-Q8_0.gguf`      | Q8_0    | ~33 GB  | Near-lossless. Needs 40+ GB VRAM or CPU offload.                 |

## Training

- Base: `deepseek-ai/DeepSeek-R1-Distill-Qwen-32B` (32B, BF16)
- Method: LoRA (r=64, α=128, dropout=0.05), BF16 compute
- Hardware: 1× NVIDIA H200 (141 GB)
- Data: mixed_v1 (200K train / 2K eval)
- Final: step 5000, train loss 0.956, eval loss 0.715, tok-acc 80.9%
- See companion repo `ianwesterfield/AJ-DeepSeekR1Qwen32B-v2.1.0-lora` for
  the raw adapter.

## Usage with llama.cpp

```bash
llama-server -m AJ-DeepSeekR1Qwen32B-v2.1.0-Q4_K_M.gguf \\
    --host 0.0.0.0 --port 8081 \\
    --n-gpu-layers 65 --ctx-size 8192
```

## Chat template

DeepSeek-R1 distill uses the DeepSeek chat template (`<｜User｜>` /
`<｜Assistant｜>` / `<｜end▁of▁sentence｜>`). `llama-server` picks this up
from the embedded tokenizer metadata; no manual template required.
"""

LORA_README = """---
license: mit
base_model: deepseek-ai/DeepSeek-R1-Distill-Qwen-32B
library_name: peft
tags:
- lora
- peft
- deepseek
- qwen2
- aj
---

# AJ-DeepSeekR1Qwen32B-v2.1.0 — LoRA adapter

LoRA adapter fine-tuned from
[deepseek-ai/DeepSeek-R1-Distill-Qwen-32B](https://huggingface.co/deepseek-ai/DeepSeek-R1-Distill-Qwen-32B)
for the AJ orchestrator / agent system. Adapter-only weights (~2.1 GB
after excluding optimizer state).

## Training

- Method: LoRA (r=64, α=128, dropout=0.05)
- Targets: q_proj, k_proj, v_proj, o_proj, gate_proj, up_proj, down_proj
- BF16 compute, 1× H200
- Dataset: mixed_v1 (200K train / 2K eval)
- Final: step 5000, train loss 0.956, eval loss 0.715, tok-acc 80.9%

## Merging

```python
from transformers import AutoModelForCausalLM, AutoTokenizer
from peft import PeftModel
import torch

base = AutoModelForCausalLM.from_pretrained(
    "deepseek-ai/DeepSeek-R1-Distill-Qwen-32B", torch_dtype=torch.bfloat16)
model = PeftModel.from_pretrained(base, "ianwesterfield/AJ-DeepSeekR1Qwen32B-v2.1.0-lora")
merged = model.merge_and_unload()
merged.save_pretrained("./AJ-DeepSeekR1Qwen32B-v2.1.0-merged", max_shard_size="10GB")
```

For pre-quantized GGUFs, see
[ianwesterfield/AJ-DeepSeekR1Qwen32B-v2.1.0-GGUF](https://huggingface.co/ianwesterfield/AJ-DeepSeekR1Qwen32B-v2.1.0-GGUF).
"""

# Don't upload optimizer / RNG / training-only state with the adapter.
LORA_IGNORE = [
    "optimizer.pt",
    "scheduler.pt",
    "training_args.bin",
    "rng_state_*.pth",
    "checkpoint-*/optimizer.pt",
    "checkpoint-*/scheduler.pt",
    "checkpoint-*/training_args.bin",
    "checkpoint-*/rng_state_*.pth",
]


def load_token() -> str:
    tok = TOKEN_FILE.read_text().strip()
    if not tok:
        print(f"ERROR: HF token at {TOKEN_FILE} is empty", file=sys.stderr)
        sys.exit(2)
    return tok


def upload_ggufs(api: HfApi) -> None:
    print(f"\n=== Ensuring GGUF repo: {GGUF_REPO} (public) ===", flush=True)
    create_repo(GGUF_REPO, repo_type="model", exist_ok=True, private=False)

    for gguf in GGUFS:
        if not gguf.is_file():
            print(f"  SKIP missing: {gguf}", flush=True)
            continue
        size_gb = gguf.stat().st_size / 1e9
        print(f"\n  Uploading {gguf.name} ({size_gb:.2f} GB)...", flush=True)
        api.upload_file(
            path_or_fileobj=str(gguf),
            path_in_repo=gguf.name,
            repo_id=GGUF_REPO,
            repo_type="model",
            commit_message=f"Upload {gguf.name}",
        )
        print(f"  Done: {gguf.name}", flush=True)

    print("\n  Uploading README.md", flush=True)
    api.upload_file(
        path_or_fileobj=GGUF_README.encode("utf-8"),
        path_in_repo="README.md",
        repo_id=GGUF_REPO,
        repo_type="model",
        commit_message="Add model card",
    )
    print(f"  https://huggingface.co/{GGUF_REPO}", flush=True)


def upload_lora(api: HfApi) -> None:
    if not LORA_DIR.is_dir():
        print(f"\n  SKIP LoRA upload: {LORA_DIR} not found", flush=True)
        return
    print(f"\n=== Ensuring LoRA repo: {LORA_REPO} (PRIVATE) ===", flush=True)
    create_repo(LORA_REPO, repo_type="model", exist_ok=True, private=True)

    print(f"\n  Uploading folder {LORA_DIR} ...", flush=True)
    api.upload_folder(
        folder_path=str(LORA_DIR),
        repo_id=LORA_REPO,
        repo_type="model",
        ignore_patterns=LORA_IGNORE,
        commit_message="Upload LoRA adapter (excluding optimizer state)",
    )
    print("  Done uploading LoRA folder", flush=True)

    print("\n  Uploading README.md", flush=True)
    api.upload_file(
        path_or_fileobj=LORA_README.encode("utf-8"),
        path_in_repo="README.md",
        repo_id=LORA_REPO,
        repo_type="model",
        commit_message="Add adapter card",
    )
    print(f"  https://huggingface.co/{LORA_REPO}", flush=True)


def main() -> int:
    token = load_token()
    # HfApi will pick up token from HF_TOKEN env or explicit param
    api = HfApi(token=token)
    os.environ["HF_TOKEN"] = token  # for any nested hf calls
    # Keep hf_transfer off unless installed; normal upload is fine
    os.environ.setdefault("HF_HUB_ENABLE_HF_TRANSFER", "0")

    mode = sys.argv[1] if len(sys.argv) > 1 else "all"
    if mode in ("all", "ggufs"):
        upload_ggufs(api)
    if mode in ("all", "lora"):
        upload_lora(api)
    print("\nAll requested uploads complete.", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
