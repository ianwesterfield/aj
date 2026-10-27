#!/bin/bash
# AJ Model Merge & Quantization - Q8_0 Version
# Model: aj-deepseek-r1-32b-Q8_0 (~32GB, high quality)
#
# Usage:
#   cd /mnt/c/Code/aj.westerfield.cloud/training/scripts/aj-deepseek-r1-32b-Q8_0
#   python -m venv venv
#   source venv/bin/activate
#   pip install -r requirements.txt
#   ./merge.sh

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TRAINING_DIR="$(dirname "$(dirname "$SCRIPT_DIR")")"
CONFIG_DIR="$TRAINING_DIR/configs"
OUTPUT_DIR="$TRAINING_DIR/output"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

QUANT_NAME="Q8_0"
CONFIG_FILE="$CONFIG_DIR/merge_config_q8.yaml"

echo -e "${GREEN}╔════════════════════════════════════════════╗${NC}"
echo -e "${GREEN}║  AJ Model Merge - Q8_0 (High Quality)      ║${NC}"
echo -e "${GREEN}╚════════════════════════════════════════════╝${NC}"

mkdir -p "$OUTPUT_DIR"

echo -e "\n${YELLOW}▶ Merging adapters with config: $CONFIG_FILE${NC}"

# Step 1: Merge LoRA adapters into base model
python3 -c "
from peft import PeftModel
from transformers import AutoModelForCausalLM, AutoTokenizer
import yaml
import torch

with open('$CONFIG_FILE', 'r') as f:
    config = yaml.safe_load(f)

print(f'Loading base model: {config[\"base_model\"]}')
# Load on CPU to avoid offloading issues during merge (needs ~65GB RAM)
base_model = AutoModelForCausalLM.from_pretrained(
    config['base_model'],
    torch_dtype=torch.bfloat16,
    device_map='cpu',
    trust_remote_code=True,
    low_cpu_mem_usage=True
)
tokenizer = AutoTokenizer.from_pretrained(config['base_model'], trust_remote_code=True)

# Merge each adapter
for adapter in config['adapters']:
    print(f'Merging adapter: {adapter[\"path\"]} (weight: {adapter[\"weight\"]})')
    model = PeftModel.from_pretrained(base_model, adapter['path'], device_map='cpu')
    base_model = model.merge_and_unload()
    print('Adapter merged successfully')

# Save merged model
merged_path = config['output'] + '-merged'
print(f'Saving merged model to: {merged_path}')
base_model.save_pretrained(merged_path)
tokenizer.save_pretrained(merged_path)
print('✅ Merge complete!')
"

# Step 2: Convert to GGUF
echo -e "\n${YELLOW}▶ Converting to GGUF format...${NC}"

MERGED_PATH="$OUTPUT_DIR/deepseek-r1-distill-qwen-32b-aj-${QUANT_NAME}-merged"
GGUF_OUTPUT="$OUTPUT_DIR/aj-deepseek-r1-32b-${QUANT_NAME}.gguf"

python3 "$TRAINING_DIR/llama.cpp/convert_hf_to_gguf.py" \
    "$MERGED_PATH" \
    --outfile "$GGUF_OUTPUT" \
    --outtype f16

# Step 3: Quantize
echo -e "\n${YELLOW}▶ Quantizing to $QUANT_NAME...${NC}"

FINAL_OUTPUT="$OUTPUT_DIR/aj-deepseek-r1-32b-${QUANT_NAME}-final.gguf"

"$TRAINING_DIR/llama.cpp/llama-quantize" "$GGUF_OUTPUT" "$FINAL_OUTPUT" "$QUANT_NAME"

# Cleanup intermediate files
rm -f "$GGUF_OUTPUT"

echo -e "\n${GREEN}╔════════════════════════════════════════════╗${NC}"
echo -e "${GREEN}║  Q8_0 Build Complete!                      ║${NC}"
echo -e "${GREEN}╚════════════════════════════════════════════╝${NC}"
echo -e "\nOutput: $FINAL_OUTPUT"
ls -lh "$FINAL_OUTPUT"

echo -e "\n${YELLOW}Next steps:${NC}"
echo "1. Copy GGUF to Ollama models directory"
echo "2. Create Modelfile and import: ollama create aj-deepseek-r1-32b-q8 -f Modelfile"
