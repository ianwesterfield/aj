"""
Image Vision Service

Generates text descriptions of images by calling the local llama.cpp
llama-server, which serves Bonsai-2-27B with its multimodal projector
(mmproj). Images are sent as base64 data URIs via the OpenAI-compatible
chat completions API — no local vision model, no torch.

Env vars:
  - LLM_BASE_URL (default: http://localhost:8081)
  - LLM_MODEL    (default: bonsai-2-27b)
"""

import base64
import os
from typing import Optional

import httpx

DEFAULT_PROMPT = (
    "Describe this image in detail. Include all visible objects, "
    "people, text, colors, and any notable features."
)

# Magic-byte sniffing for the data URI mime type
_SIGNATURES = [
    (b"\x89PNG", "image/png"),
    (b"\xff\xd8", "image/jpeg"),
    (b"GIF8", "image/gif"),
    (b"RIFF", "image/webp"),
    (b"BM", "image/bmp"),
]


def _mime_type(image_data: bytes) -> str:
    for sig, mime in _SIGNATURES:
        if image_data.startswith(sig):
            return mime
    return "image/png"


async def extract_from_image(image_data: bytes, prompt: Optional[str] = None) -> str:
    """
    Generate text description of an image via the llama-server VLM.

    Args:
        image_data: Raw image bytes (PNG, JPEG, etc.)
        prompt: Optional guided prompt for description

    Returns:
        Text description of the image content
    """
    base_url = os.getenv("LLM_BASE_URL", "http://localhost:8081").rstrip("/")
    model = os.getenv("LLM_MODEL", "bonsai-2-27b")

    data_uri = (
        f"data:{_mime_type(image_data)};base64,"
        f"{base64.b64encode(image_data).decode('ascii')}"
    )

    async with httpx.AsyncClient(timeout=300.0) as client:
        resp = await client.post(
            f"{base_url}/v1/chat/completions",
            json={
                "model": model,
                "messages": [
                    {
                        "role": "user",
                        "content": [
                            {"type": "image_url", "image_url": {"url": data_uri}},
                            {"type": "text", "text": prompt or DEFAULT_PROMPT},
                        ],
                    }
                ],
                "max_tokens": 1024,
                "temperature": 0.2,
            },
        )
        resp.raise_for_status()
        return resp.json()["choices"][0]["message"]["content"].strip()


def load_model_at_startup() -> None:
    """Vision now runs on the shared llama-server — nothing to preload."""
    base_url = os.getenv("LLM_BASE_URL", "http://localhost:8081")
    print(f"[extractor] Vision via llama-server at {base_url} (no local model)")
