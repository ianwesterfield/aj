"""
Memory API Router

Core endpoints for my memory service:
- /save: Store conversations with embeddings and optional pre-extracted facts
- /search: Find relevant memories via semantic similarity
- /summaries: Get memory summaries for a user

The save endpoint does a "search-first" pattern:
1. Generate embedding from the conversation
2. Search for similar existing memories
3. Store with both original text and any provided facts
4. Return any found context so the filter can inject it

NOTE: Fact extraction (names, dates, preferences, etc.) should be done
by the pragmatics layer before calling this service. Memory is a storage
layer, not a processing layer.
"""

import os
import time
from typing import Dict, Any, Optional, List, Union
import uuid
import json
import hashlib
import requests
import base64
from requests.adapters import HTTPAdapter
from urllib3.util.retry import Retry
from fastapi import APIRouter, HTTPException
from services.qdrant_client import _client, _ensure_collection
from services.embedder import embed_messages, embed

# Fact extractor utilities - actual extraction is no-op (should be done upstream)
from services.fact_extractor import (
    extract_facts,
    format_facts_for_storage,
    facts_to_embedding_text,
)
from utils.schemas import SaveRequest, SearchRequest, MemoryResult
from qdrant_client.http import models

router = APIRouter(tags=["memory"])
collection_name = os.getenv("INDEX_NAME", "user_memory_collection")

# HTTP session with retry for Qdrant REST calls
_http_session = None


def _get_http_session() -> requests.Session:
    """Get a requests session with automatic retry on connection errors."""
    global _http_session
    if _http_session is None:
        _http_session = requests.Session()
        retry = Retry(total=3, backoff_factor=1, status_forcelist=[502, 503, 504])
        adapter = HTTPAdapter(max_retries=retry)
        _http_session.mount("http://", adapter)
        _http_session.mount("https://", adapter)
    return _http_session


def _get_text_content(content) -> str:
    """Extract plain text from message content (handles strings and multi-modal arrays)."""
    if isinstance(content, str):
        return content
    elif isinstance(content, list):
        text_parts = []
        for item in content:
            if isinstance(item, dict):
                if item.get("type") == "text":
                    text_parts.append(item.get("text", ""))
        return " ".join(text_parts)
    return ""


def _serialize_full_content(
    content: Union[str, List[Dict[str, Any]]], max_size_mb: int = 100
) -> Optional[str]:
    """
    Serialize full message content for storage, including multi-modal data.
    Converts images to base64, checks total size limit.

    Returns JSON string of the content, or None if too large.
    """
    if isinstance(content, str):
        # Plain text - check size
        size_bytes = len(content.encode("utf-8"))
        if size_bytes > max_size_mb * 1024 * 1024:
            return None
        return content

    elif isinstance(content, list):
        serialized_items = []
        total_size = 0

        for item in content:
            if not isinstance(item, dict):
                continue

            item_type = item.get("type")
            if item_type == "text":
                text = item.get("text", "")
                text_bytes = text.encode("utf-8")
                total_size += len(text_bytes)
                if total_size > max_size_mb * 1024 * 1024:
                    return None
                serialized_items.append({"type": "text", "text": text})

            elif item_type == "image_url":
                # Convert image to base64
                image_url = item.get("image_url", {})
                if isinstance(image_url, dict):
                    url = image_url.get("url", "")
                else:
                    url = str(image_url)

                try:
                    # For now, assume data URLs or fetch if needed
                    # In practice, Open-WebUI sends base64 data URLs
                    if url.startswith("data:image/"):
                        # Already base64, extract
                        header, data = url.split(",", 1)
                        image_bytes = base64.b64decode(data)
                        total_size += len(image_bytes)
                        if total_size > max_size_mb * 1024 * 1024:
                            return None
                        serialized_items.append(
                            {
                                "type": "image_url",
                                "image_url": {"url": url},  # Keep original
                            }
                        )
                    else:
                        # External URL - could fetch, but for now skip or placeholder
                        # To keep simple, just store the URL
                        serialized_items.append(
                            {"type": "image_url", "image_url": {"url": url}}
                        )
                except Exception:
                    # If conversion fails, skip this item
                    continue

        if total_size > max_size_mb * 1024 * 1024:
            return None

        return json.dumps(serialized_items)

    return None


def _make_uuid(user_id: str, content_hash: str) -> int:
    """
    Generate a deterministic ID for Qdrant from user_id + content hash.
    Same content always gets same ID, so upserts work correctly.
    """
    name = f"{user_id}:{content_hash}"
    uuid_obj = uuid.uuid5(uuid.NAMESPACE_DNS, name)
    return int(uuid_obj.int) % (2**63)  # Keep within 64-bit signed range


def _normalize_fact_value(value: str) -> str:
    """
    Normalize a fact value for deduplication.
    - Lowercase
    - Strip leading articles (the, a, an)
    - Strip extra whitespace
    """
    normalized = value.lower().strip()
    # Strip leading articles
    for article in ["the ", "a ", "an "]:
        if normalized.startswith(article):
            normalized = normalized[len(article) :]
    # Collapse whitespace
    normalized = " ".join(normalized.split())
    return normalized


def _make_fact_uuid(user_id: str, fact_type: str, fact_value: str) -> int:
    """
    Generate a deterministic ID for a fact based on type + normalized value.
    """
    normalized_value = _normalize_fact_value(fact_value)
    fact_key = f"{fact_type.lower()}:{normalized_value}"
    return _make_uuid(user_id, fact_key)


@router.post("/save", status_code=200)
def save_memory(req: SaveRequest) -> Dict[str, Any]:
    """
    Save a conversation to memory.

    ATOMIC: By the time this is called, the decision to save has already
    been made by the upstream filter/orchestrator. This endpoint just:
    1. Extracts text and facts
    2. Embeds the content
    3. Stores in Qdrant (one point per fact for deduplication)

    No classification, no searching - those happen elsewhere.
    """
    _ensure_collection()

    # Extract the last user message for storage (full content + text)
    user_content = None
    user_text = None
    for msg in reversed(req.messages):
        msg_dict = msg.model_dump() if hasattr(msg, "model_dump") else msg
        if msg_dict.get("role") == "user":
            user_content = msg_dict.get("content", "")
            user_text = _get_text_content(user_content)
            break

    if not user_text:
        user_text = "[empty message]"

    # Serialize full content for storage (with size limit)
    serialized_content = _serialize_full_content(user_content)
    if serialized_content is None:
        return {
            "status": "skipped",
            "point_id": None,
            "content_hash": None,
            "reason": "Content too large (>100MB)",
        }

    # Use pre-extracted facts from pragmatics layer (preferred) or fallback to local extraction
    facts = req.facts if req.facts else []
    if not facts:
        # Fallback: try local extraction (usually no-op)
        facts = extract_facts(user_text) if user_text else []

    # If no facts, skip storage (we only store facts now, not raw text)
    if not facts:
        return {
            "status": "skipped",
            "point_id": None,
            "content_hash": None,
            "reason": "No facts to store",
        }

    client = _client()
    saved_points = []

    # Store each fact as its own point for proper deduplication
    for fact in facts:
        fact_type = fact.get("type", "unknown")
        fact_value = fact.get("value", "")

        if not fact_value:
            continue

        # Generate embedding for this specific fact
        fact_text = f"{fact_type}: {fact_value}"
        fact_vector = embed(fact_text)

        # Build payload for this fact
        payload: Dict[str, Any] = {
            "user_id": req.user_id,
            "facts": fact_text,  # Single fact string for compatibility
        }

        # Add source information
        if req.source_type:
            payload["source_type"] = req.source_type
        if req.source_name:
            payload["source_name"] = req.source_name

        # Generate deterministic ID based on type + normalized value
        # This ensures "The Big Bang Theory" and "Big Bang Theory" get the same ID
        point_id = _make_fact_uuid(req.user_id, fact_type, fact_value)

        point = models.PointStruct(id=point_id, vector=fact_vector, payload=payload)
        client.upsert(collection_name=collection_name, points=[point])

        saved_points.append(point_id)
        print(f"[/save] Stored fact: {fact_type}={fact_value} (id={point_id})")

    print(f"[/save] user={req.user_id} saved {len(saved_points)} facts")

    return {
        "status": "saved",
        "point_id": saved_points[0] if saved_points else None,
        "content_hash": None,
        "facts_saved": len(saved_points),
    }


@router.post("/search", response_model=list[MemoryResult])
def search_memory(req: SearchRequest) -> List[MemoryResult]:
    """
    Search for relevant memories by semantic similarity.
    Embeds the query, searches Qdrant, returns matches filtered by user_id.
    """
    # Ensure collection exists before searching
    _ensure_collection()

    query_vec = embed(req.query_text)
    qdrant_host = os.getenv("QDRANT_HOST", "localhost")
    qdrant_port = os.getenv("QDRANT_PORT", "6333")

    try:
        session = _get_http_session()
        search_response = session.post(
            f"http://{qdrant_host}:{qdrant_port}/collections/{collection_name}/points/search",
            json={
                "vector": query_vec,
                "filter": {
                    "must": [{"key": "user_id", "match": {"value": req.user_id}}]
                },
                "limit": req.top_k,
                "with_payload": True,
            },
            timeout=5,
        )

        # Handle collection not found (404) - return empty, not error
        if search_response.status_code == 404:
            print(f"[/search] user={req.user_id} collection not found, returning empty")
            raise HTTPException(status_code=404, detail="No memories found")

        search_response.raise_for_status()
        search_data = search_response.json()
        results = search_data.get("result", []) if "result" in search_data else []

        if not results:
            raise HTTPException(status_code=404, detail="No memories found")

        print(f"[/search] user={req.user_id} results={len(results)}")

        return [
            MemoryResult(
                user_text=pt.get("payload", {}).get("user_text", ""),
                facts=pt.get("payload", {}).get("facts"),
                messages=None,
                score=pt.get("score", 0),
                source_type=pt.get("payload", {}).get("source_type"),
                source_name=pt.get("payload", {}).get("source_name"),
            )
            for pt in results
        ]
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Search failed: {str(e)}")


@router.post("/summaries")
def list_summaries(req: SearchRequest) -> List[Dict[str, Any]]:
    """
    Get memory summaries matching a query.
    Like search but returns just the summary text, not full content.
    """
    query = req.query_text or "personal facts dates names preferences"
    query_vec = embed(query)
    qdrant_host = os.getenv("QDRANT_HOST", "localhost")
    qdrant_port = os.getenv("QDRANT_PORT", "6333")

    try:
        session = _get_http_session()
        search_response = session.post(
            f"http://{qdrant_host}:{qdrant_port}/collections/{collection_name}/points/search",
            json={
                "vector": query_vec,
                "filter": {
                    "must": [{"key": "user_id", "match": {"value": req.user_id}}]
                },
                "limit": req.top_k,
                "with_payload": True,
            },
            timeout=10,
        )
        search_response.raise_for_status()
        data = search_response.json()
        results = data.get("result", [])

        summaries = [
            {
                "summary": pt.get("payload", {}).get("summary"),
                "score": pt.get("score", 0),
            }
            for pt in results
            if pt.get("payload", {}).get("summary")
        ]

        print(f"[/summaries] user={req.user_id} results={len(summaries)}")

        if not summaries:
            raise HTTPException(status_code=404, detail="No summaries found")

        return summaries
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Summaries failed: {str(e)}")
