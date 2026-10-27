#!/bin/bash
# Launch HF upload in the background with hf_transfer.
# Usage: bash start_hf_upload.sh [all|ggufs|lora]
set -euo pipefail

MODE="${1:-all}"
LOG_DIR="/mnt/c/Code/aj/training/logs"
mkdir -p "$LOG_DIR"

export HF_HUB_ENABLE_HF_TRANSFER=1
nohup python3 /mnt/c/Code/aj/training/scripts/upload_deepseek_quants.py "$MODE" \
    > "$LOG_DIR/hf_upload_deepseek.log" 2>&1 &
PID=$!
echo "Started upload PID=$PID mode=$MODE"
echo "Log: $LOG_DIR/hf_upload_deepseek.log"
disown
