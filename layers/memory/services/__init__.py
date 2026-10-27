"""
Memory Services Package

Core services for the memory API.

Modules:
    embedder: Text embedding via the local llama-server (Qwen3-Embedding, 1024-dim).
    qdrant_client: Singleton Qdrant client and collection management.
    fact_extractor: Utility functions for formatting pre-extracted facts.
    summarizer: LLM-backed text summarization with heuristic fallback.
"""