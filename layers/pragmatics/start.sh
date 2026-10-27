#!/bin/bash
# Pragmatics API startup script
# Waits for the local LLM (llama.cpp llama-server), then starts the API

set -e

echo "=========================================="
echo "Pragmatics Container Startup"
echo "=========================================="

# ====== Wait for LLM ======
# Block until the OpenAI-compatible endpoint is serving the chat model.
# Env precedence: LLM_BASE_URL > OLLAMA_BASE_URL > OLLAMA_HOST:OLLAMA_PORT.
if [ -n "${LLM_BASE_URL:-}" ]; then
  LLM_URL="${LLM_BASE_URL}"
elif [ -n "${OLLAMA_BASE_URL:-}" ]; then
  LLM_URL="${OLLAMA_BASE_URL}"
else
  LLM_HOST="${OLLAMA_HOST:-localhost}"
  LLM_PORT="${OLLAMA_PORT:-8081}"
  LLM_URL="http://${LLM_HOST}:${LLM_PORT}"
fi
LLM_MODEL="${LLM_MODEL:-${OLLAMA_MODEL:-ajr1-32b}}"

echo "[1/2] Waiting for LLM at $LLM_URL with model $LLM_MODEL..."
/app/shared/wait-for-llm.sh "$LLM_URL" "$LLM_MODEL" 300

# Start Pragmatics API
echo "[2/2] Starting Pragmatics API..."
exec uvicorn server:app --host 0.0.0.0 --port 8001
