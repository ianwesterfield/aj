# AJ Layers (Microservices)

This directory contains the Python and .NET microservices that make up AJ's backend.

## Services Overview

| Service                                      | Port  | Technology                                | Purpose                                                    |
| -------------------------------------------- | ----- | ----------------------------------------- | ---------------------------------------------------------- |
| [orchestrator-dotnet/](#orchestrator-net-10) | 8004  | .NET 10, gRPC, MCP                        | Reasoning, classification, fact extraction, agent dispatch |
| [memory/](#memory)                           | 8000  | FastAPI, Qdrant, Qwen3-Embedding (remote) | Semantic memory storage & recall                           |
| [extractor/](#extractor)                     | 8002  | FastAPI, llama-server VLM, faster-whisper | PDF/image/audio extraction                                 |
| [agents/FunnelCloud/](#funnelcloud-agents)   | 41235 | .NET 10, gRPC                             | Distributed command execution                              |
| pragmatics/                                  | —     | DEPRECATED                                | Folded into orchestrator `/api/orchestrate/extract-facts`  |

---

## Orchestrator (.NET 10)

**Path**: `orchestrator-dotnet/`  
**Port**: 8004  
**Active**: ✅ Yes (production)

The reasoning engine that coordinates task execution. Uses a two-tier skill system:

- **Tier 1**: YAML skills execute deterministically (no LLM)
- **Tier 2**: LLM reasoning with SKILL.md context injection

### Key Files

| File                                                        | Purpose                         |
| ----------------------------------------------------------- | ------------------------------- |
| `AJ.Orchestrator.API/Controllers/OrchestratorController.cs` | REST endpoints                  |
| `AJ.Orchestrator.Domain/Services/ReasoningEngine.cs`        | LLM reasoning loop              |
| `AJ.Orchestrator.Domain/Services/SkillExecutor.cs`          | YAML skill matching & execution |
| `AJ.Orchestrator.Domain/Services/AgentDiscoveryService.cs`  | FunnelCloud discovery           |
| `AJ.Orchestrator.Domain/Services/GrpcAgentClient.cs`        | Task execution via gRPC         |

### API Endpoints

| Method | Endpoint                    | Purpose                            |
| ------ | --------------------------- | ---------------------------------- |
| POST   | `/api/orchestrate/classify` | Classify intent (calls pragmatics) |
| POST   | `/api/orchestrate/run-task` | Execute task (SSE stream)          |
| GET    | `/api/orchestrate/agents`   | List discovered agents             |
| GET    | `/api/orchestrate/health`   | Health check                       |

---

## Pragmatics

**Path**: `pragmatics/`  
**Port**: 8001

Natural language understanding: intent classification, entity extraction, fact summarization.

### Models

| Model                | Location                  | Purpose                                         |
| -------------------- | ------------------------- | ----------------------------------------------- |
| DistilBERT 4-class   | `/app/distilbert_intent/` | Intent classification (casual/save/recall/task) |
| spaCy en_core_web_sm | Lazy-loaded               | Named entity recognition                        |

### API Endpoints

| Endpoint                                | Method | Response                                    |
| --------------------------------------- | ------ | ------------------------------------------- |
| `/api/pragmatics/classify`              | POST   | `{intent, confidence, all_probs}`           |
| `/api/pragmatics/classify-with-context` | POST   | Intent with conversation context            |
| `/api/pragmatics/entities`              | POST   | `{names[], orgs[], dates[], emails[], ...}` |
| `/api/pragmatics/extract-facts-storage` | POST   | Memory-worthy facts for storage             |

### Intent Classes

| ID  | Intent   | Description                          |
| --- | -------- | ------------------------------------ |
| 0   | `casual` | Greetings, general chat              |
| 1   | `save`   | "Remember this..."                   |
| 2   | `recall` | "What do you remember about..."      |
| 3   | `task`   | Code execution, workspace operations |

### Retraining

```bash
cd layers/pragmatics/static
python train_intent_classifier.py
docker compose up -d --build pragmatics_api
```

Training examples in: `layers/pragmatics/static/examples/`

---

## Memory

**Path**: `memory/`  
**Port**: 8000

Semantic vector storage using Qdrant for long-term memory persistence.

### Technology

- **Vector DB**: Qdrant (768-dim vectors, COSINE distance)
- **Embeddings**: sentence-transformers `all-mpnet-base-v2`
- **Format**: JSON payloads with `user_id`, `facts`, `source_type`

### API Endpoints

| Endpoint                | Method | Purpose                       |
| ----------------------- | ------ | ----------------------------- |
| `/api/memory/save`      | POST   | Store conversation with facts |
| `/api/memory/search`    | POST   | Semantic search by query      |
| `/api/memory/summaries` | POST   | Get memory summaries          |
| `/api/agent`            | GET    | Serve AJ filter plugin source |
| `/health`               | GET    | Health check                  |

### Request Schemas

**SaveRequest**:

```json
{
  "user_id": "string",
  "messages": [{ "role": "user", "content": "..." }],
  "facts": ["fact1", "fact2"],
  "workspace_context": "optional",
  "metadata": {}
}
```

**SearchRequest**:

```json
{
  "user_id": "string",
  "query_text": "what I'm looking for",
  "top_k": 5
}
```

---

## Extractor

**Path**: `extractor/`  
**Port**: 8002

Media processing for PDF, images, and audio files.

### Supported Formats

| Type          | Handler              | Technology                          |
| ------------- | -------------------- | ----------------------------------- |
| Text/Markdown | `chunker.py`         | Fixed-size + heading-aware chunking |
| Images        | `image_extractor.py` | LLaVA-1.5-7B (4-bit) or Florence-2  |
| Audio         | `audio_extractor.py` | OpenAI Whisper (configurable size)  |
| PDF           | `pdf_extractor.py`   | PyMuPDF (fitz)                      |

### API Endpoints

| Endpoint            | Method | Purpose                     |
| ------------------- | ------ | --------------------------- |
| `/api/extract`      | POST   | Extract from base64 content |
| `/api/extract/file` | POST   | Extract from uploaded file  |
| `/health`           | GET    | Health check                |

### Environment Variables

```bash
IMAGE_MODEL=llava-4bit    # or llava, florence
WHISPER_MODEL=base        # or tiny, small, medium, large
```

---

## FunnelCloud Agents

**Path**: `agents/FunnelCloud/`  
**Ports**: 41420 (UDP), 41421 (HTTP), 41235 (gRPC)

Distributed execution agents for Windows and Linux machines.

### Discovery Protocol

1. **UDP Multicast** (239.255.77.77:41420) - LAN discovery
2. **Gossip** - Cross-subnet peer propagation (every 30s)
3. **HTTP API** - Fallback when multicast fails

### gRPC Service

```protobuf
service TaskService {
    rpc Execute(TaskRequest) returns (TaskResult);
    rpc ExecuteStreaming(TaskRequest) returns (stream TaskOutput);
    rpc GetStatus(TaskStatusRequest) returns (TaskStatusResponse);
    rpc Cancel(CancelRequest) returns (CancelResponse);
}
```

### Task Types

- `SHELL` - bash/cmd
- `POWERSHELL` - PowerShell 7+
- `READ_FILE`, `WRITE_FILE`, `LIST_DIRECTORY`
- `DOTNET_CODE` - Execute .NET snippets

---

## Shared Utilities

**Path**: `shared/`

Common utilities used across Python services.

### Exports

```python
from shared.logging_utils import log_message, LogCategory, LogLevel
from shared.logging_utils import create_status_dict, create_error_dict
```

---

## Deprecated

### orchestrator/ (Python)

**Status**: ⚠️ DEPRECATED  
**Retained for**: Reference only

The Python orchestrator has been replaced by `orchestrator-dotnet/`. Do not use for new development.

---

## Docker Compose

All services are defined in `docker-compose.yaml` at the repo root. Key environment variables:

```yaml
# Ollama
OLLAMA_BASE_URL: http://ollama:11434
OLLAMA_MODEL: r1-distill-aj:32b-8k

# Qdrant
QDRANT_HOST: qdrant
QDRANT_PORT: 6333

# FunnelCloud
FunnelCloud__GossipSeedHost: 192.168.10.166
```
