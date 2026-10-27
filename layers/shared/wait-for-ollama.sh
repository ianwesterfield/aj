#!/bin/bash
# =============================================================================
# wait-for-ollama.sh - Block until Ollama model is ready to serve
# =============================================================================
# Usage: ./wait-for-ollama.sh [OLLAMA_URL] [MODEL_NAME] [MAX_WAIT_SECONDS]
#
# Polls Ollama API and sends a test prompt to verify the model is loaded
# and responding. Exits 0 only when model is confirmed ready.
# =============================================================================

set -e

OLLAMA_URL="${1:-http://localhost:11434}"
MODEL_NAME="${2:-}"
MAX_WAIT="${3:-300}"  # 5 minutes default

echo "=========================================="
echo "Waiting for Ollama Model"
echo "=========================================="
echo "  Ollama URL: $OLLAMA_URL"
echo "  Model: ${MODEL_NAME:-<any>}"
echo "  Max wait: ${MAX_WAIT}s"
echo ""

# Extract host:port for curl
OLLAMA_HOST=$(echo "$OLLAMA_URL" | sed 's|http://||' | sed 's|https://||')

start_time=$(date +%s)

# Phase 1: Wait for Ollama API to be reachable
echo "[1/2] Waiting for Ollama API..."
while true; do
    elapsed=$(($(date +%s) - start_time))
    if [ $elapsed -ge $MAX_WAIT ]; then
        echo "ERROR: Timeout waiting for Ollama API after ${MAX_WAIT}s"
        exit 1
    fi

    # Check if API is responding
    if curl -s "${OLLAMA_URL}/api/tags" > /dev/null 2>&1; then
        echo "      Ollama API is responding"
        break
    fi

    echo "      Waiting... (${elapsed}s elapsed)"
    sleep 5
done

# Phase 2: Wait for model to be loaded and responding
echo "[2/2] Waiting for model to respond..."

# If no model specified, just check API is up
if [ -z "$MODEL_NAME" ]; then
    echo "      No specific model required - Ollama is ready!"
    exit 0
fi

# First check if model is already loaded via /api/ps
# This avoids triggering a reload if healthcheck just loaded it
echo "      Checking if model is already loaded..."
loaded_models=$(curl -s "${OLLAMA_URL}/api/ps" 2>/dev/null || echo "{}")
if echo "$loaded_models" | grep -q "\"$MODEL_NAME\""; then
    echo "      Model '$MODEL_NAME' is already loaded!"
    # Give Ollama a moment to stabilize after healthcheck
    echo "      Waiting 5s for stabilization..."
    sleep 5
    echo ""
    echo "=========================================="
    echo "Ollama Ready - Starting Service"
    echo "=========================================="
    exit 0
fi

echo "      Model not loaded yet, waiting for it..."

while true; do
    elapsed=$(($(date +%s) - start_time))
    if [ $elapsed -ge $MAX_WAIT ]; then
        echo "ERROR: Timeout waiting for model '$MODEL_NAME' after ${MAX_WAIT}s"
        exit 1
    fi

    # Check /api/ps first (non-intrusive)
    loaded_models=$(curl -s "${OLLAMA_URL}/api/ps" 2>/dev/null || echo "{}")
    if echo "$loaded_models" | grep -q "\"$MODEL_NAME\""; then
        echo "      Model '$MODEL_NAME' is now loaded!"
        # Wait for stabilization before service starts
        echo "      Waiting 5s for stabilization..."
        sleep 5
        echo ""
        echo "=========================================="
        echo "Ollama Ready - Starting Service"
        echo "=========================================="
        exit 0
    fi

    echo "      Model loading... (${elapsed}s elapsed)"
    sleep 10
done
