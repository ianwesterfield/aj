<#
.SYNOPSIS
    Deploy FunnelCloud Agent to a Windows machine.

.DESCRIPTION
    This script bootstraps a FunnelCloud agent on a remote Windows client by:
    1. Connecting to the build server to generate a unique certificate
    2. Building/packaging the agent with the certificate
    3. Downloading the package to the local machine
    4. Installing and starting the agent as a Windows service

.PARAMETER BuildServer
    The hostname or IP of the build server (your workstation).
    Default: ians-r16

.PARAMETER BuildServerUser
    Username for connecting to build server. If not specified, uses current user.

.PARAMETER AgentId
    Unique identifier for this agent. Defaults to local computer name.

.PARAMETER InstallPath
    Where to install the agent. Default: C:\FunnelCloud\Agent

.PARAMETER Insecure
    Retained for compatibility; insecure mode is no longer supported.

.EXAMPLE
    .\Deploy-FunnelCloudAgent.ps1 -BuildServer "ians-r16"

.EXAMPLE
    .\Deploy-FunnelCloudAgent.ps1 -BuildServer "192.168.1.100" -AgentId "build-server-01"

.NOTES
    Prerequisites:
    - PowerShell Remoting enabled on build server
    - Admin rights on local machine for service installation
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$BuildServer = "ians-r16",
    
    [string]$BuildServerUser,
    
    [string]$AgentId = $env:COMPUTERNAME.ToLower(),
    
    [string]$InstallPath = "C:\FunnelCloud\Agent",
    
    [switch]$Insecure
)

$ErrorActionPreference = "Stop"

if ($Insecure) { throw "Insecure agent deployment is no longer supported." }
if ([string]::IsNullOrWhiteSpace($env:FUNNEL_CERT_PASSWORD)) {
    throw "Set FUNNEL_CERT_PASSWORD before deploying a secure agent."
}
if ([string]::IsNullOrWhiteSpace($env:FUNNEL_ALLOWED_CLIENT_SHA256)) {
    throw "Set FUNNEL_ALLOWED_CLIENT_SHA256 before deploying a secure agent."
}
$clientFingerprints = @($env:FUNNEL_ALLOWED_CLIENT_SHA256 -split ',' | ForEach-Object { $_.Trim() })
foreach ($fingerprint in $clientFingerprints) {
    if (($fingerprint -replace '(?i)^SHA256:', '' -replace ':', '') -notmatch '^[A-Fa-f0-9]{64}$') {
        throw "FUNNEL_ALLOWED_CLIENT_SHA256 contains an invalid fingerprint."
    }
}
$clientFingerprints = $clientFingerprints -join ','

# Simple output functions (ASCII-safe)
function Write-Step { param($msg) Write-Host ""; Write-Host "[>] $msg" -ForegroundColor Cyan }
function Write-Ok { param($msg) Write-Host "    [OK] $msg" -ForegroundColor Green }
function Write-Warn { param($msg) Write-Host "    [!] $msg" -ForegroundColor Yellow }
function Write-Err { param($msg) Write-Host "    [X] $msg" -ForegroundColor Red }

# Banner
Write-Host ""
Write-Host "================================================================" -ForegroundColor Magenta
Write-Host "         FunnelCloud Agent Deployment Script                   " -ForegroundColor Magenta
Write-Host "================================================================" -ForegroundColor Magenta
Write-Host ""
Write-Host "  This script will:"
Write-Host "  1. Connect to build server and generate your certificate"
Write-Host "  2. Build and package the agent"
Write-Host "  3. Download and install locally as a Windows service"
Write-Host ""

Write-Host "Configuration:" -ForegroundColor White
Write-Host "  Build Server:  $BuildServer"
Write-Host "  Agent ID:      $AgentId"
Write-Host "  Install Path:  $InstallPath"
if ($Insecure) {
    Write-Host "  Mode:          INSECURE (no mTLS)" -ForegroundColor Yellow
}
else {
    Write-Host "  Mode:          Secure (mTLS)"
}
Write-Host ""

# Check if running as admin
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Err "This script must be run as Administrator for service installation."
    Write-Host "  Right-click PowerShell and select 'Run as Administrator'" -ForegroundColor Gray
    exit 1
}

# Build paths
$buildRoot = "C:\Code\aj.westerfield.cloud\layers\agents\FunnelCloud"
$packageName = "FunnelCloud-Agent-$AgentId.zip"
$tempPath = Join-Path $env:TEMP $packageName
$certsPath = Join-Path $InstallPath "Certificates"

# ================================================================
# STEP 1: Connect to build server
# ================================================================
Write-Step "Connecting to build server: $BuildServer"

$sessionParams = @{
    ComputerName = $BuildServer
}

if ($BuildServerUser) {
    $cred = Get-Credential -UserName $BuildServerUser -Message "Enter credentials for $BuildServer"
    $sessionParams.Credential = $cred
}

try {
    $session = New-PSSession @sessionParams
    Write-Ok "Connected to $BuildServer"
}
catch {
    Write-Err "Could not connect to build server: $_"
    Write-Host ""
    Write-Host "Troubleshooting:" -ForegroundColor Gray
    Write-Host "  1. Ensure PowerShell Remoting is enabled on the build server:" -ForegroundColor Gray
    Write-Host "     Run on build server: Enable-PSRemoting -Force" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  2. If on different domain/workgroup, add to TrustedHosts:" -ForegroundColor Gray
    Write-Host "     Set-Item WSMan:\localhost\Client\TrustedHosts -Value $BuildServer -Force" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  3. Check Windows Firewall allows WinRM (TCP 5985/5986)" -ForegroundColor Gray
    Write-Host ""
    exit 1
}

# ================================================================
# STEP 2: Generate certificate and build agent on build server
# ================================================================
Write-Step "Building agent package on $BuildServer (Agent ID: $AgentId)"

$remoteBuildScript = {
    param($AgentId, $BuildRoot, $Insecure, $CertPassword)
    
    $results = @{
        Success      = $false
        PackagePath  = ""
        Error        = ""
    }
    
    try {
        Set-Location $BuildRoot
        
        # Generate certificate for this agent (unless insecure mode)
        if (-not $Insecure) {
            $env:FUNNEL_CERT_PASSWORD = $CertPassword
            Write-Host "Generating certificate for agent: $AgentId"
            $certScript = Join-Path $BuildRoot "scripts\New-AgentCertificate.ps1"
            $expectedPfx = Join-Path $BuildRoot "certs\agents\$AgentId\agent.pfx"
            
            # Run cert script, redirecting stderr to a temp file to avoid PS remoting issues
            $stderrFile = Join-Path $env:TEMP "cert-stderr.txt"
            $processArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$certScript`" -AgentId `"$AgentId`" -Force"
            $proc = Start-Process -FilePath "powershell.exe" -ArgumentList $processArgs -Wait -PassThru -NoNewWindow -RedirectStandardError $stderrFile
            Remove-Item $stderrFile -ErrorAction SilentlyContinue
            
            # Check if the expected output files exist
            if (-not (Test-Path $expectedPfx)) {
                throw "Certificate PFX not found at $expectedPfx"
            }
            Write-Host "Certificate generated successfully"
        }
        
        # Build/publish the agent
        Write-Host "Building agent..."
        $agentProject = Join-Path $BuildRoot "FunnelCloud.Agent"
        $publishPath = Join-Path $env:TEMP "FunnelCloud-$AgentId"
        
        # Clean previous build
        if (Test-Path $publishPath) {
            Remove-Item $publishPath -Recurse -Force
        }
        
        Push-Location $agentProject
        dotnet publish -c Release -r win-x64 --self-contained -o $publishPath 2>&1
        Pop-Location
        
        if (-not (Test-Path (Join-Path $publishPath "FunnelCloud.Agent.exe"))) {
            throw "Build failed - FunnelCloud.Agent.exe not found"
        }
        
        # Copy certificates to package (unless insecure)
        $certDestPath = Join-Path $publishPath "Certificates"
        New-Item -ItemType Directory -Path $certDestPath -Force | Out-Null
        
        if (-not $Insecure) {
            $certSourcePath = Join-Path $BuildRoot "certs\agents\$AgentId"
            Copy-Item (Join-Path $certSourcePath "agent.pfx") -Destination $certDestPath -Force
            Copy-Item (Join-Path $certSourcePath "agent-fingerprint.txt") -Destination $certDestPath -Force
            Copy-Item (Join-Path $BuildRoot "certs\ca\ca.crt") -Destination $certDestPath -Force
            
            # Also copy CA fingerprint for verification
            $caFingerprint = Join-Path $BuildRoot "certs\ca\ca-fingerprint.txt"
            if (Test-Path $caFingerprint) {
                Copy-Item $caFingerprint -Destination $certDestPath -Force
            }
        }
        
        # Create ZIP package
        $packagePath = Join-Path $env:TEMP "FunnelCloud-Agent-$AgentId.zip"
        if (Test-Path $packagePath) {
            Remove-Item $packagePath -Force
        }
        
        Compress-Archive -Path "$publishPath\*" -DestinationPath $packagePath -Force
        
        $results.Success = $true
        $results.PackagePath = $packagePath
        
        Write-Host "Package created: $packagePath"
    }
    catch {
        $results.Error = $_.ToString()
    }
    
    return $results
}

try {
    # Use -ErrorAction SilentlyContinue to ignore stderr from remote OpenSSL commands
    $buildResult = Invoke-Command -Session $session -ScriptBlock $remoteBuildScript -ArgumentList $AgentId, $buildRoot, $Insecure, $env:FUNNEL_CERT_PASSWORD -ErrorAction SilentlyContinue
    
    if (-not $buildResult -or -not $buildResult.Success) {
        $errorMsg = if ($buildResult) { $buildResult.Error } else { "No result returned from remote build" }
        throw "Remote build failed: $errorMsg"
    }
    
    Write-Ok "Agent package built successfully"
}
catch {
    Write-Err "Build failed: $_"
    Remove-PSSession $session -ErrorAction SilentlyContinue
    exit 1
}

# ================================================================
# STEP 3: Download package from build server
# ================================================================
Write-Step "Downloading agent package"

try {
    Copy-Item -FromSession $session -Path $buildResult.PackagePath -Destination $tempPath
    Write-Ok "Downloaded to: $tempPath"
}
catch {
    Write-Err "Download failed: $_"
    Remove-PSSession $session -ErrorAction SilentlyContinue
    exit 1
}

# Clean up remote session
Remove-PSSession $session

# ================================================================
# STEP 4: Extract and install locally
# ================================================================
Write-Step "Installing agent to: $InstallPath"

try {
    # Stop existing service if running
    $existingService = Get-Service FunnelCloudAgent -ErrorAction SilentlyContinue
    if ($existingService) {
        Write-Warn "Stopping existing FunnelCloud Agent service..."
        Stop-Service FunnelCloudAgent -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    
    # Create install directory
    if (Test-Path $InstallPath) {
        # Backup existing install
        $backupPath = "$InstallPath.backup.$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        Write-Warn "Backing up existing installation to: $backupPath"
        Move-Item $InstallPath $backupPath -Force
    }
    
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
    New-Item -ItemType Directory -Path $certsPath -Force | Out-Null
    & icacls $certsPath /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not restrict certificate directory permissions." }
    
    # Extract package
    Expand-Archive -Path $tempPath -DestinationPath $InstallPath -Force
    $passwordPath = Join-Path $certsPath 'pfx-password'
    [IO.File]::WriteAllText($passwordPath, $env:FUNNEL_CERT_PASSWORD, [Text.UTF8Encoding]::new($false))
    Write-Ok "Extracted agent files"
    
    # Clean up temp file
    Remove-Item $tempPath -Force
}
catch {
    Write-Err "Installation failed: $_"
    exit 1
}

# ================================================================
# STEP 5: Install and start service
# ================================================================
Write-Step "Installing Windows service"

try {
    # Check for NSSM
    $nssm = Get-Command nssm -ErrorAction SilentlyContinue
    if (-not $nssm) {
        Write-Warn "NSSM not found, installing..."
        winget install nssm --accept-source-agreements --accept-package-agreements
        
        # Refresh PATH
        $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path", "User")
        
        # Verify NSSM is now available
        $nssm = Get-Command nssm -ErrorAction SilentlyContinue
        if (-not $nssm) {
            throw "NSSM installation failed - not found in PATH"
        }
    }
    
    # Remove existing service if present - temporarily disable error handling for NSSM
    $existingSvc = Get-Service FunnelCloudAgent -ErrorAction SilentlyContinue
    if ($existingSvc) {
        Write-Warn "Removing existing service..."
        $ErrorActionPreference = "SilentlyContinue"
        & nssm stop FunnelCloudAgent 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        & nssm remove FunnelCloudAgent confirm 2>&1 | Out-Null
        Start-Sleep -Seconds 1
        $ErrorActionPreference = "Stop"
    }
    
    # Install service
    $exePath = Join-Path $InstallPath "FunnelCloud.Agent.exe"
    if (-not (Test-Path $exePath)) {
        throw "Agent executable not found at $exePath"
    }
    
    Write-Host "    Installing service with NSSM..." -ForegroundColor Gray
    $installResult = & nssm install FunnelCloudAgent $exePath 2>&1
    Start-Sleep -Seconds 1
    
    # Verify service was created
    $svc = Get-Service FunnelCloudAgent -ErrorAction SilentlyContinue
    if (-not $svc) {
        throw "NSSM failed to create service: $installResult"
    }
    
    # Configure service (suppress NSSM output)
    $null = & nssm set FunnelCloudAgent AppDirectory $InstallPath 2>&1
    
    # Configure logging
    $logsPath = Join-Path $InstallPath "logs"
    New-Item -ItemType Directory -Path $logsPath -Force | Out-Null
    $null = & nssm set FunnelCloudAgent AppStdout (Join-Path $logsPath "stdout.log") 2>&1
    $null = & nssm set FunnelCloudAgent AppStderr (Join-Path $logsPath "stderr.log") 2>&1
    $null = & nssm set FunnelCloudAgent AppRotateFiles 1 2>&1
    $null = & nssm set FunnelCloudAgent AppRotateBytes 1048576 2>&1
    
    # Set environment for certificates (unless insecure)
    if (-not $Insecure) {
        $certPath = Join-Path $InstallPath "Certificates\agent.pfx"
        $caPath = Join-Path $InstallPath "Certificates\ca.crt"
        $null = & nssm set FunnelCloudAgent AppEnvironmentExtra "FUNNEL_CERT_PATH=$certPath" "FUNNEL_CERT_PASSWORD_FILE=$passwordPath" "FUNNEL_CA_PATH=$caPath" "FUNNEL_ALLOWED_CLIENT_SHA256=$clientFingerprints" 2>&1
    }
    
    # Auto-start and metadata
    $null = & nssm set FunnelCloudAgent Start SERVICE_AUTO_START 2>&1
    $null = & nssm set FunnelCloudAgent DisplayName "FunnelCloud Agent" 2>&1
    $null = & nssm set FunnelCloudAgent Description "FunnelCloud remote task execution agent" 2>&1
    
    Write-Ok "Service installed"
}
catch {
    Write-Err "Service installation failed: $_"
    exit 1
}

# ================================================================
# STEP 6: Start service and verify
# ================================================================
Write-Step "Starting FunnelCloud Agent service"

try {
    $null = & nssm start FunnelCloudAgent 2>&1
    Start-Sleep -Seconds 3
    
    $service = Get-Service FunnelCloudAgent -ErrorAction SilentlyContinue
    if ($service.Status -eq "Running") {
        Write-Ok "Service is running!"
    }
    else {
        Write-Warn "Service status: $($service.Status)"
        Write-Host "    Check logs at: $logsPath" -ForegroundColor Gray
    }
}
catch {
    Write-Err "Failed to start service: $_"
}

# ================================================================
# Summary
# ================================================================
Write-Host ""
Write-Host "================================================================" -ForegroundColor Green
Write-Host "                  Deployment Complete!                         " -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
Write-Host ""

Write-Host "Agent Details:" -ForegroundColor White
Write-Host "  Agent ID:      $AgentId"
Write-Host "  Install Path:  $InstallPath"
Write-Host "  Logs:          $(Join-Path $InstallPath 'logs')"
Write-Host "  Service:       FunnelCloudAgent"
Write-Host ""

# Show service status
Get-Service FunnelCloudAgent | Format-List Name, Status, StartType

Write-Host "Commands:" -ForegroundColor Gray
$logFile = Join-Path $logsPath "stdout.log"
Write-Host "  View logs:     Get-Content '$logFile' -Tail 50" -ForegroundColor Gray
Write-Host "  Stop service:  Stop-Service FunnelCloudAgent" -ForegroundColor Gray
Write-Host "  Start service: Start-Service FunnelCloudAgent" -ForegroundColor Gray
Write-Host "  Uninstall:     nssm remove FunnelCloudAgent confirm" -ForegroundColor Gray
Write-Host ""
