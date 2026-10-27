# AJ Training Datasets

> **Versioning Scheme**: `AJ-{base_model_code}-{dataset_version}-{training_config}`

## Model Naming Convention

```
AJ-DSR1Q32B-v{major}.{minor}.{patch}-{suffix}
│   │        │  │     │       └── Optional: lora, merged, q4km, q8
│   │        │  │     └────────── Patch: bug fixes, hyperparameter tweaks
│   │        │  └──────────────── Minor: dataset mix adjustments
│   │        └─────────────────── Major: new base model or architecture change
│   └──────────────────────────── Base model code
└──────────────────────────────── Project name
```

### Base Model Codes

| Code       | Model                        |
| ---------- | ---------------------------- |
| `DSR1Q32B` | DeepSeek-R1-Distill-Qwen-32B |
| `DSR1Q14B` | DeepSeek-R1-Distill-Qwen-14B |
| `DSR1Q7B`  | DeepSeek-R1-Distill-Qwen-7B  |
| `Q3232B`   | Qwen 2.5 32B                 |
| `L3170B`   | Llama 3.1 70B                |

### Training Runs Registry

| Version       | Dataset Mix          | Steps | Status      | Notes                                |
| ------------- | -------------------- | ----- | ----------- | ------------------------------------ |
| `v2.0.0-lora` | Mixed-v1 (see below) | 5000  | 🟡 Training | Conversational + Plants + Apothecary |

---

## Dataset Configurations

### v2.0.0 - Mixed (Conversational + Domain Knowledge)

```yaml
name: mixed-v1
total_examples: ~300,000
composition:
  conversational: 50% # ~150K
  plant_farming: 15% # ~45K
  strategic: 10% # ~30K
  apothecary: 10% # ~30K (Western only)
  existing_aj: 15% # ~45K
```

#### Component Datasets

| Category           | Dataset                    | Source                                         | Rows    | License         |
| ------------------ | -------------------------- | ---------------------------------------------- | ------- | --------------- |
| **Conversational** | WildChat-1M (filtered)     | `allenai/WildChat-1M`                          | 100K    | ODC-BY          |
| **Conversational** | UltraChat 200K             | `HuggingFaceH4/ultrachat_200k`                 | 50K     | MIT             |
| **Plant/Farming**  | Agriculture QA English     | `KisanVaani/agriculture-qa-english-only`       | 22.6K   | Apache 2.0      |
| **Plant/Farming**  | Gardening LLM Multiturn    | `Reggie123/Gardening_LLM_Synthetic_Multiturn`  | 3.2K    | CC BY-NC-SA 4.0 |
| **Plant/Farming**  | StackExchange Gardening    | `UDACA/StackExchange-gardening`                | 42K     | CC BY-SA        |
| **Plant/Farming**  | Natural Farming Q&A        | `CopyleftCultivars/Natural-Farming-Real-QandA` | 1.1K    | -               |
| **Strategic**      | Skein Text Adventures      | `lara-martin/Skein`                            | 14.6K   | CC BY 4.0       |
| **Strategic**      | Text Adventure Games       | TBD                                            | ~15K    | Various         |
| **Apothecary**     | Western Herbalism (custom) | Public domain texts                            | ~10-15K | Public Domain   |
| **Existing**       | AJ Training Data           | `training/data/*.jsonl`                        | ~45K    | Proprietary     |

#### Western Apothecary Sources (Public Domain)

| Text                                 | Author/Year          | Source                   | Content                                  |
| ------------------------------------ | -------------------- | ------------------------ | ---------------------------------------- |
| **The Complete Herbal**              | Culpeper, 1653       | Project Gutenberg #49513 | 300+ herbs, Western herbalism foundation |
| **American Eclectic Materia Medica** | Scudder, 1883        | Internet Archive         | North American eclectic medicine         |
| **King's American Dispensatory**     | Felter & Lloyd, 1898 | Internet Archive         | Comprehensive NA materia medica          |
| **Herbal Simples**                   | Fernie               | Project Gutenberg #19352 | British/Western remedies                 |

---

## Directory Structure

```
training/
├── datasets/
│   ├── README.md                    # This file
│   ├── download_datasets.py         # HuggingFace dataset downloader
│   ├── prepare_mixed_v1.py          # Mix and prepare v2.0 dataset
│   ├── extract_apothecary.py        # Parse public domain herbal texts
│   ├── raw/                         # Downloaded raw datasets
│   │   ├── wildchat/
│   │   ├── ultrachat/
│   │   ├── agriculture/
│   │   ├── gardening/
│   │   └── apothecary/
│   └── processed/                   # Training-ready datasets
│       ├── mixed_v1_train.jsonl
│       └── mixed_v1_eval.jsonl
├── configs/
│   └── mixed_v1.yaml                # v2.0 config
├── scripts/
│   └── train_mixed.py               # v2.0 training script
└── data/                            # Existing AJ training data
    └── *.jsonl
```

---

## Usage

### 1. Download Datasets

```bash
cd training/datasets
python download_datasets.py --config mixed_v1
```

### 2. Prepare Western Apothecary Data

```bash
# Download public domain texts
python extract_apothecary.py --download

# Parse into Q&A pairs
python extract_apothecary.py --parse --output raw/apothecary/
```

### 3. Build Mixed Dataset

```bash
python prepare_mixed_v1.py \
    --wildchat-sample 100000 \
    --ultrachat-sample 50000 \
    --output processed/mixed_v1_train.jsonl
```

### 4. Train

```bash
# On GPU server
python scripts/train_mixed.py --config configs/mixed_v1.yaml
```

---

## Changelog

### v2.0.0-lora (In Progress)

- [x] Add conversational datasets (WildChat, UltraChat)
- [x] Add plant/farming knowledge (Agriculture QA, Gardening, StackExchange)
- [x] Add Western apothecary from public domain texts
- [x] Add strategic reasoning (text adventures)
- [x] Merge with existing AJ training data
- [ ] Training complete (~11 hours)
- [ ] Merge LoRA adapters
- [ ] Convert to GGUF quantization
