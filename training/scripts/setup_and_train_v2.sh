#!/bin/bash
# =============================================================================
# AJ Training Setup Script - Mixed Dataset v2.1.0
# Run this on the GPU instance to download, prepare, and train
# =============================================================================

set -e  # Exit on error

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo "============================================================"
echo "AJ-DeepSeekR1Qwen32B-v2.1.0-lora Training Setup"
echo "============================================================"

# Configuration
# Note: WORKSPACE is often preset by cloud providers (Vast.ai, RunPod)
# so we use TRAINING_DIR to avoid conflicts
TRAINING_DIR="${TRAINING_DIR:-/workspace/training}"
DATASETS_DIR="$TRAINING_DIR/datasets"
OUTPUT_DIR="$TRAINING_DIR/AJ-DeepSeekR1Qwen32B-v2.1.0-lora"
PROGRESS_FILE="$TRAINING_DIR/.setup_progress"

# Target dataset sizes
TOTAL_EXAMPLES=300000
WILDCHAT_SAMPLE=100000
ULTRACHAT_SAMPLE=50000

cd $TRAINING_DIR

# Helper function to track progress
mark_step_complete() {
    echo "$1" >> "$PROGRESS_FILE"
}

is_step_complete() {
    grep -q "^$1$" "$PROGRESS_FILE" 2>/dev/null
}

# =============================================================================
# Step 1: Install dependencies
# =============================================================================
STEP="step1_deps"
if is_step_complete "$STEP"; then
    echo -e "${GREEN}[1/5] Dependencies already installed (skipping)${NC}"
else
    echo ""
    echo "[1/5] Installing dependencies..."
    
    # Check if PyTorch is already installed (from Docker image)
    if python -c "import torch" 2>/dev/null; then
        echo "PyTorch already installed from Docker image"
    else
        echo "Installing PyTorch 2.5 + CUDA 12.4..."
        pip install -q torch==2.5.1 torchvision torchaudio --index-url https://download.pytorch.org/whl/cu124
    fi
    
    # Verify CUDA is available
    python -c "import torch; print(f'PyTorch {torch.__version__}, CUDA: {torch.cuda.is_available()}, GPUs: {torch.cuda.device_count()}')"
    
    # Install remaining dependencies
    pip install -q datasets huggingface_hub tqdm requests pyyaml beautifulsoup4 unsloth transformers bitsandbytes accelerate peft trl sentencepiece protobuf
    mark_step_complete "$STEP"
    echo -e "${GREEN}[1/5] Dependencies installed ✓${NC}"
fi

# =============================================================================
# Step 2: Download HuggingFace datasets
# =============================================================================
STEP="step2_download"
if is_step_complete "$STEP"; then
    echo -e "${GREEN}[2/5] Datasets already downloaded (skipping)${NC}"
else
    echo ""
    echo "[2/5] Downloading datasets from HuggingFace..."
    cd $DATASETS_DIR
    
    # The download script has skip-if-exists logic built in
    python3 download_datasets.py --config mixed_v1
    
    mark_step_complete "$STEP"
    echo -e "${GREEN}[2/5] Datasets downloaded ✓${NC}"
fi

# =============================================================================
# Step 3: Download and parse Western apothecary texts
# =============================================================================
STEP="step3_apothecary"
if is_step_complete "$STEP"; then
    echo -e "${GREEN}[3/5] Apothecary data already extracted (skipping)${NC}"
else
    echo ""
    echo "[3/5] Extracting Western apothecary data..."
    cd $DATASETS_DIR
    python3 extract_apothecary.py --download --parse --output raw/apothecary
    
    mark_step_complete "$STEP"
    echo -e "${GREEN}[3/5] Apothecary data extracted ✓${NC}"
fi

# =============================================================================
# Step 4: Build mixed dataset
# =============================================================================
STEP="step4_prepare"
MIXED_TRAIN="$DATASETS_DIR/processed/mixed_v1.train.jsonl"
if [ -f "$MIXED_TRAIN" ] && is_step_complete "$STEP"; then
    EXAMPLE_COUNT=$(wc -l < "$MIXED_TRAIN")
    echo -e "${GREEN}[4/5] Mixed dataset already prepared ($EXAMPLE_COUNT examples) (skipping)${NC}"
else
    echo ""
    echo "[4/5] Building mixed dataset..."
    cd $DATASETS_DIR
    python3 prepare_mixed_v1.py \
        --output processed/mixed_v1 \
        --total $TOTAL_EXAMPLES \
        --eval-ratio 0.01
    
    mark_step_complete "$STEP"
    echo -e "${GREEN}[4/5] Mixed dataset prepared ✓${NC}"
fi

# =============================================================================
# Step 5: Start training
# =============================================================================
echo ""
echo "[5/5] Starting training..."
echo "============================================================"
echo -e "${YELLOW}Training Parameters:${NC}"
echo "  Model: deepseek-ai/DeepSeek-R1-Distill-Qwen-32B"
echo "  Dataset: ~$TOTAL_EXAMPLES examples"
echo "  LoRA: r=64, alpha=128 (1.61% trainable)"
echo "  Output: $OUTPUT_DIR"
echo "============================================================"
cd $TRAINING_DIR

# Log to file and console with timestamps
python3 scripts/train_mixed_h200.py \
    --config configs/mixed_v1_h200.yaml \
    2>&1 | tee -a "training_$(date +%Y%m%d_%H%M%S).log"

echo ""
echo "============================================================"
echo -e "${GREEN}Training complete!${NC}"
echo "Output: $OUTPUT_DIR"
echo "============================================================"
