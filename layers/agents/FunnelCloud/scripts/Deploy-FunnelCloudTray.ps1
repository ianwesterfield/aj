<#
.SYNOPSIS
    Deploy FunnelCloud.Tray to the current user's startup.

.DESCRIPTION
    This script installs the FunnelCloud tray notification app for the current user:
    1. Publishes the tray app (or uses pre-built publish folder)
    2. Copies to %LOCALAPPDATA%\FunnelCloud\Tray
    3. Creates a startup shortcut so it launches at login
    4. Optionally starts the tray app immediately

.PARAMETER SkipBuild
    If specified, skips the publish step and expects files already in publish folder.

.PARAMETER NoStart
    If specified, does not start the tray app after installation.

.EXAMPLE
    .\Deploy-FunnelCloudTray.ps1

.EXAMPLE
    .\Deploy-FunnelCloudTray.ps1 -SkipBuild -NoStart

.NOTES
    Does not require admin rights - installs per-user.
    Requires the FunnelCloud Agent to be running to see events.
#>

[CmdletBinding()]
param(
  [switch]$SkipBuild,
  [switch]$NoStart
)

$ErrorActionPreference = "Stop"

function Write-Step { param($msg) Write-Host "`n[>] $msg" -ForegroundColor Cyan }
function Write-Ok { param($msg) Write-Host "    [OK] $msg" -ForegroundColor Green }
function Write-Warn { param($msg) Write-Host "    [!] $msg" -ForegroundColor Yellow }
function Write-Err { param($msg) Write-Host "    [X] $msg" -ForegroundColor Red }

# Banner
Write-Host ""
Write-Host "================================================================" -ForegroundColor Magenta
Write-Host "         FunnelCloud Tray App Deployment                       " -ForegroundColor Magenta
Write-Host "================================================================" -ForegroundColor Magenta
Write-Host ""

# Paths
$ScriptRoot = $PSScriptRoot
$TrayProject = Join-Path $ScriptRoot "..\FunnelCloud.Tray"
$PublishFolder = Join-Path $TrayProject "bin\Release\net8.0-windows\win-x64\publish"
$InstallPath = Join-Path $env:LOCALAPPDATA "FunnelCloud\Tray"
$ExeName = "FunnelCloud.Tray.exe"
$StartupFolder = [Environment]::GetFolderPath("Startup")
$ShortcutPath = Join-Path $StartupFolder "FunnelCloud Tray.lnk"

Write-Host "Configuration:" -ForegroundColor White
Write-Host "  Project:       $TrayProject"
Write-Host "  Install Path:  $InstallPath"
Write-Host "  Startup:       $StartupFolder"
Write-Host ""

# Step 1: Build/Publish (unless skipped)
if (-not $SkipBuild) {
  Write-Step "Publishing FunnelCloud.Tray..."
    
  Push-Location $TrayProject
  try {
    dotnet publish -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
      Write-Err "Publish failed"
      exit 1
    }
    Write-Ok "Published successfully"
  }
  finally {
    Pop-Location
  }
}

# Verify publish folder exists
if (-not (Test-Path (Join-Path $PublishFolder $ExeName))) {
  Write-Err "Publish folder not found or missing exe: $PublishFolder"
  Write-Host "  Run without -SkipBuild to publish first" -ForegroundColor Gray
  exit 1
}

# Step 2: Stop existing instance
Write-Step "Stopping existing instances..."
$existing = Get-Process -Name "FunnelCloud.Tray" -ErrorAction SilentlyContinue
if ($existing) {
  $existing | Stop-Process -Force
  Start-Sleep -Milliseconds 500
  Write-Ok "Stopped existing process"
}
else {
  Write-Ok "No existing process running"
}

# Step 3: Copy to install location
Write-Step "Installing to $InstallPath..."

if (Test-Path $InstallPath) {
  Remove-Item $InstallPath -Recurse -Force
}
New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null

# Copy all files from publish folder
Copy-Item -Path "$PublishFolder\*" -Destination $InstallPath -Recurse -Force
Write-Ok "Files copied"

# Step 4: Create startup shortcut
Write-Step "Creating startup shortcut..."

$WshShell = New-Object -ComObject WScript.Shell
$Shortcut = $WshShell.CreateShortcut($ShortcutPath)
$Shortcut.TargetPath = Join-Path $InstallPath $ExeName
$Shortcut.WorkingDirectory = $InstallPath
$Shortcut.Description = "FunnelCloud Agent Notification Tray"
$Shortcut.Save()

Write-Ok "Startup shortcut created: $ShortcutPath"

# Step 5: Start the app (unless skipped)
if (-not $NoStart) {
  Write-Step "Starting FunnelCloud.Tray..."
    
  $exePath = Join-Path $InstallPath $ExeName
  Start-Process -FilePath $exePath -WorkingDirectory $InstallPath
  Write-Ok "Tray app started - check your system tray!"
}

# Done
Write-Host ""
Write-Host "================================================================" -ForegroundColor Green
Write-Host "  Installation Complete!" -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
Write-Host ""
Write-Host "  The tray app will:"
Write-Host "  - Show in your system tray"
Write-Host "  - Display events from the FunnelCloud Agent"
Write-Host "  - Start automatically at login"
Write-Host ""
Write-Host "  To uninstall:"
Write-Host "  - Delete: $ShortcutPath"
Write-Host "  - Delete: $InstallPath"
Write-Host ""
