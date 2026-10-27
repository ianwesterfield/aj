<#
.SYNOPSIS
    AJ Deployment Script - Orchestrates all deployment tasks

.DESCRIPTION
    Central deployment script for the AJ infrastructure.
    Handles stack deployment, filter updates, base image builds, and training uploads.

.PARAMETER Action
    The deployment action to perform:
    - stack       : Deploy/restart Docker Compose stack
    - filter      : Deploy AJ filter to Open-WebUI
    - action      : Deploy training capture action to Open-WebUI
    - webui       : Deploy both filter and action to Open-WebUI
    - base-images : Build and push base Docker images
    - training    : Upload training files to GPU server
    - all         : Run all local deployments (stack + filter + action)

.PARAMETER Services
    For 'stack' action: specific services to restart (default: all)
    For 'base-images' action: which base images to build

.PARAMETER Force
    Force rebuild/redeploy even if no changes detected

.EXAMPLE
    # Deploy full stack (always rebuilds)
    .\deploy.ps1 -Action stack

.EXAMPLE
    # Restart specific services (rebuilds them)
    .\deploy.ps1 -Action stack -Services orchestrator_api,memory_api

.EXAMPLE
    # Deploy filter only
    .\deploy.ps1 -Action filter

.EXAMPLE
    # Deploy training capture action only
    .\deploy.ps1 -Action action

.EXAMPLE
    # Deploy both filter and action
    .\deploy.ps1 -Action webui

.EXAMPLE
    # Build and push base images
    .\deploy.ps1 -Action base-images

.EXAMPLE
    # Upload training to GPU server
    .\deploy.ps1 -Action training -Server root@gpu.runpod.io
#>

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("stack", "filter", "action", "webui", "base-images", "training", "all")]
    [string]$Action,

    [Parameter(Mandatory = $false)]
    [string[]]$Services,

    [Parameter(Mandatory = $false)]
    [switch]$Build,

    [Parameter(Mandatory = $false)]
    [switch]$Force,

    [Parameter(Mandatory = $false)]
    [string]$Server,

    [Parameter(Mandatory = $false)]
    [string]$Registry = "ianwesterfield"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Split-Path -Parent $ScriptDir

# Colors
function Write-Header($text) {
    Write-Host ""
    Write-Host "╔════════════════════════════════════════════╗" -ForegroundColor Cyan
    Write-Host ("║  $text".PadRight(45) + "║") -ForegroundColor Cyan
    Write-Host "╚════════════════════════════════════════════╝" -ForegroundColor Cyan
    Write-Host ""
}

function Write-Success($text) {
    Write-Host "✅ $text" -ForegroundColor Green
}

function Write-Info($text) {
    Write-Host "ℹ️  $text" -ForegroundColor Yellow
}

function Write-Step($text) {
    Write-Host "▶ $text" -ForegroundColor White
}

# ============================================================
# Stack Deployment
# ============================================================
function Deploy-Stack {
    param(
        [string[]]$Services,
        [switch]$Build
    )
    
    Write-Header "Deploying Docker Stack"
    
    Push-Location $ProjectRoot
    try {
        # Always rebuild to pick up latest code changes
        if ($Services -and $Services.Count -gt 0) {
            $serviceList = $Services -join " "
            Write-Step "Rebuilding and restarting: $serviceList"
            wsl docker compose up -d --build $Services
        }
        else {
            Write-Step "Rebuilding and starting all services..."
            wsl docker compose up -d --build
        }
        
        if ($LASTEXITCODE -eq 0) {
            Write-Success "Stack deployed successfully"
            Write-Host ""
            Write-Step "Service status:"
            wsl docker compose ps
        }
        else {
            throw "Docker Compose failed"
        }
    }
    finally {
        Pop-Location
    }
}

# ============================================================
# Filter Deployment
# ============================================================
function Deploy-Filter {
    param([switch]$FilterOnly, [switch]$ActionOnly)
    
    Write-Header "Deploying to Open-WebUI"
    
    $filterScript = Join-Path $ScriptDir "deploy-open-webui.py"
    
    if (-not (Test-Path $filterScript)) {
        throw "Filter deployment script not found: $filterScript"
    }
    
    # Build arguments
    $pyArgs = @()
    if ($FilterOnly) {
        $pyArgs += "--filter"
        Write-Step "Deploying AJ filter only..."
    }
    elseif ($ActionOnly) {
        $pyArgs += "--action"
        Write-Step "Deploying training capture action only..."
    }
    else {
        Write-Step "Deploying filter and action..."
    }
    
    # Try Python 3 first, fall back to python
    $python = Get-Command python3 -ErrorAction SilentlyContinue
    if (-not $python) {
        $python = Get-Command python -ErrorAction SilentlyContinue
    }
    
    if ($python) {
        & $python.Source $filterScript @pyArgs
    }
    else {
        # Fall back to WSL
        Write-Info "Using for Python..."
        $wslPath = $filterScript.Replace("\", "/").Replace("C:", "/mnt/c").Trim("\r")
        Write-Info "  WSL Path: $wslPath"
        python3 $wslPath @pyArgs
    }
    
    if ($LASTEXITCODE -eq 0) {
        Write-Success "Deployment complete"
    }
    else {
        throw "Deployment failed"
    }
}

# ============================================================
# Base Images
# ============================================================
function Deploy-BaseImages {
    param(
        [string[]]$Services,
        [switch]$Force
    )
    
    Write-Header "Building Base Images"
    
    $baseScript = Join-Path $ScriptDir "build-base-images.ps1"
    
    $params = @{
        Registry = $Registry
        Push     = $true
    }
    
    if ($Services -and $Services.Count -gt 0) {
        $params.Services = $Services
    }
    
    if ($Force) {
        $params.Force = $true
    }
    
    & $baseScript @params
}

# ============================================================
# Training Upload
# ============================================================
function Deploy-Training {
    param([string]$Server)
    
    Write-Header "Uploading Training Files"
    
    if (-not $Server) {
        Write-Host "ERROR: -Server parameter required for training upload" -ForegroundColor Red
        Write-Host ""
        Write-Host "Usage:" -ForegroundColor Yellow
        Write-Host "  .\deploy.ps1 -Action training -Server root@gpu.runpod.io" -ForegroundColor White
        throw "Missing server parameter"
    }
    
    $trainingScript = Join-Path $ProjectRoot "training" "scripts" "upload-training.ps1"
    
    if (-not (Test-Path $trainingScript)) {
        # Fall back to old location
        $trainingScript = Join-Path $ScriptDir "upload-training-minimal.ps1"
    }
    
    if (-not (Test-Path $trainingScript)) {
        throw "Training upload script not found"
    }
    
    & $trainingScript -Server $Server
}

# ============================================================
# Main
# ============================================================

Write-Host ""
Write-Host "AJ Deployment" -ForegroundColor Cyan
Write-Host "Action: $Action" -ForegroundColor White
Write-Host ""

$startTime = Get-Date

switch ($Action) {
    "stack" {
        Deploy-Stack -Services $Services -Build:$Build
    }
    "filter" {
        Deploy-Filter -FilterOnly
    }
    "action" {
        Deploy-Filter -ActionOnly
    }
    "webui" {
        Deploy-Filter
    }
    "base-images" {
        Deploy-BaseImages -Services $Services -Force:$Force
    }
    "training" {
        Deploy-Training -Server $Server
    }
    "all" {
        Deploy-Stack -Build:$Build
        Deploy-Filter
        Write-Success "All local deployments complete"
    }
}

$elapsed = (Get-Date) - $startTime
Write-Host ""
Write-Host "Completed in $([math]::Round($elapsed.TotalSeconds, 1)) seconds" -ForegroundColor Green
Write-Host ""
