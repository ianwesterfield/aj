<#
.SYNOPSIS
    Upload training files to GPU server (scripts + domain knowledge data)

.DESCRIPTION
    Uploads scripts, configs, and domain knowledge (~50MB).
    Large HuggingFace datasets (WildChat, UltraChat, Skein) are downloaded on the server.

.PARAMETER Server
    SSH connection string (e.g., "root@gpu-server.example.com" or "user@192.168.1.100")

.PARAMETER RemotePath
    Remote directory path (default: /workspace/training)

.PARAMETER KeyFile
    Optional SSH key file path

.PARAMETER Port
    SSH port (default: 22)

.EXAMPLE
    .\upload-training.ps1 -Server root@gpu.runpod.io -RemotePath /workspace/training

.EXAMPLE
    .\upload-training.ps1 -Server user@vast-ai-host -KeyFile ~/.ssh/vast_key

.EXAMPLE
    # From project root via deploy.ps1:
    .\scripts\deploy.ps1 -Action training -Server root@gpu.runpod.io
#>

param(
  [Parameter(Mandatory = $true)]
  [string]$Server,
    
  [Parameter(Mandatory = $false)]
  [string]$RemotePath = "../training",
    
  [Parameter(Mandatory = $false)]
  [string]$KeyFile,

  [Parameter(Mandatory = $false)]
  [int]$Port = 22
)

$ErrorActionPreference = "Stop"

# Paths - script is now in training/scripts/
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$TrainingDir = Split-Path -Parent $ScriptDir
$TempDir = Join-Path $env:TEMP "aj-training-upload"

Write-Host ""
Write-Host "╔════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  AJ Training Upload                        ║" -ForegroundColor Cyan
Write-Host "╚════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "Server: $Server" -ForegroundColor White
Write-Host "Remote: $RemotePath" -ForegroundColor White
Write-Host ""

# Clean up temp directory
if (Test-Path $TempDir) {
  Remove-Item -Recurse -Force $TempDir
}
New-Item -ItemType Directory -Path $TempDir | Out-Null

Write-Host "▶ Collecting training files..." -ForegroundColor Yellow

# Files/folders to include
$includes = @(
  # Setup scripts
  "setup_and_train_v2.sh",
  "setup_digitalocean_h200.sh",
  "requirements.txt",
    
  # Documentation
  "DEPLOY_V2.md",
  "README.md",
  "pytest.ini",
    
  # Configs (LoRA, merge, etc.)
  "configs",
    
  # Training scripts (this folder)
  "scripts",
    
  # Domain knowledge JSONL files (~26K examples, ~15MB)
  "data",
    
  # Dataset processing scripts
  "datasets/*.py",
  "datasets/README.md",
    
  # Agentic training data generation
  "agentic/configs",
  "agentic/converters",
  "agentic/generators",
  "agentic/schemas",
  "agentic/tasks",
  "agentic/utils",
  "agentic/*.py",
  "agentic/*.md"
)

# Copy structure
foreach ($pattern in $includes) {
  $sourcePath = Join-Path $TrainingDir $pattern
  $destPath = Join-Path $TempDir $pattern
    
  if (Test-Path $sourcePath) {
    $destDir = Split-Path $destPath -Parent
    if (-not (Test-Path $destDir)) {
      New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }
        
    if ((Get-Item $sourcePath) -is [System.IO.DirectoryInfo]) {
      Copy-Item -Recurse -Path $sourcePath -Destination $destPath -Force
    }
    else {
      # Handle wildcards
      $files = Get-ChildItem -Path $sourcePath -ErrorAction SilentlyContinue
      foreach ($file in $files) {
        Copy-Item -Path $file.FullName -Destination $destDir -Force
      }
    }
  }
}

# Create datasets directory structure (empty, for downloads)
$datasetDirs = @("datasets/raw", "datasets/processed")
foreach ($dir in $datasetDirs) {
  $path = Join-Path $TempDir $dir
  if (-not (Test-Path $path)) {
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    # Add .gitkeep
    New-Item -ItemType File -Path (Join-Path $path ".gitkeep") -Force | Out-Null
  }
}

# Calculate size
$totalSize = (Get-ChildItem -Recurse -Path $TempDir -File | Measure-Object -Property Length -Sum).Sum
$sizeMB = [math]::Round($totalSize / 1MB, 2)

Write-Host "  Package size: $sizeMB MB" -ForegroundColor Green
Write-Host ""

# Build SSH/SCP options
$sshOpts = @("-p", $Port)
if ($KeyFile) {
  $sshOpts += "-i", $KeyFile
}

# Check for rsync (preferred) or fall back to scp
$useRsync = Get-Command rsync -ErrorAction SilentlyContinue

if ($useRsync) {
  Write-Host "▶ Uploading via rsync..." -ForegroundColor Yellow
    
  $rsyncOpts = @("-avz", "--progress")
  if ($KeyFile) {
    $rsyncOpts += "-e", "ssh -p $Port -i $KeyFile"
  }
  else {
    $rsyncOpts += "-e", "ssh -p $Port"
  }
    
  & rsync @rsyncOpts "$TempDir/" "${Server}:${RemotePath}/"
}
else {
  Write-Host "▶ Uploading via scp..." -ForegroundColor Yellow
  Write-Host "  (Install rsync for faster uploads with progress)" -ForegroundColor DarkGray
    
  # Create remote directory
  & ssh @sshOpts $Server "mkdir -p $RemotePath"
    
  # Upload
  $scpOpts = @("-P", $Port, "-r")
  if ($KeyFile) {
    $scpOpts += "-i", $KeyFile
  }
  & scp @scpOpts "$TempDir\*" "${Server}:${RemotePath}/"
}

if ($LASTEXITCODE -eq 0) {
  Write-Host ""
  Write-Host "╔════════════════════════════════════════════╗" -ForegroundColor Green
  Write-Host "║  Upload Complete!                          ║" -ForegroundColor Green
  Write-Host "╚════════════════════════════════════════════╝" -ForegroundColor Green
  Write-Host ""
  Write-Host "Next steps on the server:" -ForegroundColor Cyan
  Write-Host "  ssh -p $Port $Server" -ForegroundColor White
  Write-Host "  cd $RemotePath" -ForegroundColor White
  Write-Host "  chmod +x setup_and_train_v2.sh" -ForegroundColor White
  Write-Host "  ./setup_and_train_v2.sh" -ForegroundColor White
  Write-Host ""
}
else {
  Write-Host "Upload failed!" -ForegroundColor Red
  exit 1
}

# Cleanup
Remove-Item -Recurse -Force $TempDir -ErrorAction SilentlyContinue
