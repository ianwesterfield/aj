#!/usr/bin/env python3
"""Delete deprecated step-28000 models from Ollama."""
import json
import urllib.request

OLLAMA_URL = "http://localhost:11434"

OLD_MODELS = [
    "aj:70b-step28000-q8",
    "aj:70b-step28000-q6",
    "hf.co/ianwesterfield/AJ-Llama33-70B-v3.0.0-GGUF-step28000:Q6_K",
]

for name in OLD_MODELS:
    print(f"Deleting {name}...")
    data = json.dumps({"name": name}).encode()
    req = urllib.request.Request(
        f"{OLLAMA_URL}/api/delete",
        data=data,
        headers={"Content-Type": "application/json"},
        method="DELETE",
    )
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            print(f"  OK: {resp.status}")
    except Exception as e:
        print(f"  Error: {e}")

print("\n=== Remaining models ===")
with urllib.request.urlopen(f"{OLLAMA_URL}/api/tags") as resp:
    tags = json.loads(resp.read())
    for m in tags.get("models", []):
        size_gb = m["size"] / 1e9
        print(f"  {m['name']}: {size_gb:.1f} GB")
