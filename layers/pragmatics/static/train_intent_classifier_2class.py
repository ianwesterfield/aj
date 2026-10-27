# pragmatics/train_intent_classifier_2class.py
"""
Train a 2-class intent classifier for AJ:
  - Class 0: CASUAL - General chat, greetings, questions about info, memory recall
  - Class 1: TASK - Workspace/code/execution requests that need agent action

This simplifies the 4-class model. Memory save/recall happens automatically
as part of normal processing, not as a separate intent.

Run with: python train_intent_classifier_2class.py
The model will be saved to ./intent_classifier_2class/ and automatically
copied to ./intent_classifier/ to become the active model.
"""
import torch
import random
import os
import sys
import shutil
from pathlib import Path
from sklearn.model_selection import train_test_split
from sklearn.metrics import accuracy_score, classification_report, confusion_matrix
from transformers import AutoTokenizer, AutoModelForSequenceClassification, Trainer, TrainingArguments
import numpy as np

# Add parent directory to path for imports
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '../..'))
from shared.logging_utils import format_training_message

# -----------------------------------------------------------------
# GPU Detection
# -----------------------------------------------------------------
device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
print(f"Using device: {device}")
if torch.cuda.is_available():
    print(f"GPU: {torch.cuda.get_device_name(0)}")
    print(f"Memory: {torch.cuda.get_device_properties(0).total_memory / 1024**3:.1f} GB")

# -----------------------------------------------------------------
# Intent Labels (2-class simplified)
# -----------------------------------------------------------------
INTENT_LABELS = {
    0: "casual",
    1: "task",
}

LABEL_TO_ID = {v: k for k, v in INTENT_LABELS.items()}

# -----------------------------------------------------------------
# Load Training Data
# -----------------------------------------------------------------
print("Loading training examples...")

from examples import SAVE_EXAMPLES, RECALL_EXAMPLES, CASUAL_EXAMPLES, TASK_EXAMPLES

print(f"  Original Casual: {len(CASUAL_EXAMPLES)} examples")
print(f"  Original Save: {len(SAVE_EXAMPLES)} examples")
print(f"  Original Recall: {len(RECALL_EXAMPLES)} examples")  
print(f"  Original Task: {len(TASK_EXAMPLES)} examples")

# Map to 2-class:
# - casual + recall → casual (conversation, asking questions about remembered info)
# - task + save → task (anything that needs action; saving happens during tasks too)
texts = []
labels = []

# CASUAL: Greetings, chitchat, simple questions
for ex in CASUAL_EXAMPLES:
    texts.append(ex)
    labels.append(LABEL_TO_ID["casual"])

# RECALL → CASUAL: Asking about remembered info is just conversation
# Note: Some recall examples that look like tasks should be manually reviewed
for ex in RECALL_EXAMPLES:
    texts.append(ex)
    labels.append(LABEL_TO_ID["casual"])

# TASK: Anything requiring agent execution
for ex in TASK_EXAMPLES:
    texts.append(ex)
    labels.append(LABEL_TO_ID["task"])

# SAVE → TASK: Explicit "remember this" requests need agent action to store
for ex in SAVE_EXAMPLES:
    texts.append(ex)
    labels.append(LABEL_TO_ID["task"])

print(f"\nMapped to 2-class:")
print(f"  casual (casual + recall): {labels.count(LABEL_TO_ID['casual'])}")
print(f"  task (task + save): {labels.count(LABEL_TO_ID['task'])}")
print(f"  Total: {len(texts)}")

# -----------------------------------------------------------------
# Balance Classes (oversample minority class)
# -----------------------------------------------------------------
print("\nBalancing classes...")

class_counts = {}
for label in INTENT_LABELS.keys():
    class_counts[label] = labels.count(label)
    print(f"  {INTENT_LABELS[label]}: {class_counts[label]}")

max_count = max(class_counts.values())

# Oversample minority class
balanced_texts = []
balanced_labels = []

for label_id, label_name in INTENT_LABELS.items():
    class_examples = [(t, l) for t, l in zip(texts, labels) if l == label_id]
    count = len(class_examples)
    
    # Add original examples
    for t, l in class_examples:
        balanced_texts.append(t)
        balanced_labels.append(l)
    
    # Oversample to match max class
    if count < max_count:
        oversample_count = max_count - count
        oversampled = random.choices(class_examples, k=oversample_count)
        for t, l in oversampled:
            balanced_texts.append(t)
            balanced_labels.append(l)

# Shuffle
combined = list(zip(balanced_texts, balanced_labels))
random.seed(42)
random.shuffle(combined)
texts, labels = zip(*combined)
texts = list(texts)
labels = list(labels)

print(f"\nAfter balancing: {len(texts)} total examples")
for label_id, label_name in INTENT_LABELS.items():
    print(f"  {label_name}: {labels.count(label_id)}")

# -----------------------------------------------------------------
# Tokenizer & Train/Val Split
# -----------------------------------------------------------------
print("\nTokenizing...")
tokenizer = AutoTokenizer.from_pretrained("distilbert-base-uncased")

train_texts, val_texts, train_labels, val_labels = train_test_split(
    texts,
    labels,
    test_size=0.2,
    stratify=labels,
    random_state=42
)

print(f"Train: {len(train_texts)}, Val: {len(val_texts)}")

train_encodings = tokenizer(
    train_texts,
    truncation=True,
    padding=True,
    max_length=128
)

val_encodings = tokenizer(
    val_texts,
    truncation=True,
    padding=True,
    max_length=128
)

# -----------------------------------------------------------------
# Dataset Class
# -----------------------------------------------------------------
class IntentDataset(torch.utils.data.Dataset):
    def __init__(self, encodings, labels):
        self.encodings = encodings
        self.labels = torch.tensor(labels, dtype=torch.long)

    def __len__(self):
        return len(self.labels)

    def __getitem__(self, idx):
        item = {k: torch.tensor(v[idx]) for k, v in self.encodings.items()}
        item["labels"] = self.labels[idx]
        return item

train_dataset = IntentDataset(
    {"input_ids": train_encodings["input_ids"], 
     "attention_mask": train_encodings["attention_mask"]}, 
    train_labels
)
val_dataset = IntentDataset(
    {"input_ids": val_encodings["input_ids"], 
     "attention_mask": val_encodings["attention_mask"]}, 
    val_labels
)

# -----------------------------------------------------------------
# Model Initialization
# -----------------------------------------------------------------
print("\nInitializing model...")

id2label = INTENT_LABELS
label2id = LABEL_TO_ID

model = AutoModelForSequenceClassification.from_pretrained(
    "distilbert-base-uncased",
    num_labels=2,
    id2label=id2label,
    label2id=label2id,
).to(device)

print(f"Model loaded with {model.num_labels} labels: {list(id2label.values())}")

# -----------------------------------------------------------------
# Training Configuration
# -----------------------------------------------------------------
OUTPUT_DIR = "./intent_classifier_2class"
FINAL_DIR = "./intent_classifier"

training_args = TrainingArguments(
    output_dir=OUTPUT_DIR,
    eval_strategy="epoch",
    save_strategy="epoch",
    learning_rate=2e-5,
    per_device_train_batch_size=32,
    per_device_eval_batch_size=32,
    num_train_epochs=5,
    weight_decay=0.01,
    load_best_model_at_end=True,
    metric_for_best_model="accuracy",
    greater_is_better=True,
    logging_steps=20,
    warmup_steps=100,
    seed=42,
    report_to=[],  # Disable wandb/other logging
)

def compute_metrics(eval_pred):
    logits, labels = eval_pred
    predictions = np.argmax(logits, axis=-1)
    acc = accuracy_score(labels, predictions)
    return {"accuracy": acc}

trainer = Trainer(
    model=model,
    args=training_args,
    train_dataset=train_dataset,
    eval_dataset=val_dataset,
    compute_metrics=compute_metrics,
)

# -----------------------------------------------------------------
# Training
# -----------------------------------------------------------------
print("\n" + "="*60)
print("STARTING 2-CLASS INTENT CLASSIFIER TRAINING")
print("="*60)

trainer.train()

# -----------------------------------------------------------------
# Evaluation
# -----------------------------------------------------------------
print("\n" + "="*60)
print("EVALUATION")
print("="*60)

# Get predictions on validation set
predictions = trainer.predict(val_dataset)
pred_labels = np.argmax(predictions.predictions, axis=-1)

print("\nClassification Report:")
print(classification_report(val_labels, pred_labels, target_names=["casual", "task"]))

print("\nConfusion Matrix:")
cm = confusion_matrix(val_labels, pred_labels)
print(cm)
print("\n  Rows: Actual, Cols: Predicted")
print("  [casual, task]")

# -----------------------------------------------------------------
# Save Model
# -----------------------------------------------------------------
print(f"\nSaving model to {OUTPUT_DIR}...")
trainer.save_model(OUTPUT_DIR)
tokenizer.save_pretrained(OUTPUT_DIR)

# Copy to active model directory
print(f"\nCopying to active model directory {FINAL_DIR}...")
if os.path.exists(FINAL_DIR):
    shutil.rmtree(FINAL_DIR)
shutil.copytree(OUTPUT_DIR, FINAL_DIR)

print("\n" + "="*60)
print("TRAINING COMPLETE!")
print("="*60)
print(f"Model saved to: {OUTPUT_DIR}")
print(f"Active model updated: {FINAL_DIR}")
print("\nRestart pragmatics_api container to use new model:")
print("  docker compose restart pragmatics_api")

# -----------------------------------------------------------------
# Test Examples
# -----------------------------------------------------------------
print("\n" + "="*60)
print("TEST PREDICTIONS")
print("="*60)

test_examples = [
    "Hello, how are you today?",
    "What's the weather like?",
    "Check the disk space on domain01",
    "Run whoami on all agents",
    "What AD groups does my account belong to?",
    "List the running services on r730xd",
    "Tell me a joke",
    "Remember that the API key is stored in /secrets",
    "What was that API key location?",
    "Get the DNS configuration from domain02",
    "Who are the members of Domain Admins?",
    "Thanks for your help!",
    "What's your name?",
    "Execute Get-Process on ians-r16",
]

# Load model for inference
model.eval()
for example in test_examples:
    inputs = tokenizer(example, return_tensors="pt", truncation=True, max_length=128).to(device)
    with torch.no_grad():
        outputs = model(**inputs)
        probs = torch.softmax(outputs.logits, dim=-1)[0]
        pred_id = torch.argmax(probs).item()
        pred_label = INTENT_LABELS[pred_id]
        confidence = probs[pred_id].item()
    
    marker = "✓" if pred_label == "task" and any(kw in example.lower() for kw in ["check", "run", "list", "get", "execute", "ad groups", "domain"]) else ""
    marker = "✓" if pred_label == "casual" and any(kw in example.lower() for kw in ["hello", "thanks", "joke", "name", "weather"]) else marker
    print(f"  [{pred_label:6s}] ({confidence:.2%}) {marker} {example[:60]}")
