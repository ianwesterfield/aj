#!/bin/bash
# =============================================================================
# AJ-DeepSeekR1Qwen32B-v2.1.0-lora - 1x H200 (141GB VRAM)
# Usage: curl -sL <raw-url> | bash
# Or:    REPO_URL=https://github.com/you/repo.git bash 1xH200.sh
#
# Recommended Docker Image: pytorch/pytorch:2.2.0-cuda12.1-cudnn8-devel (or newer)
# =============================================================================
set -e

echo "============================================================"
echo "AJ Training - 1x NVIDIA H200 (141GB)"
echo "Version: v2.1.0-lora"
echo "============================================================"

# Configuration - override with environment variables
REPO_URL="${REPO_URL:-https://github.com/ianwesterfield/aj.westerfield.cloud.git}"
BRANCH="${BRANCH:-main}"
WORKSPACE="${WORKSPACE:-/workspace}"

# Detect workspace (Vast.ai, RunPod, Lambda use different defaults)
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
    echo "[1/2] Repository exists, pulling latest..."
    cd aj.westerfield.cloud
    git fetch origin
    git reset --hard "origin/$BRANCH"
    git clean -fd
else
    echo "[1/2] Cloning repository..."
    git clone --branch "$BRANCH" "$REPO_URL"
    cd aj.westerfield.cloud
fi

echo ""
echo "[2/2] Starting training pipeline..."
echo ""
echo "Hardware: 1x H200 (141GB VRAM)"
echo "Config:   batch_size=2, grad_accum=8, eff_batch=16"
echo "Output:   $WORKSPACE/aj.westerfield.cloud/training/AJ-DeepSeekR1Qwen32B-v2.1.0-lora/"
echo ""

cd training
export TRAINING_DIR="$WORKSPACE/aj.westerfield.cloud/training"

# Run the main setup script
bash setup_and_train_v2.sh

echo ""
echo "============================================================"
echo "Training complete!"
echo "Adapter: $TRAINING_DIR/AJ-DeepSeekR1Qwen32B-v2.1.0-lora/"
echo "============================================================"
