#!/usr/bin/env python3
"""
Download datasets for AJ training configurations.

Usage:
    python download_datasets.py --config mixed_v1
    python download_datasets.py --dataset wildchat --sample 100000
"""

import argparse
import json
import os
from pathlib import Path
from typing import Optional

from datasets import load_dataset, Dataset
from huggingface_hub import login
from tqdm import tqdm

# Dataset configurations
DATASET_CONFIGS = {
    "mixed_v1": {
        "wildchat": {
            "hf_path": "allenai/WildChat-1M",
            "sample_size": 100_000,
            "output_dir": "raw/wildchat",
            "filter_tech": True,  # Filter for tech/code conversations
        },
        "ultrachat": {
            "hf_path": "HuggingFaceH4/ultrachat_200k",
            "sample_size": 50_000,
            "output_dir": "raw/ultrachat",
            "split": "train_sft",  # ultrachat_200k exposes train_sft/test_sft/train_gen/test_gen
        },
        "agriculture_qa": {
            "hf_path": "KisanVaani/agriculture-qa-english-only",
            "sample_size": None,  # Use all
            "output_dir": "raw/agriculture",
        },
        "natural_farming": {
            "hf_path": "CopyleftCultivars/Natural-Farming-Real-QandA-Conversations-Q1-2024-Update",
            "sample_size": None,
            "output_dir": "raw/natural_farming",
        },
        # Dropped (no longer on HF Hub as of Apr 2025):
        #   Reggie123/Gardening_LLM_Synthetic_Multiturn
        #   UDACA/StackExchange-gardening
        #   lara-martin/Skein
        # Strategic slot (was skein) has been rolled into domain_knowledge in prepare_mixed_v1.py
    }
}

# Tech/code keywords for WildChat filtering
TECH_KEYWORDS = [
    "python",
    "javascript",
    "typescript",
    "code",
    "programming",
    "function",
    "api",
    "database",
    "sql",
    "react",
    "node",
    "docker",
    "kubernetes",
    "git",
    "linux",
    "server",
    "deploy",
    "debug",
    "error",
    "exception",
    "algorithm",
    "data structure",
    "class",
    "method",
    "variable",
    "loop",
    "array",
    "object",
    "json",
    "html",
    "css",
    "http",
    "rest",
    "graphql",
    "aws",
    "azure",
    "cloud",
    "terminal",
    "command",
    "script",
    "bash",
    "shell",
    "pip",
    "npm",
    "yarn",
    "build",
    "compile",
    "test",
    "unit test",
    "integration",
    "ci/cd",
    "devops",
]


def filter_tech_conversations(
    dataset: Dataset, text_column: str = "conversation"
) -> Dataset:
    """Filter dataset to keep only tech/code related conversations."""

    def is_tech_related(example):
        text = str(example.get(text_column, "")).lower()
        return any(keyword in text for keyword in TECH_KEYWORDS)

    return dataset.filter(is_tech_related, desc="Filtering tech conversations")


def download_dataset(
    hf_path: str,
    output_dir: str,
    sample_size: Optional[int] = None,
    filter_tech: bool = False,
    split: str = "train",
    streaming: bool = False,
) -> int:
    """Download a dataset from HuggingFace and save to disk."""

    output_path = Path(output_dir)
    output_path.mkdir(parents=True, exist_ok=True)

    # Skip if already downloaded
    output_file = output_path / "data.jsonl"
    if output_file.exists():
        # Count existing lines
        with open(output_file, "r") as f:
            existing_count = sum(1 for _ in f)
        if existing_count > 0:
            print(f"\n✓ Already downloaded: {hf_path} ({existing_count:,} examples)")
            return existing_count

    print(f"\n{'='*60}")
    print(f"Downloading: {hf_path}")
    print(f"Output: {output_dir}")
    print(f"{'='*60}")

    try:
        # Use streaming for very large datasets to avoid OOM
        use_streaming = (
            sample_size
            and sample_size < 200_000
            and hf_path in ["allenai/WildChat-1M", "HuggingFaceH4/ultrachat_200k"]
        )

        if use_streaming:
            print(f"Using streaming mode (sampling {sample_size:,} from large dataset)")
            dataset = load_dataset(
                hf_path, split=split, streaming=True, trust_remote_code=True
            )

            # Collect samples
            samples = []
            for i, example in enumerate(
                tqdm(dataset, total=sample_size, desc="Streaming")
            ):
                if filter_tech:
                    # Check if tech-related
                    text = ""
                    for col in ["conversation", "messages", "text", "content"]:
                        if col in example:
                            text = str(example[col]).lower()
                            break
                    if not any(kw in text for kw in TECH_KEYWORDS):
                        continue

                samples.append(example)
                if len(samples) >= sample_size:
                    break

            # Convert to Dataset
            dataset = Dataset.from_list(samples)
            print(f"Collected {len(dataset)} examples via streaming")
        else:
            # Standard loading for smaller datasets
            dataset = load_dataset(hf_path, split=split, trust_remote_code=True)
            print(f"Loaded {len(dataset)} examples")

            # Apply tech filter if requested
            if filter_tech:
                original_size = len(dataset)
                for col in ["conversation", "messages", "text", "content"]:
                    if col in dataset.column_names:
                        dataset = filter_tech_conversations(dataset, col)
                        break
                print(f"Filtered: {original_size} -> {len(dataset)} examples")

            # Sample if requested
            if sample_size and len(dataset) > sample_size:
                dataset = dataset.shuffle(seed=42).select(range(sample_size))
                print(f"Sampled: {sample_size} examples")

        # Save to disk
        output_file = output_path / "data.jsonl"
        dataset.to_json(str(output_file), orient="records", lines=True)

        # Save metadata
        metadata = {
            "source": hf_path,
            "original_size": len(dataset),
            "filtered": filter_tech,
            "sampled": sample_size,
            "final_size": len(dataset),
        }
        with open(output_path / "metadata.json", "w") as f:
            json.dump(metadata, f, indent=2)

        print(f"✓ Saved {len(dataset)} examples to {output_file}")
        return len(dataset)

    except Exception as e:
        print(f"✗ Error downloading {hf_path}: {e}")
        return 0


def download_config(config_name: str):
    """Download all datasets for a configuration."""

    if config_name not in DATASET_CONFIGS:
        print(f"Unknown config: {config_name}")
        print(f"Available: {list(DATASET_CONFIGS.keys())}")
        return

    config = DATASET_CONFIGS[config_name]
    total_examples = 0

    print(f"\n{'#'*60}")
    print(f"# Downloading config: {config_name}")
    print(f"# Datasets: {len(config)}")
    print(f"{'#'*60}")

    for name, settings in config.items():
        count = download_dataset(
            hf_path=settings["hf_path"],
            output_dir=settings["output_dir"],
            sample_size=settings.get("sample_size"),
            filter_tech=settings.get("filter_tech", False),
            split=settings.get("split", "train"),
        )
        total_examples += count

    print(f"\n{'='*60}")
    print(f"Total examples downloaded: {total_examples:,}")
    print(f"{'='*60}")


def main():
    parser = argparse.ArgumentParser(description="Download AJ training datasets")
    parser.add_argument(
        "--config", type=str, help="Download all datasets for a config (e.g., mixed_v1)"
    )
    parser.add_argument("--dataset", type=str, help="Download a single dataset by name")
    parser.add_argument(
        "--sample", type=int, help="Sample size for single dataset download"
    )
    parser.add_argument(
        "--hf-token", type=str, help="HuggingFace token for gated datasets"
    )

    args = parser.parse_args()

    # Login if token provided
    if args.hf_token:
        login(token=args.hf_token)

    if args.config:
        download_config(args.config)
    elif args.dataset:
        # Find dataset in any config
        for config_name, config in DATASET_CONFIGS.items():
            if args.dataset in config:
                settings = config[args.dataset]
                download_dataset(
                    hf_path=settings["hf_path"],
                    output_dir=settings["output_dir"],
                    sample_size=args.sample or settings.get("sample_size"),
                    filter_tech=settings.get("filter_tech", False),
                    split=settings.get("split", "train"),
                )
                return
        print(f"Unknown dataset: {args.dataset}")
    else:
        print("Usage:")
        print("  python download_datasets.py --config mixed_v1")
        print("  python download_datasets.py --dataset wildchat --sample 100000")


if __name__ == "__main__":
    main()
