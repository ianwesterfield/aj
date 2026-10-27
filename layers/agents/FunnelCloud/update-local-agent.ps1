# Update Local FunnelCloud Agent
# Run this script as Administrator

$ErrorActionPreference = "Stop"

$publishPath = "C:\Code\aj.westerfield.cloud\layers\agents\FunnelCloud\publish"
$installPath = "C:\FunnelCloud\Agent"
$serviceName = "FunnelCloudAgent"

Write-Host "=== Updating Local FunnelCloud Agent ===" -ForegroundColor Cyan
Write-Host ""

# Check if running as admin
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
  Write-Host "ERROR: This script must be run as Administrator" -ForegroundColor Red
  Write-Host "Right-click PowerShell and select 'Run as Administrator'" -ForegroundColor Yellow
  exit 1
}

# Stop service
Write-Host "Stopping service..." -ForegroundColor Yellow
Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
Start-Sleep 3

# Verify service stopped
$svc = Get-Service $serviceName
if ($svc.Status -ne "Stopped") {
  Write-Host "ERROR: Could not stop service. Current status: $($svc.Status)" -ForegroundColor Red
  exit 1
}
Write-Host "  Service stopped" -ForegroundColor Green

# Copy files
Write-Host "Copying files from publish folder..." -ForegroundColor Yellow
Copy-Item "$publishPath\*" $installPath -Force -Recurse
Write-Host "  Files copied" -ForegroundColor Green

# Verify the new file
$newFile = Get-Item "$installPath\FunnelCloud.Agent.exe"
Write-Host "  Binary timestamp: $($newFile.LastWriteTime)" -ForegroundColor Gray

# Start service
Write-Host "Starting service..." -ForegroundColor Yellow
Start-Service $serviceName
Start-Sleep 3

# Verify service started
$svc = Get-Service $serviceName
if ($svc.Status -eq "Running") {
  Write-Host "  Service running" -ForegroundColor Green
}
else {
  Write-Host "ERROR: Service did not start. Status: $($svc.Status)" -ForegroundColor Red
  exit 1
}

# Test health endpoint
Write-Host ""
Write-Host "Testing endpoints..." -ForegroundColor Yellow
Start-Sleep 2

try {
  $health = Invoke-RestMethod "http://localhost:41421/health" -TimeoutSec 5
  Write-Host "  /health: OK ($($health.hostname))" -ForegroundColor Green
}
catch {
  Write-Host "  /health: FAILED - $_" -ForegroundColor Red
}

try {
  $peers = Invoke-RestMethod "http://localhost:41421/peers" -TimeoutSec 5
  Write-Host "  /peers: OK ($($peers.count) cached)" -ForegroundColor Green
}
catch {
  Write-Host "  /peers: FAILED - $_" -ForegroundColor Red
}

Write-Host ""
Write-Host "=== Update Complete ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "The local agent now has gossip support."
Write-Host "It will automatically share peer information with other agents every 30 seconds."
Write-Host ""
