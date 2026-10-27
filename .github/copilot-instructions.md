# AJ — Copilot / Contributor Orientation

Quick context for AI assistants and human contributors jumping into this repo.

## What AJ is

A privacy-first, locally-hosted agentic AI assistant. Intent-driven orchestrator
dispatches atomic commands to FunnelCloud agents over gRPC. See
[README.md](../README.md) for the full picture.

## LLM runtime (important — recently changed)

AJ runs inference on a **host-side `llama-server`** (from
[llama.cpp](https://github.com/ggml-org/llama.cpp)), **not Ollama**. It exposes
the standard OpenAI API at `http://localhost:8081/v1/chat/completions`.

- **Model alias:** `ajr1-32b` (AJ-DeepSeek-R1-Qwen-32B, Q4_K_M, 8k ctx)
- **Env var:** `LLM_BASE_URL` (legacy `OLLAMA_BASE_URL` is still honored as a
  fallback, but new code should use `LLM_BASE_URL` + `LLM_MODEL`).
- **Build script:** [`scripts/build_llamacpp.sh`](../scripts/build_llamacpp.sh)
- **Launcher:** [`scripts/start-llama-server.sh`](../scripts/start-llama-server.sh)
- **systemd unit:** [`scripts/llama-server.service`](../scripts/llama-server.service)
- **Wait helper:** [`layers/shared/wait-for-llm.sh`](../layers/shared/wait-for-llm.sh)
  (replaced the old `wait-for-ollama.sh`).

The `ollama` service in [`docker-compose.yaml`](../docker-compose.yaml) is left
commented out for easy rollback; do not re-enable it without discussing.

## Published Models (HuggingFace)

| Repo                                                                                                                        | Contents                                                     | Visibility |
| --------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------ | ---------- |
| [`ianwesterfield/AJ-DeepSeekR1Qwen32B-v2.1.0-GGUF`](https://huggingface.co/ianwesterfield/AJ-DeepSeekR1Qwen32B-v2.1.0-GGUF) | Q4_K_M + Q8_0 GGUFs — load directly with `llama-server`      | Public     |
| [`ianwesterfield/AJ-DeepSeekR1Qwen32B-v2.1.0-lora`](https://huggingface.co/ianwesterfield/AJ-DeepSeekR1Qwen32B-v2.1.0-lora) | LoRA adapter over `deepseek-ai/DeepSeek-R1-Distill-Qwen-32B` | Private    |

The older **v3.0.0 70B** (Llama-3.3-70B QLoRA) is archived adapter-only and is
_not_ the primary runtime — it only comes back for rent-a-GPU experiments.
Intermediate training checkpoints (e.g. step-28000) are intentionally deleted.

## Repo layout cheat sheet

| Path                          | Purpose                                                                      |
| ----------------------------- | ---------------------------------------------------------------------------- |
| `layers/orchestrator-dotnet/` | .NET 10 orchestrator (reasoning engine, API, MCP facade, tests)              |
| `layers/pragmatics/`          | DEPRECATED — folded into the orchestrator (`/api/orchestrate/extract-facts`) |
| `layers/memory/`              | Qdrant-backed semantic memory (Qwen3 embeddings via llama-server)            |
| `layers/extractor/`           | PDF / image / audio extractors (remote VLM + faster-whisper)                 |
| `layers/agents/FunnelCloud/`  | Distributed execution agents                                                 |
| `layers/shared/`              | Shared helpers incl. `wait-for-llm.sh`                                       |
| `filters/`                    | Open-WebUI Python filter (IRON GATE, training capture)                       |
| `training/`                   | QLoRA pipeline, datasets, upload scripts                                     |
| `scripts/`                    | Deploy, build, smoke-test, llama-server lifecycle                            |
| `tests/`                      | Python unit + e2e tests                                                      |
| `secrets/`                    | Local-only secrets + scratch docs (git-ignored sensitive content)            |

## House rules for AI changes

1. **Read before writing.** Search for existing patterns before introducing new
   ones — LLM call sites should all go through the OpenAI chat-completions
   shape now.
2. **Don't re-introduce Ollama-specific code paths** (`/api/generate`,
   Ollama-shaped response parsing). The orchestrator and `fact_extractor.py`
   have been migrated; keep them migrated.
3. **Prefer `LLM_BASE_URL` / `LLM_MODEL`** for new env plumbing. Keep the
   `OLLAMA_*` fallback branch untouched for back-compat.
4. **No unrequested refactors.** Don't add docstrings/comments/type hints to
   files you didn't otherwise change.
5. **Tests are the contract.**
   - .NET: `dotnet test layers/orchestrator-dotnet/AJ.sln`
   - Python: `pytest tests/ -v`
6. **Secrets stay in `secrets/`.** Never paste them into code or docs. The sudo
   password, HF token, and API keys live there.

## Common commands

```powershell
# Build CUDA llama.cpp (WSL, one-time)
wsl -- bash /mnt/c/Code/aj/scripts/build_llamacpp.sh

# Start llama-server in tmux
wsl -- bash /mnt/c/Code/aj/scripts/start-llama-server.sh
wsl -- tmux attach -t llm      # Ctrl-B D to detach

# Bring up the rest of the stack
docker compose up -d
docker compose ps

# Smoke test the LLM endpoint
curl http://localhost:8081/v1/models
```

## Links

- HuggingFace profile: <https://huggingface.co/ianwesterfield>
- llama.cpp upstream: <https://github.com/ggml-org/llama.cpp>
- Open-WebUI: <https://github.com/open-webui/open-webui>
- Qdrant: <https://github.com/qdrant/qdrant>
