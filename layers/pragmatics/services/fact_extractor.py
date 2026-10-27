"""
Memory Summarizer

Summarizes what's worth remembering from conversational text using the local
LLM (llama.cpp llama-server via its OpenAI-compatible /v1/chat/completions
endpoint). Instead of extracting structured key:value facts, this creates a
concise summary of personal information, preferences, and context worth
preserving.

This is a middle ground between:
  - Full prompt saving (too much noise)
  - Structured fact extraction (too brittle, loses context)
"""

import os
import logging
from typing import List, Dict, Any, Optional

import httpx

logger = logging.getLogger("pragmatics.memory_summarizer")

# Kept as OLLAMA_* env vars for backwards compatibility with existing
# deployments; the new target is llama.cpp's llama-server on port 8081.
# LLM_BASE_URL (preferred) > OLLAMA_BASE_URL > host+port fallback.
LLM_BASE_URL = (
    os.getenv("LLM_BASE_URL")
    or os.getenv("OLLAMA_BASE_URL")
    or f"http://{os.getenv('OLLAMA_HOST', 'localhost')}:{os.getenv('OLLAMA_PORT', '8081')}"
)
LLM_DEFAULT_MODEL = os.getenv("LLM_MODEL") or os.getenv("OLLAMA_MODEL") or "ajr1-32b"

SUMMARIZE_PROMPT = """Summarize what's worth REMEMBERING about this user from their message.

Focus ONLY on:
- Personal identity (name, location, job)
- Relationships (spouse, family, pets)
- Preferences and opinions ("I prefer...", "I like...", "I hate...")
- Custom terminology ("when I say X, I mean Y")
- Important context for future conversations

DO NOT summarize:
- General questions (e.g., "What is the capital of France?")
- Casual mentions without preference signals
- Technical requests without personal info

If nothing is worth remembering, respond with exactly: NOTHING_TO_REMEMBER

Keep the summary concise - 1-3 sentences max.

Message: {text}

Summary:"""


async def summarize_for_memory(
    text: str, model: Optional[str] = None
) -> Dict[str, Any]:
    """
    Summarize what's worth remembering from text using LLM.

    Returns dict with:
      - summary: string summary of what to remember (or None if nothing worth saving)
      - facts: list with one {type: "memory", value: summary} for storage compatibility

    Uses the main Ollama model for better context understanding.
    """
    if not text or len(text.strip()) < 10:
        return _empty_result()

    llm_model = model or LLM_DEFAULT_MODEL

    prompt = SUMMARIZE_PROMPT.format(text=text)

    try:
        async with httpx.AsyncClient(timeout=120.0) as client:
            response = await client.post(
                f"{LLM_BASE_URL.rstrip('/')}/v1/chat/completions",
                json={
                    "model": llm_model,
                    "messages": [
                        {"role": "user", "content": prompt},
                    ],
                    "stream": False,
                    "temperature": 0.3,
                    "max_tokens": 150,
                },
            )

            if response.status_code != 200:
                logger.warning(f"LLM returned {response.status_code}")
                return _empty_result()

            result = response.json()
            choices = result.get("choices") or []
            if not choices:
                return _empty_result()
            summary = (choices[0].get("message") or {}).get("content", "").strip()

            # Check if LLM said nothing worth remembering
            if not summary or "NOTHING_TO_REMEMBER" in summary.upper():
                logger.debug(f"LLM found nothing to remember: {text[:50]}...")
                return _empty_result()

            # Clean up the summary
            summary = _clean_summary(summary)

            if not summary:
                return _empty_result()

            logger.info(f"Memory summary: {summary[:100]}...")

            # Return in format compatible with existing storage
            return {"summary": summary, "facts": [{"type": "memory", "value": summary}]}

    except httpx.TimeoutException:
        logger.warning("LLM timeout during summarization")
        return _empty_result()
    except httpx.ConnectError:
        logger.warning(f"Cannot connect to LLM at {LLM_BASE_URL}")
        return _empty_result()
    except Exception as e:
        logger.error(f"Summarization failed: {e}")
        return _empty_result()


def _clean_summary(summary: str) -> str:
    """Clean up LLM summary output."""
    # Remove any markdown formatting
    summary = summary.strip()

    # Remove leading "Summary:" if present
    if summary.lower().startswith("summary:"):
        summary = summary[8:].strip()

    # Remove quotes if wrapped
    if summary.startswith('"') and summary.endswith('"'):
        summary = summary[1:-1].strip()

    # Reject if too short or generic
    if len(summary) < 10:
        return ""

    # Reject if it's just a restatement of the prompt
    generic_phrases = [
        "nothing to remember",
        "no personal information",
        "general question",
        "no preferences",
    ]
    if any(phrase in summary.lower() for phrase in generic_phrases):
        return ""

    return summary


def _empty_result() -> Dict[str, Any]:
    """Return empty result."""
    return {"summary": None, "facts": []}


# Compatibility aliases for existing code
async def extract_facts_llm(text: str, model: Optional[str] = None) -> Dict[str, Any]:
    """
    Extract facts from text - now uses summarization.

    This is a compatibility wrapper - new code should use summarize_for_memory().
    """
    return await summarize_for_memory(text, model)


def facts_to_storage_format(result: Dict[str, Any]) -> List[Dict[str, str]]:
    """
    Convert summarization result to storage format.

    Returns list of {type, value} dicts for Qdrant storage.
    """
    return result.get("facts", [])
