"""
Fact Extractor - Utility Functions

Provides formatting utilities for structured facts in memory storage.

NOTE: Fact extraction itself should happen in the pragmatics layer.
The memory service only stores and formats pre-extracted facts.
This module provides no-op extraction and formatting utilities.
"""

import logging
from typing import List, Dict

logger = logging.getLogger("memory.fact_extractor")


def extract_facts(text: str) -> List[Dict[str, str]]:
    """
    No-op fact extraction stub.
    
    Fact extraction should be performed by the pragmatics layer before
    calling the memory service. Memory is a storage layer, not a 
    processing layer.
    
    Returns empty list - callers should pass pre-extracted facts.
    """
    # Memory layer should not call out to pragmatics
    # Return empty - extraction happens upstream if needed
    return []


def extract_facts_from_document(text: str) -> List[Dict[str, str]]:
    """
    No-op document fact extraction stub.
    
    Returns empty list - callers should pass pre-extracted facts.
    """
    return []


def format_facts_for_storage(facts: List[Dict[str, str]]) -> str:
    """
    Format facts as readable string for storage.
    
    Output: "type: value" on separate lines.
    """
    if not facts:
        return ""
    
    lines = []
    for f in facts:
        lines.append(f"{f['type']}: {f['value']}")
    
    return "\n".join(lines)


def facts_to_embedding_text(facts: List[Dict[str, str]], original_text: str = "") -> str:
    """
    Build text optimized for embedding search.
    
    Combines fact types and values in a searchable format.
    """
    if not facts:
        return ""
    
    parts = []
    for f in facts:
        # Make type readable
        readable_type = f['type'].replace('_', ' ')
        parts.append(f"{readable_type}: {f['value']}")
    
    return " | ".join(parts)
