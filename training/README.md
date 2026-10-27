# AJ Training Infrastructure

> **Current Approach**: Single or Multi-GPU LoRA fine-tuning for conversational AI agents

Train DeepSeek-R1-Distill-Qwen-32B for agentic intent logic with conversational adaptability. Optimize model reasoning while preserving base capabilities through parameter-efficient LoRA adaptation.

**Multi-GPU Support**: DDP (Distributed Data Parallel) training is now fully supported on 2+ GPUs using HuggingFace Accelerate. See [Multi-GPU Training](#multi-gpu-training-ddp) section.

---

## Versioning

See [datasets/README.md](datasets/README.md) for full versioning scheme.

```
AJ-DeepSeekR1Qwen32B-v2.1.0-lora
│         │          │ │ │   │
│         │          │ │ │   └─── Suffix: lora, merged, q4km, q8
│         │          │ │ └─────── Patch: hyperparameter tweaks
│         │          │ └───────── Minor: dataset mix adjustments
│         │          └─────────── Major: new base model or architecture
│         └────────────────────── Base model (no spaces)
└──────────────────────────────── Project name
```

### Active Training Runs

| Version       | Dataset  | Status     | Notes                                      |
| ------------- | -------- | ---------- | ------------------------------------------ |
| `v2.1.0-lora` | Mixed-v1 | ⏳ Pending | + Context Switching (Manchurian Candidate) |

---

## Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                        TRAINING PIPELINE                            │
│                                                                     │
│  High-Memory GPU (140GB+ VRAM)                                     │
│  └── DeepSeek-R1-Distill-Qwen-32B (32.76B params)                  │
│      └── LoRA (r=64, ~536M trainable = 1.61%)                      │
│          └── Mixed Dataset (300K examples)                         │
│              ├── 50% Conversational (WildChat + UltraChat)         │
│              ├── 40% Domain Knowledge (~26K from training/data/)   │
│              │   ├── CLI (Linux, Windows, cross-platform)          │
│              │   ├── Development (Python, TS, Node, .NET, etc.)    │
│              │   ├── Infrastructure (Docker, Cloud, Security)      │
│              │   ├── Agentic (AJ persona, Guardrails, Intent)      │
│              │   └── Specialized
│              └── 10% Strategic Reasoning (Skein text adventures)   │
│                                                                     │
│  Output: Quantized GGUF for deployment                             │
└─────────────────────────────────────────────────────────────────────┘
```

### Why This Setup?

| Decision                 | Rationale                                                                        |
| ------------------------ | -------------------------------------------------------------------------------- |
| **Single-GPU LoRA**      | High-VRAM GPU fits 32B model in bf16 + LoRA. No distributed training complexity  |
| **LoRA (not full FT)**   | 99% fewer trainable params. Preserves base model reasoning                       |
| **Mixed datasets**       | Conversational breadth + domain knowledge (plants, apothecary, agriculture)      |
| **Context Switching**    | `contextType` signal teaches model to output conversational vs JSON as needed    |
| **Server-side download** | Avoid large local uploads; fetch 5MB scripts only, download 300K examples on GPU |

---

## Quick Start

### 1. Provision GPU Instance

Use any cloud provider with high-VRAM GPU availability (RunPod, Lambda Labs, Vast.ai, etc.):

```bash
# Requirements:
# - 1x NVIDIA H100/H200 (140GB+ VRAM)
# - 20+ vCPU, 200GB+ RAM
# - 500GB+ SSD for datasets and outputs
```

### 2. Setup Training Environment

SSH into your droplet and run:

```bash
# Clone repo
git clone https://github.com/your-org/aj.westerfield.cloud
cd aj.westerfield.cloud/training

# Run pipeline (5 automatic steps)
bash setup_and_train_v2.sh

# Monitor training
tail -f training_*.log
```

### Start Training (Automated)

The `setup_and_train_v2.sh` script handles everything:

1. Downloads datasets
2. Prepares mixed dataset
3. Starts training
4. Logs to `training_YYYYMMDD_HHMMSS.log`

**No manual steps needed!**

---

## Training Configuration

### Hardware Requirements

| Component   | Single-GPU         | Multi-GPU (2×H200) | Notes                             |
| ----------- | ------------------ | ------------------ | --------------------------------- |
| **GPU**     | 1× 140GB+ VRAM     | 2× 140GB+ VRAM     | H100/H200 recommended             |
| **CPU**     | 20+ cores          | 32+ cores          | For data loading and dataset prep |
| **RAM**     | 200GB+             | 400GB+             | Model loading and merging         |
| **Storage** | 500GB+ SSD         | 500GB+ SSD         | Datasets, checkpoints, outputs    |
| **ETA**     | ~50 hours/2 epochs | ~35 hours/2 epochs | DDP ~30% faster                   |

### LoRA Configuration

```yaml
lora_r: 64
lora_alpha: 128
lora_dropout: 0.05
target_modules:
  - q_proj, k_proj, v_proj, o_proj # Attention
  - gate_proj, up_proj, down_proj # MLP


# Results in ~536M trainable params (1.61% of 32.76B)
```

### Training Hyperparameters

```yaml
# Batch
batch_size: 2
gradient_accumulation_steps: 8
effective_batch_size: 16

# Duration
max_steps: 5000
max_seq_length: 2048

# Optimizer
learning_rate: 2e-5
weight_decay: 0.01
warmup_steps: 100
lr_scheduler: cosine

# Precision
bf16: true
tf32: true
gradient_checkpointing: true
```

---

## Time Estimate

| Phase                          | Single-GPU    | Multi-GPU (2×H200) | Notes                                     |
| ------------------------------ | ------------- | ------------------ | ----------------------------------------- |
| Data Download                  | ~10 min       | ~10 min            | 7 HuggingFace datasets (~2GB)             |
| Dataset Preparation            | ~5 min        | ~10 min            | Each process tokenizes independently      |
| Training (5000 steps)          | ~11 hours     | ~7 hours           | ~2.2s/step vs ~5s/step (but 2 GPUs)       |
| Training (25K steps, 2 epochs) | ~50 hours     | ~35 hours          | Production training for context switching |
| Model Merge + Convert          | ~1 hour       | ~1 hour            | LoRA adapter + quantization               |
| **Total (5K steps)**           | **~12 hours** | **~8 hours**       | Start-to-finish                           |

---

## Dataset: Mixed v1.0 (v2.1.0 Training)

Combines 7 curated datasets for conversational breadth + domain knowledge:

```
127,366 total examples (99:1 train/eval split):
├── 50% Conversational (82K WildChat + 45K UltraChat)
├── 15% Plant/Farming (25K Agriculture + Gardening + StackExchange)
├── 10% Strategic (14.6K Skein text adventures)
├── 10% Western Apothecary (89 public domain herbalism Q&A)
└── 15% Custom AJ Data (45K domain-specific conversations)
```

**Format**: ChatML multi-turn conversations with tool calls

**Example**:

```json
{
  "messages": [
    { "role": "system", "content": "You are AJ..." },
    { "role": "user", "content": "List running containers on domain01" },
    {
      "role": "assistant",
      "tool_calls": [
        {
          "name": "remote_execute",
          "arguments": { "agent_id": "domain01", "command": "docker ps" }
        }
      ]
    },
    { "role": "tool", "content": "CONTAINER ID  IMAGE  ..." },
    { "role": "assistant", "content": "Here are 3 running containers..." }
  ]
}
```

---

## Context Switching ("Manchurian Candidate")

The model learns to switch output modes based on a `contextType` signal in the system prompt:

| contextType | Output Mode                         | Use Case                      |
| ----------- | ----------------------------------- | ----------------------------- |
| `external`  | Conversational, friendly, markdown  | User-facing chat (Open WebUI) |
| `internal`  | Structured JSON with action objects | Agent/tool orchestration      |

**Training Data**: `data/context_switching.jsonl` (999 examples, 50/50 split, weighted 3x)

**Example pairs** (same question, different output):

```json
// contextType: external
{"messages": [
  {"role": "system", "content": "...contextType: external"},
  {"role": "user", "content": "How do I list Docker containers?"},
  {"role": "assistant", "content": "# Listing Docker Containers\\n\\nHere's how..."}
]}

// contextType: internal
{"messages": [
  {"role": "system", "content": "...contextType: internal"},
  {"role": "user", "content": "How do I list Docker containers?"},
  {"role": "assistant", "content": "{\"action\": \"provide_command\", \"command\": \"docker ps\", ...}"}
]}
```

**Filter Integration**: The Open WebUI filter (`filters/aj.filter.py`) injects `contextType` based on intent classification before each request reaches the model.

**Validation**: Run `python scripts/test_context_switching.py` to verify the behavior with a trained adapter.

---

## File Structure

```
training/
├── scripts/
│   ├── train_mixed_h200.py     # Main training script
│   └── merge_adapters.py       # Merge LoRA → full model
├── configs/
│   ├── mixed_v1_h200.yaml      # v2.1.0 training config
│   └── merge_config.yaml       # Merge configuration
├── datasets/
│   ├── download_datasets.py    # HuggingFace dataset loader
│   ├── prepare_mixed_v1.py     # Build 300K mixed dataset
│   ├── extract_apothecary.py   # Parse public domain herbalism texts
│   └── raw/ & processed/       # Dataset cache
└── data/
    └── *.jsonl                 # Custom AJ training data
```

---

## Scripts Reference

### train_mixed_h200.py

Main training script for v2.1.0 mixed dataset:

```bash
# Automatically called by setup_and_train_v2.sh
python scripts/train_mixed_h200.py --config configs/mixed_v1_h200.yaml

# Resume from checkpoint if needed
python scripts/train_mixed_h200.py --config configs/mixed_v1_h200.yaml \
    --resume ./AJ-DeepSeekR1Qwen32B-v2.1.0-lora/checkpoint-2500
```

**Output**: `AJ-DeepSeekR1Qwen32B-v2.1.0-lora/` with LoRA adapter files

### merge_adapters.py

Merge LoRA adapters back into base model:

```bash
python scripts/merge_adapters.py \
    --base-model deepseek-ai/DeepSeek-R1-Distill-Qwen-32B \
    --adapter-path ./AJ-DeepSeekR1Qwen32B-v2.1.0-lora \
    --output-path ./AJ-DeepSeekR1Qwen32B-v2.1.0-merged
```

---

## Troubleshooting

### Out of Memory

If you see CUDA OOM errors:

1. **Reduce batch size** in `configs/mixed_v1.yaml`:

   ```yaml
   batch_size: 1 # Down from 2
   gradient_accumulation_steps: 16 # Double to maintain effective batch
   ```

2. **Reduce sequence length**:
   ```yaml
   max_seq_length: 1024 # Down from 2048
   ```

### Training Stalls

If loss stops decreasing:

1. Check learning rate isn't too high/low
2. Verify data loading (watch GPU utilization)
3. Review gradient norms in logs

### Connection Lost

If SSH disconnects:

1. Use `tmux` or `screen` before training
2. Resume from latest checkpoint with `--resume`

```bash
# Before training
tmux new -s training
python scripts/train_mixed.py ...

# Detach: Ctrl+B, then D
# Reattach after reconnect:
tmux attach -t training
```

---

## Reference: Tool Schema

AJ uses 4 core tools in the .NET 9 orchestrator:

| Tool          | Purpose                     | Example                      |
| ------------- | --------------------------- | ---------------------------- |
| `execute`     | Execute on a specific agent | Run PowerShell on `domain01` |
| `list_agents` | Discover available agents   | Show FunnelCloud agents      |
| `think`       | Reasoning step (internal)   | Plan before execution        |
| `complete`    | Task completion signal      | Mark task finished           |

> **Training data note**: Older training examples may reference `remote_execute` / `remote_execute_all`. The orchestrator's `NormalizeTool()` method maps these to `execute` at runtime.

**Note**: All commands are LLM-generated from training. The orchestrator does NOT provide bash, file read/write, or code execution tools. The LLM knows how to construct PowerShell (Windows) or Bash (Linux) commands.

### Data Sources

| Source                  | Size  | Content                                  |
| ----------------------- | ----- | ---------------------------------------- |
| Conversational datasets | 127K  | WildChat + UltraChat (HuggingFace)       |
| Plant/Farming datasets  | 70K   | Agriculture QA + Gardening (HuggingFace) |
| Strategic/Adventure     | 14.6K | Skein text adventures (HuggingFace)      |
| Western Apothecary      | 89    | Public domain herbalism texts            |
| Custom AJ Data          | 45K+  | Domain-specific conversational data      |

### Generate Custom Data

```bash
# The v2.1.0 pipeline downloads and prepares datasets automatically
# No manual data generation needed for basic training
cd training
bash setup_and_train_v2.sh
```

---

## Local Development (RTX 4090)

For experimentation on local hardware:

### WSL2 Setup (Windows)

```powershell
# Enable WSL2
wsl --install -d Ubuntu-22.04

# Inside WSL
sudo apt update && sudo apt install -y python3-pip python3-venv
cd /mnt/c/Code/aj.westerfield.cloud/training
python3 -m venv venv
source venv/bin/activate
pip install -r requirements.txt
```

### Limitations

RTX 4090 (24GB) cannot train 32B models even with LoRA. Use it for:

- Testing training scripts with smaller models (7B)
- Running inference on quantized models
- Data preprocessing and validation

---

## Previous Approaches (Historical)

Earlier iterations tried different approaches that proved less reliable:

1. **4x H100 + DeepSpeed ZeRO-3** (alternative providers): Complex distributed training, frequent crashes
2. **FSDP + LoRA**: Optimizer state sharding incompatible with LoRA
3. **Full Fine-tuning**: Required 8+ GPUs, expensive

**Update (Jan 2026)**: DDP with HuggingFace Accelerate now works reliably with specific settings. See [Multi-GPU Training](#multi-gpu-training-ddp).

---

## Multi-GPU Training (DDP)

Distributed Data Parallel (DDP) training is now fully supported. This provides ~30% speedup with 2 GPUs.

### Prerequisites

1. **Multiple GPUs** (2× H200 tested, H100 should also work)
2. **HuggingFace Accelerate** installed (`pip install accelerate`)

### Critical DDP Settings

These settings are **REQUIRED** for DDP to work with LoRA fine-tuning:

```python
# In train_mixed_h200.py

# 1. device_map MUST be None (not 'auto')
# Accelerate handles device placement, not AutoModelForCausalLM
model = AutoModelForCausalLM.from_pretrained(
    model_name,
    device_map=None,  # CRITICAL: Must be None for DDP
    torch_dtype=torch.bfloat16,
    ...
)

# 2. use_reentrant MUST be False
# Prevents "parameter marked ready twice" error with gradient checkpointing + DDP
gradient_checkpointing_kwargs={"use_reentrant": False}

# 3. ddp_find_unused_parameters MUST be False
# LoRA only trains ~1.6% of params; base model params are frozen but still in graph
SFTConfig(
    ...
    ddp_find_unused_parameters=False,
)
```

### Accelerate Configuration

Create `training/accelerate_config.yaml`:

```yaml
compute_environment: LOCAL_MACHINE
distributed_type: MULTI_GPU
downcast_bf16: "no"
machine_rank: 0
main_training_function: main
mixed_precision: bf16
num_machines: 1
num_processes: 2 # Number of GPUs
rdzv_backend: static
same_network: true
tpu_env: []
tpu_use_cluster: false
tpu_use_sudo: false
use_cpu: false
```

### Training Config for 2 GPUs

Create `training/configs/mixed_v1_2xh200.yaml`:

```yaml
model_name: deepseek-ai/DeepSeek-R1-Distill-Qwen-32B
dataset_path: ./datasets/processed/mixed_v1_final.jsonl
output_dir: ./AJ-DeepSeekR1Qwen32B-v2.1.0-lora

# LoRA
lora_r: 64
lora_alpha: 128
lora_dropout: 0.05

# Training - adjusted for 2 GPUs
batch_size: 2 # Per GPU
gradient_accumulation_steps: 4 # Effective batch = 2 * 2 * 4 = 16
num_train_epochs: 2
max_seq_length: 2048
learning_rate: 2e-5
warmup_steps: 100
```

### Launch DDP Training

```bash
# Start tmux session (protects against SSH disconnect)
tmux new -s train

# Launch with accelerate
cd /workspace/training
accelerate launch \
    --config_file accelerate_config.yaml \
    scripts/train_mixed_h200.py \
    --config configs/mixed_v1_2xh200.yaml \
    2>&1 | tee training_2gpu.log

# Detach: Ctrl+B, then D
# Reattach: tmux attach -t train
```

### DDP Troubleshooting

| Error                                                                                                | Cause                                            | Fix                                                       |
| ---------------------------------------------------------------------------------------------------- | ------------------------------------------------ | --------------------------------------------------------- |
| `ValueError: You can't train a model that has been loaded with device_map='auto'`                    | `device_map='auto'` incompatible with DDP        | Set `device_map=None`                                     |
| `RuntimeError: Expected to have finished reduction in the prior iteration before starting a new one` | `use_reentrant=True` with gradient checkpointing | Set `use_reentrant=False`                                 |
| `Gradient norms exploding (>10)`                                                                     | DDP gradient sync issues                         | Ensure `ddp_find_unused_parameters=False`                 |
| Both GPUs show 0% utilization                                                                        | Model not distributed                            | Check accelerate config `num_processes` matches GPU count |

### Monitoring DDP Training

```bash
# Check GPU utilization (both should be >90%)
nvidia-smi --query-gpu=utilization.gpu,memory.used --format=csv

# Watch training progress
tail -f /workspace/training/training_2gpu.log

# Check grad norms (should be <1.0 for stable training)
grep grad_norm training_2gpu.log | tail -5
```

### Expected DDP Metrics

| Metric          | Healthy Range    | Notes                                |
| --------------- | ---------------- | ------------------------------------ |
| GPU Memory      | ~88GB each (61%) | Both GPUs should be similar          |
| GPU Utilization | 95-100%          | Both GPUs actively computing         |
| Grad Norm       | 0.3-0.8          | Much lower than single-GPU (1.5-3.0) |
| Step Time       | ~5-6 sec         | With 2× H200                         |
| Loss            | Decreasing       | Should drop from ~1.6 to <1.0        |
