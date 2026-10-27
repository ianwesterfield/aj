#!/bin/bash
# =============================================================================
# AJ-DeepSeekR1Qwen32B-v2.1.0-lora - 2x H200 (282GB VRAM total)
# Usage: curl -sL <raw-url> | bash
# Multi-GPU DDP training - ~2x faster than single H200
# 
# Recommended Docker Image: pytorch/pytorch:2.2.0-cuda12.1-cudnn8-devel (or newer)
# =============================================================================
set -e

echo "============================================================"
echo "AJ Training - 2x NVIDIA H200 (282GB total)"
echo "Version: v2.1.0-lora (Multi-GPU)"
echo "============================================================"

# Configuration
REPO_URL="${REPO_URL:-https://github.com/ianwesterfield/aj.westerfield.cloud.git}"
BRANCH="${BRANCH:-main}"
WORKSPACE="${WORKSPACE:-/workspace}"

# Detect workspace
if [ -d "/workspace" ]; then
    WORKSPACE="/workspace"
elif [ -d "/root" ]; then
    WORKSPACE="/root"
fi

echo "Workspace: $WORKSPACE"
echo "Repository: $REPO_URL"
echo "Branch: $BRANCH"
echo ""

# Check GPU count
GPU_COUNT=$(nvidia-smi -L | wc -l)
echo "Detected GPUs: $GPU_COUNT"
if [ "$GPU_COUNT" -lt 2 ]; then
    echo "WARNING: Expected 2 GPUs but found $GPU_COUNT"
    echo "Continuing anyway..."
fi
echo ""

cd "$WORKSPACE"

# Clone or update repository
if [ -d "aj.westerfield.cloud" ]; then
    echo "[1/5] Repository exists, pulling latest..."
    cd aj.westerfield.cloud
    git fetch origin
    git reset --hard "origin/$BRANCH"
    git clean -fd
else
    echo "[1/5] Cloning repository..."
    git clone --branch "$BRANCH" "$REPO_URL"
    cd aj.westerfield.cloud
fi

cd training
export TRAINING_DIR="$WORKSPACE/aj.westerfield.cloud/training"

echo ""
echo "[2/5] Installing dependencies..."

# Check if PyTorch is already installed (from Docker image)
if python -c "import torch" 2>/dev/null; then
    echo "PyTorch already installed from Docker image"
else
    echo "Installing PyTorch 2.5 + CUDA 12.4..."
    pip install -q torch==2.5.1 torchvision torchaudio --index-url https://download.pytorch.org/whl/cu124
fi

# Verify CUDA is available
python -c "import torch; print(f'PyTorch {torch.__version__}, CUDA available: {torch.cuda.is_available()}, Devices: {torch.cuda.device_count()}')"

pip install -q transformers datasets peft trl accelerate bitsandbytes sentencepiece protobuf

echo ""
echo "[3/5] Creating 2xH200 config..."

# 2x H200 config
cat > configs/mixed_v1_2xh200.yaml << 'CONFIGEOF'
# =============================================================================
# Mixed Dataset v1 Training Config for 2x H200 (282GB VRAM total)
# AJ-DeepSeekR1Qwen32B-v2.1.0-lora
# DDP across 2 GPUs - batch 2 per GPU, grad_accum 4 = eff_batch 16
# =============================================================================

# Model
model_name: "deepseek-ai/DeepSeek-R1-Distill-Qwen-32B"
output_dir: "./AJ-DeepSeekR1Qwen32B-v2.1.0-lora"

# Dataset
train_file: "datasets/processed/mixed_v1.train.jsonl"
eval_file: "datasets/processed/mixed_v1.eval.jsonl"

# LoRA Configuration
lora_r: 64
lora_alpha: 128
lora_dropout: 0.05
target_modules:
  - "q_proj"
  - "k_proj"
  - "v_proj"
  - "o_proj"
  - "gate_proj"
  - "up_proj"
  - "down_proj"

# Batch Size - 2x H200 optimized
# Per GPU: batch_size=2, across 2 GPUs = 4, grad_accum=4 = eff_batch 16
batch_size: 2
gradient_accumulation_steps: 4

# Training Duration
num_epochs: 1
max_steps: 5000

# Optimizer
learning_rate: 2e-5
weight_decay: 0.01
warmup_steps: 100

# Sequence Length
max_seq_length: 2048

# Logging & Checkpoints
logging_steps: 10
save_steps: 500
eval_steps: 500

# Versioning
version: "v2.1.0-lora"
dataset_version: "mixed_v1"
base_model_code: "DeepSeekR1Qwen32B"
CONFIGEOF

echo "Created configs/mixed_v1_2xh200.yaml"

# Create accelerate config for 2 GPUs
cat > accelerate_config.yaml << 'ACCEOF'
compute_environment: LOCAL_MACHINE
distributed_type: MULTI_GPU
downcast_bf16: 'no'
gpu_ids: all
machine_rank: 0
main_training_function: main
mixed_precision: bf16
num_machines: 1
num_processes: 2
rdzv_backend: static
same_network: true
tpu_env: []
tpu_use_cluster: false
tpu_use_sudo: false
use_cpu: false
ACCEOF

echo "Created accelerate_config.yaml"

echo ""
echo "[4/5] Downloading and preparing datasets..."

cd datasets
python3 download_datasets.py --config mixed_v1 || echo "Dataset download - some may already exist"
python3 prepare_mixed_v1.py --output processed/mixed_v1 || echo "Dataset prep - may already exist"
cd ..

echo ""
echo "[5/5] Starting multi-GPU training..."
echo ""
echo "Hardware: 2x H200 (282GB VRAM total)"
echo "Config:   batch_size=2/GPU, 2 GPUs, grad_accum=4, eff_batch=16"
echo "Speedup:  ~2x faster than single H200"
echo "Output:   $TRAINING_DIR/AJ-DeepSeekR1Qwen32B-v2.1.0-lora/"
echo ""

# Launch with accelerate for multi-GPU
accelerate launch \
    --config_file accelerate_config.yaml \
    --num_processes 2 \
    scripts/train_mixed_h200.py \
    --config configs/mixed_v1_2xh200.yaml

echo ""
echo "============================================================"
echo "Training complete!"
echo "Adapter: $TRAINING_DIR/AJ-DeepSeekR1Qwen32B-v2.1.0-lora/"
echo "============================================================"
