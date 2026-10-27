#!/usr/bin/env python3
"""
Prepare Mixed Dataset v1 for AJ training.

Dataset Composition (v3.0.0):
- 50% Conversational patterns (WildChat + UltraChat from HuggingFace)
- 50% Domain knowledge (local .jsonl files: Angular, Python, Docker, SQL,
  plus v3.0.0 skill-aware orchestrator corpora: funnelcloud_tools /
  orchestrator_workflows / final_answer_formatting at 3x oversample)

The strategic (Skein) slice was dropped in v3.0.0 because the source dataset
(lara-martin/Skein) is no longer available on the HF Hub.

The domain knowledge files enhance Llama-3.3-70B's agentic reasoning with
specific technical expertise across infrastructure, development, and DevOps.

Usage:
    python prepare_mixed_v1.py --output processed/mixed_v1
"""

import argparse
import json
import os
import random
from pathlib import Path
from typing import List, Dict, Optional, Generator
from collections import defaultdict
from tqdm import tqdm

# Import extended data files list from separate module
try:
    from extended_categories import EXTENDED_DATA_FILES

    EXTENDED_ENABLED = True
except ImportError:
    EXTENDED_DATA_FILES = []
    EXTENDED_ENABLED = False

# Domain knowledge files from training/data/
# These are loaded proportionally to fill the 40% domain allocation
DOMAIN_KNOWLEDGE_FILES = [
    # Development
    "angular.jsonl",
    "typescript_development.jsonl",
    "python_development.jsonl",
    "nodejs.jsonl",
    "api_development.jsonl",
    "web_fundamentals.jsonl",
    "programming_concepts.jsonl",
    "complex_code.jsonl",
    "code_snippets.jsonl",
    "dotnet.jsonl",
    "scss_css.jsonl",
    # Infrastructure & DevOps
    "docker_containerization.jsonl",
    "linux_admin.jsonl",
    "windows_admin.jsonl",
    "cross_platform_cli.jsonl",
    "cloud_devops.jsonl",
    "devops_cicd.jsonl",
    "networking.jsonl",
    "network_integration.jsonl",
    "security.jsonl",
    # Database & Storage
    "database_sql.jsonl",
    "qdrant_vectors.jsonl",
    "storage_data.jsonl",
    "memory_data.jsonl",
    # Code Quality
    "code_review.jsonl",
    "refactoring.jsonl",
    "testing_debugging.jsonl",
    "architecture.jsonl",
    "architecture_qa.jsonl",
    # Agentic / Guardrails / Persona
    "aj_persona.jsonl",
    "intent_classification.jsonl",
    "guardrails.jsonl",
    "guardrails_extended.jsonl",
    # Planning & Workflows
    "project_planning.jsonl",
    "task_planning.jsonl",
    "generated_planning.jsonl",
    "multistep_workflows.jsonl",
    "multiturn_conversations.jsonl",
    # IDE & Git
    "vscode_ide_workflows.jsonl",
    "git_version_control.jsonl",
    # Project-Specific & Hardware
    "mesosync_workspace.jsonl",
    "aiml_llm.jsonl",
    "firewalla_gold_plus.jsonl",
    "ubiquiti_unifi.jsonl",
    # Specialized Knowledge
    "apothecary.jsonl",
    "herbalism_science.jsonl",
    "fermentation.jsonl",
    # Context Switching (Manchurian Candidate training)
    # Teaches model to switch between conversational and JSON output based on contextType
    "context_switching.jsonl",
    # Orchestrator Tool-Calling (v3.0.0 - Llama-3.3 retraining)
    # Skill-aware, multi-turn trajectories with list_agents/execute/think/complete schema.
    # Heavily weighted so the model reliably learns the 4-tool schema + skill context usage.
    "funnelcloud_tools.jsonl",
    "orchestrator_workflows.jsonl",
    "final_answer_formatting.jsonl",
]

# Add extended data files if available
if EXTENDED_ENABLED and EXTENDED_DATA_FILES:
    DOMAIN_KNOWLEDGE_FILES.extend(EXTENDED_DATA_FILES)

# File-specific weights for oversampling certain datasets
# Higher weight = more copies in training (e.g., 3 = 3x the examples)
# Used for critical behaviors like context switching that must be learned strongly
FILE_WEIGHTS = {
    "context_switching.jsonl": 3,  # Manchurian Candidate: contextType signal must be learned
    # Orchestrator tool-calling - critical v3.0.0 behavior, must be learned reliably.
    # These corpora are small (~400 ex each) relative to WildChat (82K), so aggressive
    # oversampling is needed so the skill-context + 4-tool schema saturates training.
    "funnelcloud_tools.jsonl": 3,
    "orchestrator_workflows.jsonl": 3,
    "final_answer_formatting.jsonl": 3,
}

# Dataset mix ratios for v3.0.0
# NOTE: Strategic (Skein) category dropped — lara-martin/Skein no longer on HF Hub.
# That 10% has been rolled into domain_knowledge, which now carries the new
# skill-aware orchestrator corpora (funnelcloud_tools / orchestrator_workflows /
# final_answer_formatting) and is the highest-value slice for AJ v3.0.0.
DATASET_MIX = {
    "conversational": {
        "ratio": 0.50,  # 50% of total
        "sources": {
            "wildchat": {
                "path": "raw/wildchat/data.jsonl",
                "ratio": 0.65,
            },  # ~100K (tech-filtered)
            "ultrachat": {"path": "raw/ultrachat/data.jsonl", "ratio": 0.35},  # ~50K
        },
    },
    "domain_knowledge": {
        "ratio": 0.50,  # 50% of total - loaded from training/data/*.jsonl
        "sources": {},  # Populated dynamically from DOMAIN_KNOWLEDGE_FILES
    },
}


def load_jsonl(path: Path, max_lines: Optional[int] = None) -> List[Dict]:
    """Load JSONL file."""
    data = []
    if not path.exists():
        print(f"  ⚠ File not found: {path}")
        return data

    with open(path, "r", encoding="utf-8") as f:
        for i, line in enumerate(f):
            if max_lines and i >= max_lines:
                break
            try:
                data.append(json.loads(line))
            except json.JSONDecodeError:
                continue
    return data


def normalize_to_chatml(example: Dict) -> Optional[Dict]:
    """
    Normalize various dataset formats to ChatML messages format.

    Returns:
        {"messages": [{"role": "user", "content": ...}, {"role": "assistant", "content": ...}]}
    """

    # Already in messages format
    if "messages" in example:
        messages = example["messages"]
        if isinstance(messages, list) and len(messages) >= 2:
            return {"messages": messages}

    # conversation format (WildChat style)
    if "conversation" in example:
        conv = example["conversation"]
        if isinstance(conv, list):
            messages = []
            for turn in conv:
                role = turn.get("role", "").lower()
                content = turn.get("content", "")
                if role in ["user", "human"]:
                    messages.append({"role": "user", "content": content})
                elif role in ["assistant", "gpt", "bot"]:
                    messages.append({"role": "assistant", "content": content})
            if len(messages) >= 2:
                return {"messages": messages}

    # Q&A format (Agriculture QA style)
    if "question" in example and "answer" in example:
        return {
            "messages": [
                {"role": "user", "content": example["question"]},
                {"role": "assistant", "content": example["answer"]},
            ]
        }

    # prompt/response format
    if "prompt" in example and "response" in example:
        return {
            "messages": [
                {"role": "user", "content": example["prompt"]},
                {"role": "assistant", "content": example["response"]},
            ]
        }

    # instruction/response format (AJ training data)
    if "instruction" in example and "response" in example:
        user_content = example["instruction"]
        if example.get("input"):
            user_content += f"\n\n{example['input']}"
        messages = []
        # Include system message if present
        if example.get("system"):
            messages.append({"role": "system", "content": example["system"]})
        messages.append({"role": "user", "content": user_content})
        messages.append({"role": "assistant", "content": example["response"]})
        return {"messages": messages}

    # instruction/output format
    if "instruction" in example and "output" in example:
        user_content = example["instruction"]
        if example.get("input"):
            user_content += f"\n\n{example['input']}"
        return {
            "messages": [
                {"role": "user", "content": user_content},
                {"role": "assistant", "content": example["output"]},
            ]
        }

    # text format (try to split)
    if "text" in example:
        text = example["text"]
        # Try common separators
        for sep in [
            "### Response:",
            "### Assistant:",
            "<|assistant|>",
            "\n\nAssistant:",
        ]:
            if sep in text:
                parts = text.split(sep, 1)
                if len(parts) == 2:
                    return {
                        "messages": [
                            {"role": "user", "content": parts[0].strip()},
                            {"role": "assistant", "content": parts[1].strip()},
                        ]
                    }

    return None


def validate_example(example: Dict) -> bool:
    """Validate that an example is usable for training."""

    if "messages" not in example:
        return False

    messages = example["messages"]
    if not isinstance(messages, list) or len(messages) < 2:
        return False

    # Check for minimum content
    for msg in messages:
        if not msg.get("content") or len(msg["content"].strip()) < 10:
            return False

    # Check for user and assistant roles
    roles = [m.get("role") for m in messages]
    if "user" not in roles or "assistant" not in roles:
        return False

    return True


def load_domain_knowledge(data_dir: Path, target_count: int) -> List[Dict]:
    """
    Load domain knowledge from training/data/*.jsonl files.

    Loads all available domain files, normalizes to ChatML format,
    and samples proportionally to reach target_count.
    """
    print(f"\n  Loading domain knowledge from: {data_dir}")

    all_domain_examples = []
    file_stats = {}

    for filename in DOMAIN_KNOWLEDGE_FILES:
        filepath = data_dir / filename
        if not filepath.exists():
            print(f"    ⚠ Missing: {filename}")
            continue

        raw_data = load_jsonl(filepath)
        if not raw_data:
            continue

        # Normalize each example
        normalized = []
        for example in raw_data:
            norm = normalize_to_chatml(example)
            if norm and validate_example(norm):
                norm["_source"] = filename.replace(".jsonl", "")
                normalized.append(norm)

        if normalized:
            # Apply weight multiplier if specified
            weight = FILE_WEIGHTS.get(filename, 1)
            if weight > 1:
                # Duplicate examples based on weight
                weighted_examples = normalized * weight
                all_domain_examples.extend(weighted_examples)
                file_stats[filename] = len(weighted_examples)
                print(
                    f"    ✓ {filename}: {len(normalized):,} examples × {weight} = {len(weighted_examples):,}"
                )
            else:
                all_domain_examples.extend(normalized)
                file_stats[filename] = len(normalized)
                print(f"    ✓ {filename}: {len(normalized):,} examples")

    print(f"\n  Domain knowledge total: {len(all_domain_examples):,} examples")

    # Sample to target if we have more than needed
    if len(all_domain_examples) > target_count:
        random.shuffle(all_domain_examples)
        all_domain_examples = all_domain_examples[:target_count]
        print(f"  Sampled to: {target_count:,} examples")
    elif len(all_domain_examples) < target_count:
        print(
            f"  ⚠ Only {len(all_domain_examples):,} available (wanted {target_count:,})"
        )
        # Optionally upsample if significantly under target
        if len(all_domain_examples) < target_count * 0.5:
            print(f"  ⚠ Upsampling domain knowledge to reach target...")
            multiplier = int(target_count / len(all_domain_examples)) + 1
            all_domain_examples = (all_domain_examples * multiplier)[:target_count]

    return all_domain_examples


def load_and_normalize(path: Path, target_count: int) -> List[Dict]:
    """Load dataset, normalize format, and sample to target count."""

    print(f"  Loading: {path}")
    raw_data = load_jsonl(path)

    if not raw_data:
        return []

    print(f"    Raw: {len(raw_data)} examples")

    # Normalize
    normalized = []
    for example in raw_data:
        norm = normalize_to_chatml(example)
        if norm and validate_example(norm):
            normalized.append(norm)

    print(f"    Normalized: {len(normalized)} examples")

    # Sample
    if len(normalized) > target_count:
        random.shuffle(normalized)
        normalized = normalized[:target_count]
        print(f"    Sampled: {target_count} examples")
    elif len(normalized) < target_count:
        print(f"    ⚠ Only {len(normalized)} available (wanted {target_count})")

    return normalized


def prepare_mixed_dataset(
    output_path: Path,
    total_target: int = 300_000,
    eval_ratio: float = 0.01,
    seed: int = 42,
):
    """Prepare the mixed dataset according to DATASET_MIX ratios."""

    random.seed(seed)

    base_dir = Path(__file__).parent
    data_dir = base_dir.parent / "data"  # training/data/
    all_examples = []
    stats = defaultdict(int)

    print("\n" + "=" * 60)
    print(f"Preparing Mixed Dataset v1 (v2.0.0)")
    print(f"Target: {total_target:,} examples")
    print("=" * 60)
    print("\nDataset composition:")
    print("  - 50% Conversational (WildChat + UltraChat)")
    print("  - 50% Domain Knowledge (local .jsonl files)")

    for category, config in DATASET_MIX.items():
        category_target = int(total_target * config["ratio"])
        category_examples = []

        print(f"\n[{category}] Target: {category_target:,}")

        # Special handling for domain_knowledge - load from training/data/
        if category == "domain_knowledge":
            domain_examples = load_domain_knowledge(data_dir, category_target)
            for ex in domain_examples:
                ex["_category"] = category
            category_examples.extend(domain_examples)
            stats["domain_knowledge"] = len(domain_examples)
        else:
            # Standard HuggingFace dataset loading
            for source_name, source_config in config["sources"].items():
                source_path = base_dir / source_config["path"]
                source_target = int(category_target * source_config["ratio"])

                examples = load_and_normalize(source_path, source_target)

                # Tag examples with source
                for ex in examples:
                    ex["_source"] = source_name
                    ex["_category"] = category

                category_examples.extend(examples)
                stats[source_name] = len(examples)

        all_examples.extend(category_examples)
        stats[f"_{category}_total"] = len(category_examples)

    # Shuffle all examples
    random.shuffle(all_examples)

    # Split train/eval
    eval_count = int(len(all_examples) * eval_ratio)
    train_examples = all_examples[eval_count:]
    eval_examples = all_examples[:eval_count]

    print("\n" + "=" * 60)
    print("Dataset Statistics")
    print("=" * 60)

    for source, count in sorted(stats.items()):
        if not source.startswith("_"):
            print(f"  {source}: {count:,}")

    print("-" * 40)
    print(f"  Total: {len(all_examples):,}")
    print(f"  Train: {len(train_examples):,}")
    print(f"  Eval:  {len(eval_examples):,}")

    # Save datasets
    output_path.parent.mkdir(parents=True, exist_ok=True)

    train_path = output_path.with_suffix(".train.jsonl")
    eval_path = output_path.with_suffix(".eval.jsonl")

    with open(train_path, "w", encoding="utf-8") as f:
        for ex in train_examples:
            # Remove internal tags before saving
            clean_ex = {k: v for k, v in ex.items() if not k.startswith("_")}
            f.write(json.dumps(clean_ex) + "\n")

    with open(eval_path, "w", encoding="utf-8") as f:
        for ex in eval_examples:
            clean_ex = {k: v for k, v in ex.items() if not k.startswith("_")}
            f.write(json.dumps(clean_ex) + "\n")

    # Save stats
    stats_path = output_path.with_suffix(".stats.json")
    with open(stats_path, "w") as f:
        json.dump(
            {
                "total": len(all_examples),
                "train": len(train_examples),
                "eval": len(eval_examples),
                "sources": dict(stats),
                "mix": {cat: cfg["ratio"] for cat, cfg in DATASET_MIX.items()},
            },
            f,
            indent=2,
        )

    print("\n" + "=" * 60)
    print("Output Files")
    print("=" * 60)
    print(f"  Train: {train_path}")
    print(f"  Eval:  {eval_path}")
    print(f"  Stats: {stats_path}")

    return train_path, eval_path


def main():
    parser = argparse.ArgumentParser(description="Prepare Mixed Dataset v1")
    parser.add_argument(
        "--output", type=str, default="processed/mixed_v1", help="Output path prefix"
    )
    parser.add_argument(
        "--total", type=int, default=300_000, help="Target total examples"
    )
    parser.add_argument(
        "--eval-ratio",
        type=float,
        default=0.01,
        help="Ratio of examples for evaluation",
    )
    parser.add_argument(
        "--seed", type=int, default=42, help="Random seed for reproducibility"
    )

    args = parser.parse_args()

    output_path = Path(args.output)
    prepare_mixed_dataset(
        output_path=output_path,
        total_target=args.total,
        eval_ratio=args.eval_ratio,
        seed=args.seed,
    )


if __name__ == "__main__":
    main()
