# Deploy-FunnelCloudAgent-Linux.ps1
# Deploys FunnelCloud agent to a Linux machine via SSH

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$TargetHost,
    [Parameter(Mandatory = $false)][string]$TargetUser = "root",
    [Parameter(Mandatory = $false)][string]$AgentId,
    [Parameter(Mandatory = $false)][string]$InstallPath = "/opt/funnelcloud",
    [Parameter(Mandatory = $false)][string]$SshKeyPath,
    [switch]$Insecure,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

if ($Insecure) { throw "Insecure agent deployment is no longer supported." }
$certPassword = [string]$env:FUNNEL_CERT_PASSWORD
if ([string]::IsNullOrWhiteSpace($certPassword)) {
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

if (-not $AgentId) { $AgentId = ($TargetHost -replace '\..*$', '' -replace '[^a-zA-Z0-9-]', '-') }

$scriptRoot = $PSScriptRoot
$projectRoot = Split-Path $scriptRoot -Parent
$agentProject = Join-Path $projectRoot "FunnelCloud.Agent"
$certsRoot = Join-Path $projectRoot "certs"
$caPath = Join-Path $certsRoot "ca"
$agentCertsPath = Join-Path (Join-Path $certsRoot "agents") $AgentId
$publishPath = Join-Path $projectRoot "publish-linux"

function Log-Step($m) { Write-Host "`n[>] $m" -ForegroundColor Cyan }
function Log-Ok($m) { Write-Host "    [OK] $m" -ForegroundColor Green }
function Log-Warn($m) { Write-Host "    [!] $m" -ForegroundColor Yellow }
function Log-Err($m) { Write-Host "    [X] $m" -ForegroundColor Red }
function Log-Info($m) { Write-Host "    $m" -ForegroundColor Gray }

function Get-SshOptions {
    $opts = @("-o", "StrictHostKeyChecking=no", "-o", "UserKnownHostsFile=/dev/null")
    if ($SshKeyPath) { $opts += @("-i", $SshKeyPath) }
    return $opts
}

function Run-Ssh([string]$Cmd) {
    $opts = Get-SshOptions
    $ErrorActionPreference = "Continue"
    $output = & ssh @opts "${TargetUser}@${TargetHost}" $Cmd 2>&1
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    # Filter out SSH warnings from output
    $cleanOutput = @()
    foreach ($line in $output) {
        $str = "$line"
        if ($str -notmatch "^Warning:" -and $str -notmatch "^debug1:") {
            $cleanOutput += $str
        }
    }
    $result = $cleanOutput -join "`n"
    if ($exitCode -ne 0) { throw "SSH failed (exit $exitCode): $result" }
    return $result
}

function Run-Scp([string]$Src, [string]$Dst, [switch]$Recurse) {
    $opts = Get-SshOptions
    if ($Recurse) { $opts += "-r" }
    $opts += @($Src, "${TargetUser}@${TargetHost}:$Dst")
    $ErrorActionPreference = "Continue"
    & scp @opts 2>&1
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    if ($exitCode -ne 0) { throw "SCP failed (exit $exitCode)" }
}

# Banner
Write-Host "`n================================================================" -ForegroundColor Magenta
Write-Host "      FunnelCloud Agent Linux Deployment                       " -ForegroundColor Magenta
Write-Host "================================================================`n" -ForegroundColor Magenta
Write-Host "Configuration:" -ForegroundColor White
Write-Host "  Target:       ${TargetUser}@${TargetHost}"
Write-Host "  Agent ID:     $AgentId"
Write-Host "  Install Path: $InstallPath"
Write-Host "  Mode:         $(if($Insecure){'INSECURE'}else{'Secure (mTLS)'})`n"

# Preflight
Log-Step "Pre-flight checks"
if (-not (Get-Command ssh -EA SilentlyContinue)) { Log-Err "SSH not found"; exit 1 }
if (-not (Get-Command scp -EA SilentlyContinue)) { Log-Err "SCP not found"; exit 1 }
Log-Ok "SSH/SCP found"

if (-not $SkipBuild -and -not (Get-Command dotnet -EA SilentlyContinue)) { Log-Err ".NET SDK not found"; exit 1 }
if (-not $SkipBuild) { Log-Ok ".NET SDK found" }

if (-not $Insecure) {
    $caCert = Join-Path $caPath "ca.crt"
    if (-not (Test-Path $caCert)) { Log-Err "CA cert not found at $caCert"; exit 1 }
    Log-Ok "CA certificate found"
}

# Test SSH
Log-Step "Testing SSH connection"
try {
    $info = Run-Ssh "uname -a"
    Log-Ok "Connected: $info"
}
catch {
    Log-Err "SSH failed: $_"
    exit 1
}

# Generate cert
if (-not $Insecure) {
    Log-Step "Checking certificate for $AgentId"
    $agentCert = Join-Path $agentCertsPath "agent.crt"
    $agentPfx = Join-Path $agentCertsPath "agent.pfx"
    
    if (-not (Test-Path $agentCert) -or -not (Test-Path $agentPfx)) {
        Log-Info "Generating new certificate..."
        New-Item -ItemType Directory -Path $agentCertsPath -Force | Out-Null
        
        $openssl = @("openssl", "C:\Program Files\Git\usr\bin\openssl.exe") | Where-Object { Get-Command $_ -EA SilentlyContinue } | Select-Object -First 1
        if (-not $openssl) { Log-Err "OpenSSL not found"; exit 1 }
        
        $keyPath = Join-Path $agentCertsPath "agent.key"
        & $openssl genrsa -out $keyPath 4096 2>$null
        
        $csrConf = @"
[req]
default_bits = 4096
prompt = no
default_md = sha256
distinguished_name = dn
req_extensions = req_ext
[dn]
CN = FunnelCloud Agent - $AgentId
O = FunnelCloud
OU = Agents
[req_ext]
subjectAltName = DNS:$AgentId,DNS:$TargetHost,IP:$TargetHost
keyUsage = digitalSignature, keyEncipherment
extendedKeyUsage = clientAuth, serverAuth
"@
        $csrConfPath = Join-Path $agentCertsPath "csr.cnf"
        $csrConf | Out-File $csrConfPath -Encoding ASCII
        
        $csrPath = Join-Path $agentCertsPath "agent.csr"
        $ErrorActionPreference = "Continue"
        & $openssl req -new -key $keyPath -out $csrPath -config $csrConfPath 2>$null
        
        $caKey = Join-Path $caPath "ca.key"
        $caCert = Join-Path $caPath "ca.crt"
        & $openssl x509 -req -in $csrPath -CA $caCert -CAkey $caKey -CAcreateserial -out $agentCert -days 730 -extensions req_ext -extfile $csrConfPath 2>$null
        
        & $openssl pkcs12 -export -out $agentPfx -inkey $keyPath -in $agentCert -certfile $caCert -passout env:FUNNEL_CERT_PASSWORD 2>$null
        $ErrorActionPreference = "Stop"
        
        Remove-Item $csrPath, $csrConfPath -EA SilentlyContinue
        Log-Ok "Certificate created"
    }
    else {
        Log-Ok "Using existing certificate"
    }
}

# Build
if (-not $SkipBuild) {
    Log-Step "Building for linux-x64"
    if (Test-Path $publishPath) { Remove-Item $publishPath -Recurse -Force }
    Push-Location $agentProject
    try {
        $r = dotnet publish -c Release -r linux-x64 --self-contained -o $publishPath 2>&1
        if ($LASTEXITCODE -ne 0) { Log-Err "Build failed: $r"; exit 1 }
        Log-Ok "Build complete"
    }
    finally { Pop-Location }
}
else {
    if (-not (Test-Path $publishPath)) { Log-Err "No build at $publishPath"; exit 1 }
    Log-Ok "Using existing build"
}

# Stage
Log-Step "Staging deployment"
$staging = Join-Path $env:TEMP "funnelcloud-$AgentId"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory $staging -Force | Out-Null
Copy-Item "$publishPath\*" $staging -Recurse

if (-not $Insecure) {
    $certStaging = Join-Path $staging "Certificates"
    New-Item -ItemType Directory $certStaging -Force | Out-Null
    Copy-Item (Join-Path $caPath "ca.crt") $certStaging
    Copy-Item (Join-Path $agentCertsPath "agent.crt") $certStaging
    Copy-Item (Join-Path $agentCertsPath "agent.pfx") $certStaging
    [IO.File]::WriteAllText((Join-Path $certStaging 'pfx-password'), $certPassword, [Text.UTF8Encoding]::new($false))
}

# Systemd service
@"
[Unit]
Description=FunnelCloud Agent - $AgentId
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$TargetUser
WorkingDirectory=$InstallPath
ExecStart=$InstallPath/FunnelCloud.Agent
Restart=always
RestartSec=10
Environment=FUNNEL_AGENT_ID=$AgentId
Environment=FUNNEL_CERT_PATH=$InstallPath/Certificates/agent.pfx
LoadCredential=funnel-cert-password:$InstallPath/Certificates/pfx-password
Environment=FUNNEL_CERT_PASSWORD_FILE=%d/funnel-cert-password
Environment=FUNNEL_CA_PATH=$InstallPath/Certificates/ca.crt
Environment=FUNNEL_ALLOWED_CLIENT_SHA256=$clientFingerprints
StandardOutput=journal
StandardError=journal
SyslogIdentifier=funnelcloud-agent

[Install]
WantedBy=multi-user.target
"@ | Out-File (Join-Path $staging "funnelcloud-agent.service") -Encoding ASCII

Log-Ok "Staged to $staging"

# Deploy
Log-Step "Deploying to $TargetHost"
try { Run-Ssh "sudo systemctl stop funnelcloud-agent 2>/dev/null || true" } catch {}

Run-Ssh "rm -rf /tmp/fc-staging && mkdir -m 700 /tmp/fc-staging"
Log-Info "Copying files..."
Run-Scp "$staging\*" "/tmp/fc-staging/" -Recurse
Log-Ok "Files copied"

Log-Info "Installing..."
$installCmd = @"
sudo mkdir -p $InstallPath $InstallPath/logs && \
sudo cp -r /tmp/fc-staging/* $InstallPath/ && \
sudo chmod +x $InstallPath/FunnelCloud.Agent && \
sudo chown -R ${TargetUser}:${TargetUser} $InstallPath && \
if [ -d $InstallPath/Certificates ]; then sudo chmod 700 $InstallPath/Certificates && sudo chmod 600 $InstallPath/Certificates/*; fi && \
sudo cp $InstallPath/funnelcloud-agent.service /etc/systemd/system/ && \
sudo systemctl daemon-reload && \
sudo systemctl enable funnelcloud-agent && \
sudo systemctl restart funnelcloud-agent
"@
Run-Ssh $installCmd
Log-Ok "Installed"

Run-Ssh "rm -rf /tmp/fc-staging"
Remove-Item $staging -Recurse -Force

# Verify
Log-Step "Verifying"
Start-Sleep 3
try {
    $status = Run-Ssh "systemctl is-active funnelcloud-agent"
    if ($status -match "active") { Log-Ok "Service running" }
    else { Log-Warn "Status: $status" }
}
catch { Log-Warn "Could not verify" }

Write-Host "`n================================================================" -ForegroundColor Green
Write-Host "         Deployment Complete!                                  " -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
Write-Host "`nPorts: UDP 41420 (discovery), TCP 41235 (gRPC), TCP 41236 (HTTP)"
Write-Host "`nCommands:"
Write-Host "  sudo systemctl status funnelcloud-agent" -ForegroundColor Gray
Write-Host "  sudo journalctl -u funnelcloud-agent -f" -ForegroundColor Gray

try {
    Write-Host "`nRecent logs:" -ForegroundColor Yellow
    Run-Ssh "sudo journalctl -u funnelcloud-agent -n 10 --no-pager 2>/dev/null || echo 'No logs yet'"
}
catch {}
