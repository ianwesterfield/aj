<#
.SYNOPSIS
    Prepare a Windows client for FunnelCloud Agent deployment.

.DESCRIPTION
    Run this script on client machines BEFORE running Deploy-FunnelCloudAgent.ps1.
    It configures PowerShell Remoting trust settings to allow connection to the build server.

.PARAMETER BuildServer
    The hostname or IP of the build server to trust.
    Default: ians-r16

.EXAMPLE
    .\Setup-FunnelCloudClient.ps1 -BuildServer "ians-r16"

.NOTES
    Must be run as Administrator.
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory = $false)]
  [string]$BuildServer = "ians-r16"
)

$ErrorActionPreference = "Stop"

# Check admin
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
  Write-Host "ERROR: Run this script as Administrator" -ForegroundColor Red
  exit 1
}

Write-Host ""
Write-Host "FunnelCloud Client Setup" -ForegroundColor Cyan
Write-Host "========================"
Write-Host ""

# Add build server to trusted hosts
Write-Host "Adding $BuildServer to TrustedHosts..." -ForegroundColor Yellow
$currentHosts = (Get-Item WSMan:\localhost\Client\TrustedHosts).Value

if ($currentHosts -match [regex]::Escape($BuildServer)) {
  Write-Host "  Already trusted: $BuildServer" -ForegroundColor Green
}
else {
  if ($currentHosts) {
    $newHosts = "$currentHosts,$BuildServer"
  }
  else {
    $newHosts = $BuildServer
  }
  Set-Item WSMan:\localhost\Client\TrustedHosts -Value $newHosts -Force
  Write-Host "  Added to TrustedHosts: $BuildServer" -ForegroundColor Green
}

# Ensure WinRM is running
Write-Host ""
Write-Host "Checking WinRM service..." -ForegroundColor Yellow
$winrm = Get-Service WinRM
if ($winrm.Status -ne "Running") {
  Start-Service WinRM
  Write-Host "  Started WinRM service" -ForegroundColor Green
}
else {
  Write-Host "  WinRM is running" -ForegroundColor Green
}

# Test connection
Write-Host ""
Write-Host "Testing connection to $BuildServer..." -ForegroundColor Yellow
try {
  $result = Test-WSMan -ComputerName $BuildServer -ErrorAction Stop
  Write-Host "  Connection successful!" -ForegroundColor Green
}
catch {
  Write-Host "  Connection failed: $_" -ForegroundColor Red
  Write-Host ""
  Write-Host "  Make sure:" -ForegroundColor Gray
  Write-Host "    1. $BuildServer is reachable on the network"
  Write-Host "    2. PowerShell Remoting is enabled on $BuildServer"
  Write-Host "    3. Firewall allows WinRM (TCP 5985)"
}

Write-Host ""
Write-Host "Setup complete! You can now run Deploy-FunnelCloudAgent.ps1" -ForegroundColor Green
Write-Host ""
