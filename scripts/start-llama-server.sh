#!/bin/bash
# =============================================================================
# start-llama-server.sh
# Launch llama.cpp servers in persistent tmux sessions.
#
# Sessions:
#   llm   - main brain (port 8081): Bonsai-2-27B ternary (Qwen3.8-27B @ 1.72bpw)
#           with vision mmproj, 64K agentic context, server-side reasoning parse.
#   embed - embeddings (port 8082): Qwen3-Embedding-0.6B for the memory layer.
#
# Profiles (first arg or PROFILE env):
#   bonsai  (default) - PrismML llama.cpp fork + Ternary-Bonsai-2-27B PTQ1_0
#   legacy            - stock llama.cpp + AJ-DeepSeekR1Qwen32B v2.1.0 (8K ctx)
#
# Overrides: MODEL_PATH MODEL_ALIAS PORT CTX NGL HOST_BIND EMBED_PORT
#
# Usage:
#   bash scripts/start-llama-server.sh              # bonsai profile in tmux
#   bash scripts/start-llama-server.sh legacy       # old model in tmux
#   bash scripts/start-llama-server.sh foreground   # bonsai, current shell
# =============================================================================

set -euo pipefail

PROFILE="${PROFILE:-bonsai}"
MODE="tmux"
case "${1:-}" in
    legacy)     PROFILE="legacy" ;;
    bonsai)     PROFILE="bonsai" ;;
    foreground) MODE="foreground" ;;
esac
[ "${2:-}" = "foreground" ] && MODE="foreground"

PRISM_BIN="$HOME/llama-prism/llama-server"
STOCK_BIN="/usr/local/bin/llama-server"

HOST_BIND="${HOST_BIND:-0.0.0.0}"
PORT="${PORT:-8081}"
EMBED_PORT="${EMBED_PORT:-8082}"
EMBED_MODEL="${EMBED_MODEL:-/mnt/c/Models/Qwen3-Embedding/Qwen3-Embedding-0.6B-Q8_0.gguf}"
EMBED_ALIAS="${EMBED_ALIAS:-qwen3-embedding-0.6b}"

if [ "$PROFILE" = "bonsai" ]; then
    LLAMACPP_BIN="${LLAMACPP_BIN:-$PRISM_BIN}"
    MODEL_PATH="${MODEL_PATH:-/mnt/c/Models/Bonsai-2-27B/Ternary-Bonsai-2-27B-PTQ1_0.gguf}"
    MMPROJ_PATH="${MMPROJ_PATH:-/mnt/c/Models/Bonsai-2-27B/Ternary-Bonsai-2-27B-mmproj-Q8_0.gguf}"
    MODEL_ALIAS="${MODEL_ALIAS:-bonsai-2-27b}"
    CTX="${CTX:-65536}"
else
    LLAMACPP_BIN="${LLAMACPP_BIN:-$STOCK_BIN}"
    MODEL_PATH="${MODEL_PATH:-/mnt/c/Models/AJ-DeepSeekR1Qwen32B-v2.1.0/AJ-DeepSeekR1Qwen32B-v2.1.0-Q4_K_M.gguf}"
    MMPROJ_PATH=""
    MODEL_ALIAS="${MODEL_ALIAS:-ajr1-32b}"
    CTX="${CTX:-8192}"
fi

NGL="${NGL:-99}"
LOG_DIR="/mnt/c/Code/westerfield.cloud/Applications/aj/training/logs"
LOG="$LOG_DIR/llama_server.log"
EMBED_LOG="$LOG_DIR/llama_embed.log"
mkdir -p "$LOG_DIR"

if [ ! -x "$LLAMACPP_BIN" ]; then
    echo "ERROR: $LLAMACPP_BIN not found." >&2
    [ "$PROFILE" = "bonsai" ] && echo "Run scripts/download-models.sh to fetch the PrismML fork." >&2
    exit 1
fi
if [ ! -f "$MODEL_PATH" ]; then
    echo "ERROR: Model not found at $MODEL_PATH" >&2
    exit 1
fi

# WSL2 exposes the GPU through /usr/lib/wsl/lib — must be on LD_LIBRARY_PATH.
# The prism fork binaries also need their own lib dir for bundled CUDA libs.
export LD_LIBRARY_PATH="/usr/lib/wsl/lib:$(dirname "$LLAMACPP_BIN")${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

CMD=(
    "$LLAMACPP_BIN"
    --model "$MODEL_PATH"
    --alias "$MODEL_ALIAS"
    --host "$HOST_BIND"
    --port "$PORT"
    --ctx-size "$CTX"
    --n-gpu-layers "$NGL"
    --flash-attn on
    --parallel 1
    --metrics
    --log-colors on
    # Split thinking into reasoning_content; JSON-schema grammar then applies
    # only to the final content channel.
    --reasoning-format auto
    --jinja
)
if [ "$PROFILE" = "bonsai" ]; then
    # KV cache quantization: frees VRAM for the 64K agentic context
    CMD+=(--cache-type-k q8_0 --cache-type-v q8_0)
    # Vision tower: only used for image input, text-only serving unaffected
    [ -f "$MMPROJ_PATH" ] && CMD+=(--mmproj "$MMPROJ_PATH")
    # Bonsai card sampling defaults (per-request params override)
    CMD+=(--temp 1.0 --top-p 0.95 --top-k 20 --min-p 0.05)
fi

EMBED_CMD=(
    "$LLAMACPP_BIN"
    --model "$EMBED_MODEL"
    --alias "$EMBED_ALIAS"
    --host "$HOST_BIND"
    --port "$EMBED_PORT"
    --embedding
    --pooling last
    --ctx-size 8192
    --n-gpu-layers 99
)

if [ "$MODE" = "foreground" ]; then
    exec "${CMD[@]}"
fi

if ! command -v tmux >/dev/null 2>&1; then
    echo "ERROR: tmux not installed. Install with: sudo apt-get install -y tmux" >&2
    exit 1
fi

tmux kill-session -t llm 2>/dev/null || true
tmux kill-session -t embed 2>/dev/null || true

printf -v CMD_STR ' %q' "${CMD[@]}"
printf -v EMBED_CMD_STR ' %q' "${EMBED_CMD[@]}"
LDPATH_Q=$(printf '%q' "$LD_LIBRARY_PATH")

tmux new-session -d -s llm "bash -c 'export LD_LIBRARY_PATH=$LDPATH_Q; ${CMD_STR# } 2>&1 | tee -a \"$LOG\"'"
if [ -f "$EMBED_MODEL" ]; then
    tmux new-session -d -s embed "bash -c 'export LD_LIBRARY_PATH=$LDPATH_Q; ${EMBED_CMD_STR# } 2>&1 | tee -a \"$EMBED_LOG\"'"
else
    echo "WARN: embedding model missing at $EMBED_MODEL — embed session not started" >&2
fi

sleep 1
echo ""
echo "llama-server starting in tmux session 'llm' (profile: $PROFILE)"
echo "  Model:  $MODEL_PATH"
echo "  Alias:  $MODEL_ALIAS   Ctx: $CTX"
echo "  URL:    http://localhost:$PORT/v1/models"
echo "  Embed:  http://localhost:$EMBED_PORT/v1/embeddings ($EMBED_ALIAS)"
echo "  Logs:   $LOG"
echo ""
echo "Attach:  tmux attach -t llm   (or -t embed)"
echo "Stop:    tmux kill-session -t llm; tmux kill-session -t embed"
