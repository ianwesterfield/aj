#!/bin/bash
# AJ Model Merge & Quantization Router
# Routes to version-specific scripts in subdirectories
#
# Usage: ./merge_and_quantize.sh [q5|q8|both]
#
# Setup (run once per version folder):
#   cd aj-deepseek-r1-32b-Q5_K_M  # or Q8_0
#   python -m venv venv
#   source venv/bin/activate
#   pip install -r requirements.txt

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Version folders
Q5_DIR="$SCRIPT_DIR/aj-deepseek-r1-32b-Q5_K_M"
Q8_DIR="$SCRIPT_DIR/aj-deepseek-r1-32b-Q8_0"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo -e "${GREEN}╔════════════════════════════════════════════╗${NC}"
echo -e "${GREEN}║     AJ Model Merge & Quantization          ║${NC}"
echo -e "${GREEN}╚════════════════════════════════════════════╝${NC}"

# Default to both if no argument
QUANT_TARGET="${1:-both}"

run_q5() {
    echo -e "\n${YELLOW}▶ Running Q5_K_M build (~22GB, RTX 4090 optimized)${NC}"
    if [ ! -f "$Q5_DIR/merge.sh" ]; then
        echo -e "${RED}Error: $Q5_DIR/merge.sh not found${NC}"
        exit 1
    fi
    cd "$Q5_DIR"
    if [ -d "venv" ]; then
        source venv/bin/activate
    else
        echo -e "${YELLOW}Warning: No venv found. Run setup first:${NC}"
        echo "  cd $Q5_DIR && python -m venv venv && source venv/bin/activate && pip install -r requirements.txt"
        exit 1
    fi
    bash merge.sh
}

run_q8() {
    echo -e "\n${YELLOW}▶ Running Q8_0 build (~32GB, high quality)${NC}"
    if [ ! -f "$Q8_DIR/merge.sh" ]; then
        echo -e "${RED}Error: $Q8_DIR/merge.sh not found${NC}"
        exit 1
    fi
    cd "$Q8_DIR"
    if [ -d "venv" ]; then
        source venv/bin/activate
    else
        echo -e "${YELLOW}Warning: No venv found. Run setup first:${NC}"
        echo "  cd $Q8_DIR && python -m venv venv && source venv/bin/activate && pip install -r requirements.txt"
        exit 1
    fi
    bash merge.sh
}

case "$QUANT_TARGET" in
    q5)
        run_q5
        ;;
    q8)
        run_q8
        ;;
    both)
        run_q5
        run_q8
        ;;
    *)
        echo -e "${RED}Usage: $0 [q5|q8|both]${NC}"
        echo ""
        echo "  q5   - Build Q5_K_M (~22GB, RTX 4090 optimized)"
        echo "  q8   - Build Q8_0 (~32GB, high quality)"
        echo "  both - Build both versions (default)"
        echo ""
        echo -e "${YELLOW}Setup (run once per version):${NC}"
        echo "  cd aj-deepseek-r1-32b-Q5_K_M"
        echo "  python -m venv venv"
        echo "  source venv/bin/activate"
        echo "  pip install -r requirements.txt"
        exit 1
        ;;
esac

echo -e "\n${GREEN}╔════════════════════════════════════════════╗${NC}"
echo -e "${GREEN}║     Build Complete!                        ║${NC}"
echo -e "${GREEN}╚════════════════════════════════════════════╝${NC}"
