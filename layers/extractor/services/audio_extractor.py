"""
Audio Extraction Service

Transcribes audio using faster-whisper (CTranslate2 backend —
~4x faster than openai-whisper at the same accuracy, lower VRAM).

Env vars:
  - WHISPER_MODEL (default: large-v3; also: distil-large-v3, medium, base)
"""

import io
import os

# Lazy-loaded model
_model = None


def _load_model():
    """Load faster-whisper model on first use."""
    global _model

    if _model is not None:
        return

    from faster_whisper import WhisperModel

    model_size = os.getenv("WHISPER_MODEL", "large-v3")

    try:
        import ctranslate2

        use_cuda = ctranslate2.get_cuda_device_count() > 0
    except Exception:
        use_cuda = False

    device = "cuda" if use_cuda else "cpu"
    compute_type = "float16" if use_cuda else "int8"

    print(f"[extractor] Loading faster-whisper '{model_size}' on {device} ({compute_type})...")
    _model = WhisperModel(model_size, device=device, compute_type=compute_type)
    print(f"[extractor] faster-whisper '{model_size}' loaded")


async def extract_from_audio(audio_data: bytes) -> str:
    """
    Transcribe audio to text.

    Args:
        audio_data: Raw audio bytes (wav, mp3, etc.)

    Returns:
        Transcribed text
    """
    _load_model()

    # faster-whisper accepts file-like objects directly — no temp file needed
    segments, _info = _model.transcribe(io.BytesIO(audio_data))
    return " ".join(segment.text.strip() for segment in segments).strip()
