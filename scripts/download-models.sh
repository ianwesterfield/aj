#!/bin/bash
# Download Bonsai-2-27B loadout + PrismML llama.cpp fork (run inside WSL)
set -u
MODELS=/mnt/c/Models
TAG=prism-b10754-2459f68
CUDA_PKG="${1:-12.4}"
mkdir -p "$MODELS/Bonsai-2-27B" "$MODELS/Qwen3-Embedding" "$HOME/llama-prism"

dl() { # url dest
  if [ -f "$2" ]; then echo "SKIP (exists): $2"; else
    echo "GET $1"
    curl -L --fail --retry 3 -C - -o "$2.part" "$1" && mv "$2.part" "$2" && echo "DONE: $2"
  fi
}

dl "https://huggingface.co/prism-ml/Ternary-Bonsai-2-27B-gguf/resolve/main/Ternary-Bonsai-2-27B-PTQ1_0.gguf" \
   "$MODELS/Bonsai-2-27B/Ternary-Bonsai-2-27B-PTQ1_0.gguf"

dl "https://huggingface.co/prism-ml/Ternary-Bonsai-2-27B-gguf/resolve/main/Ternary-Bonsai-2-27B-mmproj-Q8_0.gguf" \
   "$MODELS/Bonsai-2-27B/Ternary-Bonsai-2-27B-mmproj-Q8_0.gguf"

dl "https://huggingface.co/Qwen/Qwen3-Embedding-0.6B-GGUF/resolve/main/Qwen3-Embedding-0.6B-Q8_0.gguf" \
   "$MODELS/Qwen3-Embedding/Qwen3-Embedding-0.6B-Q8_0.gguf"

dl "https://github.com/PrismML-Eng/llama.cpp/releases/download/$TAG/llama-$TAG-bin-linux-cuda-$CUDA_PKG-x64.tar.gz" \
   "$HOME/llama-prism/llama-prism.tar.gz"

tar -xzf "$HOME/llama-prism/llama-prism.tar.gz" -C "$HOME/llama-prism" --strip-components=1 2>/dev/null \
  || tar -xzf "$HOME/llama-prism/llama-prism.tar.gz" -C "$HOME/llama-prism"
ls "$HOME/llama-prism" | head
echo "ALL DOWNLOADS COMPLETE"
