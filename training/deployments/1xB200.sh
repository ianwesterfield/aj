#!/bin/bash
# =============================================================================
# AJ-DeepSeekR1Qwen32B-v2.1.0-lora - 1x B200 (192GB VRAM)
# Usage: curl -sL <raw-url> | bash
# B200 has 36% more VRAM than H200 - allows larger batch for faster training
#
# Recommended Docker Image: pytorch/pytorch:2.2.0-cuda12.1-cudnn8-devel (or newer)
# =============================================================================
set -e

echo "============================================================"
echo "AJ Training - 1x NVIDIA B200 (192GB)"
echo "Version: v2.1.0-lora"
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

cd "$WORKSPACE"

# Clone or update repository
if [ -d "aj.westerfield.cloud" ]; then
    echo "[1/3] Repository exists, pulling latest..."
    cd aj.westerfield.cloud
    git fetch origin
    git reset --hard "origin/$BRANCH"
    git clean -fd
else
    echo "[1/3] Cloning repository..."
    git clone --branch "$BRANCH" "$REPO_URL"
    cd aj.westerfield.cloud
fi

cd training

echo ""
echo "[2/3] Creating B200-optimized config..."

# B200 config - larger batch size for faster training
cat > configs/mixed_v1_b200.yaml << 'CONFIGEOF'
# =============================================================================
# Mixed Dataset v1 Training Config for B200 (192GB VRAM)
# AJ-DeepSeekR1Qwen32B-v2.1.0-lora
# B200 allows batch_size=4 (vs H200's 2) for ~40% faster training
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

# Batch Size - B200 optimized
# Effective batch size = 4 * 4 = 16 (same as H200, but fewer accumulation steps)
batch_size: 4
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

echo "Created configs/mixed_v1_b200.yaml"

echo ""
echo "[3/3] Starting training pipeline..."
echo ""
echo "Hardware: 1x B200 (192GB VRAM)"
echo "Config:   batch_size=4, grad_accum=4, eff_batch=16"
echo "Speedup:  ~40% faster than H200 (fewer grad accumulation steps)"
echo "Output:   $WORKSPACE/aj.westerfield.cloud/training/AJ-DeepSeekR1Qwen32B-v2.1.0-lora/"
echo ""

export TRAINING_DIR="$WORKSPACE/aj.westerfield.cloud/training"

# Modify setup script to use B200 config
cp setup_and_train_v2.sh setup_and_train_b200.sh
sed -i 's/mixed_v1_h200\.yaml/mixed_v1_b200.yaml/g' setup_and_train_b200.sh

bash setup_and_train_b200.sh

echo ""
echo "============================================================"
echo "Training complete!"
echo "Adapter: $TRAINING_DIR/AJ-DeepSeekR1Qwen32B-v2.1.0-lora/"
echo "============================================================"
