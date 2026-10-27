#!/bin/bash
# Smoke test for the AJ stack (run inside WSL)
set -u
echo "--- orchestrator health ---"
curl -s -m 5 localhost:8004/health
echo
echo "--- orchestrator classify ---"
curl -s -m 90 -X POST localhost:8004/api/orchestrate/classify \
  -H 'Content-Type: application/json' \
  -d '{"text":"restart nginx on web01"}'
echo
echo "--- embedding server ---"
curl -s -m 30 -X POST localhost:8082/v1/embeddings \
  -H 'Content-Type: application/json' \
  -d '{"model":"qwen3-embedding-0.6b","input":"smoke test"}' \
  | head -c 200
echo
echo "--- llm one-token completion ---"
curl -s -m 120 -X POST localhost:8081/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{"model":"bonsai-2-27b","messages":[{"role":"user","content":"Say OK"}],"max_tokens":5,"temperature":0}' \
  | head -c 500
echo
