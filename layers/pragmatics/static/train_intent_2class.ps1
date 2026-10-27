# Train the 2-class intent classifier
# Run from the pragmatics/static directory

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  AJ 2-Class Intent Classifier Training" -ForegroundColor Cyan
Write-Host "  (task vs casual)" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check if we're in the right directory
if (-not (Test-Path "train_intent_classifier_2class.py")) {
  Write-Host "ERROR: Run this from layers/pragmatics/static/" -ForegroundColor Red
  Write-Host "  cd layers/pragmatics/static" -ForegroundColor Yellow
  Write-Host "  .\train_intent_2class.ps1" -ForegroundColor Yellow
  exit 1
}

# Check Python
Write-Host "Checking Python environment..." -ForegroundColor Yellow
python --version
if ($LASTEXITCODE -ne 0) {
  Write-Host "ERROR: Python not found" -ForegroundColor Red
  exit 1
}

# Check for required packages
Write-Host "Checking dependencies..." -ForegroundColor Yellow
python -c "import torch; import transformers; import sklearn" 2>$null
if ($LASTEXITCODE -ne 0) {
  Write-Host "Installing dependencies..." -ForegroundColor Yellow
  pip install torch transformers scikit-learn numpy
}

# Check GPU
Write-Host ""
python -c "import torch; print(f'CUDA available: {torch.cuda.is_available()}'); print(f'GPU: {torch.cuda.get_device_name(0) if torch.cuda.is_available() else \"None\"}') if torch.cuda.is_available() else None"

# Run training
Write-Host ""
Write-Host "Starting 2-class training (task vs casual)..." -ForegroundColor Green
Write-Host ""
Write-Host "Class mapping:" -ForegroundColor Yellow
Write-Host "  - casual: greetings, chitchat, questions" -ForegroundColor White
Write-Host "  - task: anything requiring agent execution" -ForegroundColor White
Write-Host ""

python train_intent_classifier_2class.py

if ($LASTEXITCODE -eq 0) {
  Write-Host ""
  Write-Host "========================================" -ForegroundColor Green
  Write-Host "  Training Complete!" -ForegroundColor Green
  Write-Host "========================================" -ForegroundColor Green
  Write-Host ""
  Write-Host "Model saved to: ./intent_classifier (active)" -ForegroundColor Cyan
  Write-Host "Backup at: ./intent_classifier_2class" -ForegroundColor Cyan
  Write-Host ""
  Write-Host "Next steps:" -ForegroundColor Yellow
  Write-Host "  1. Rebuild pragmatics container:" -ForegroundColor White
  Write-Host "     docker compose build pragmatics_api" -ForegroundColor Gray
  Write-Host ""
  Write-Host "  2. Restart pragmatics service:" -ForegroundColor White
  Write-Host "     docker compose up -d pragmatics_api" -ForegroundColor Gray
  Write-Host ""
  Write-Host "  3. Test classification:" -ForegroundColor White
  Write-Host "     curl -X POST http://localhost:8001/api/pragmatics/classify -H 'Content-Type: application/json' -d '{\"text\": \"What AD groups am I in?\"}'" -ForegroundColor Gray
}
else {
  Write-Host ""
  Write-Host "========================================" -ForegroundColor Red
  Write-Host "  Training Failed!" -ForegroundColor Red
  Write-Host "========================================" -ForegroundColor Red
}
