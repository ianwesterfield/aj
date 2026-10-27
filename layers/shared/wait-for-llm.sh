#!/bin/bash
# =============================================================================
# wait-for-llm.sh - Block until the OpenAI-compatible LLM endpoint is ready
# =============================================================================
# Usage: ./wait-for-llm.sh [LLM_URL] [MODEL_NAME] [MAX_WAIT_SECONDS]
#
# Polls the OpenAI-compatible /v1/models endpoint (llama.cpp llama-server,
# vLLM, etc.) and then issues a tiny /v1/chat/completions probe to verify
# the model is actually serving. Exits 0 only when the model is confirmed
# ready.
#
# Works against both llama-server (default) and legacy Ollama deployments:
# llama-server exposes /v1/models; Ollama exposes it too at the same path.
# =============================================================================

set -e

LLM_URL="${1:-${LLM_BASE_URL:-http://localhost:8081}}"
MODEL_NAME="${2:-${LLM_MODEL:-}}"
MAX_WAIT="${3:-300}"  # 5 minutes default

echo "=========================================="
echo "Waiting for LLM"
echo "=========================================="
echo "  LLM URL: $LLM_URL"
echo "  Model:   ${MODEL_NAME:-<any>}"
echo "  Max wait: ${MAX_WAIT}s"
echo ""

start_time=$(date +%s)

# Phase 1: Wait for /v1/models to respond
echo "[1/2] Waiting for /v1/models..."
while true; do
    elapsed=$(($(date +%s) - start_time))
    if [ $elapsed -ge $MAX_WAIT ]; then
        echo "ERROR: Timeout waiting for $LLM_URL/v1/models after ${MAX_WAIT}s"
        exit 1
    fi

    if curl -sf "${LLM_URL}/v1/models" > /dev/null 2>&1; then
        echo "      /v1/models is responding"
        break
    fi

    echo "      Waiting... (${elapsed}s elapsed)"
    sleep 5
done

# Phase 2: Verify the model actually serves a completion
echo "[2/2] Sending probe completion..."

if [ -z "$MODEL_NAME" ]; then
    echo "      No specific model required - LLM is ready!"
    exit 0
fi

while true; do
    elapsed=$(($(date +%s) - start_time))
    if [ $elapsed -ge $MAX_WAIT ]; then
        echo "ERROR: Timeout waiting for model '$MODEL_NAME' to serve after ${MAX_WAIT}s"
        exit 1
    fi

    # Minimal probe: 1 token, temperature 0.
    probe=$(curl -sf -X POST "${LLM_URL}/v1/chat/completions" \
        -H "Content-Type: application/json" \
        -d "{
            \"model\": \"${MODEL_NAME}\",
            \"messages\": [{\"role\": \"user\", \"content\": \"hi\"}],
            \"max_tokens\": 1,
            \"temperature\": 0,
            \"stream\": false
        }" 2>/dev/null || echo "")

    if [ -n "$probe" ] && echo "$probe" | grep -q '"choices"'; then
        echo "      Model '$MODEL_NAME' responded to probe"
        echo ""
        echo "=========================================="
        echo "LLM Ready - Starting Service"
        echo "=========================================="
        exit 0
    fi

    echo "      Model loading... (${elapsed}s elapsed)"
    sleep 10
done
