"""
Embedding Service

Generates semantic embeddings via an OpenAI-compatible llama.cpp embedding
server (Qwen3-Embedding-0.6B by default, 1024-dim, L2 normalized — so COSINE
= DOT product). The model runs on the WSL host GPU alongside the main LLM;
this service stays torch-free.

Env vars:
  EMBEDDING_BASE_URL (default http://localhost:8082)
  EMBEDDING_MODEL    (default qwen3-embedding-0.6b)

Key functions:
    embed(text) - embed a single string
    embed_messages(messages) - embed a full conversation with role prefixes
"""

import math
import os
from typing import List

import requests

_session = requests.Session()


def _embed_remote(text: str) -> list[float]:
    base_url = os.getenv("EMBEDDING_BASE_URL", "http://localhost:8082").rstrip("/")
    model = os.getenv("EMBEDDING_MODEL", "qwen3-embedding-0.6b")

    resp = _session.post(
        f"{base_url}/v1/embeddings",
        json={"model": model, "input": text},
        timeout=30,
    )
    resp.raise_for_status()
    vec = resp.json()["data"][0]["embedding"]

    # Normalize so COSINE and DOT give identical ordering
    norm = math.sqrt(sum(v * v for v in vec))
    if norm > 0:
        vec = [v / norm for v in vec]
    return vec


def _extract_text_from_content(content: List[dict] | str) -> str:
    """
    Extract text from message content. Handles both plain strings
    and multi-modal arrays (text + images). Images become "[image]".
    """
    if isinstance(content, str):
        return content
    
    text_parts = []
    
    if isinstance(content, list):
        
        for item in content:
            
            if isinstance(item, dict):
    
                if item.get("type") == "text" and "text" in item:
                    text_parts.append(item["text"])
                elif item.get("type") == "image_url":
                    # For images, add a placeholder
                    text_parts.append("[image]")
    
    return " ".join(text_parts)

def embed_messages(messages: List[dict]) -> list[float]:
    """Turn a full conversation into a single 768-dim vector.
    
    I join all the messages with their roles as prefixes (like "[user] ..."),
    then embed that. Handles both dicts and Pydantic models.
    """
    text_parts = []
    
    for msg in messages:
        if hasattr(msg, 'model_dump'):
            msg = msg.model_dump()
        
        if isinstance(msg, dict):
            role = msg.get("role", "")
            content = msg.get("content", "")
            
            if role:
                text_parts.append(f"[{role}]")
            
            extracted = _extract_text_from_content(content)
            if extracted:
                text_parts.append(extracted)
    
    combined_text = " ".join(text_parts)
    
    if not combined_text.strip():
        combined_text = "[empty conversation]"
    
    return _embed_remote(combined_text)

def embed(text: str) -> list[float]:
    """Embed a single string. Low-level version—use embed_messages() for conversations."""
    return _embed_remote(text)