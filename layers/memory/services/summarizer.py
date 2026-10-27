"""
Summarizer

Generates summaries of stored memories via the local llama.cpp llama-server
(OpenAI-compatible chat endpoint). Falls back to a heuristic first-sentences
summary if the LLM is unreachable. Torch-free.

Env vars:
  - LLM_BASE_URL (default: http://localhost:8081)
  - LLM_MODEL    (default: bonsai-2-27b)
"""

import os
import re

import requests

_session = requests.Session()


def _summarize_llm(text: str, max_words: int) -> str | None:
    """Summarize via the local llama-server. Returns None on any failure."""
    base_url = os.getenv("LLM_BASE_URL", "http://localhost:8081").rstrip("/")
    model = os.getenv("LLM_MODEL", "bonsai-2-27b")

    try:
        resp = _session.post(
            f"{base_url}/v1/chat/completions",
            json={
                "model": model,
                "messages": [
                    {
                        "role": "system",
                        "content": "You summarize text. Reply with ONLY the summary - no preamble.",
                    },
                    {
                        "role": "user",
                        "content": (
                            f"Summarize the key personal facts, dates, names, and "
                            f"preferences in {max_words} words or fewer:\n\n{text}"
                        ),
                    },
                ],
                "max_tokens": max(64, int(max_words * 2)),
                "temperature": 0.1,
            },
            timeout=60,
        )
        resp.raise_for_status()
        out = resp.json()["choices"][0]["message"]["content"]
        return out.strip() or None
    except Exception as e:
        print(f"[summarizer] LLM summarization failed: {e}")
        return None


def summarize(text: str, max_words: int = 60) -> str:
    """
    Summarize text. Tries the local llama-server first, then falls back to
    grabbing the first couple sentences.
    """
    print(
        f"[summarizer] summarize() called, text length={len(text or '')}, max_words={max_words}"
    )
    text = (text or "").strip()
    if not text:
        print("[summarizer] Empty text, returning empty string")
        return ""

    out = _summarize_llm(text, max_words)
    if out:
        print(
            f"[summarizer] LLM summary: {out[:80]}..."
            if len(out) > 80
            else f"[summarizer] LLM summary: {out}"
        )
        return out

    # Fallback: take first sentences and trim to max_words
    print("[summarizer] Using heuristic fallback")
    sentences = re.split(r"(?<=[.!?])\s+", text)
    summary = " ".join(sentences[:2])
    words = summary.split()
    if len(words) > max_words:
        summary = " ".join(words[:max_words]) + "…"
    print(
        f"[summarizer] Heuristic summary: {summary[:80]}..."
        if len(summary) > 80
        else f"[summarizer] Heuristic summary: {summary}"
    )
    return summary
