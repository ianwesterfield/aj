<#
.SYNOPSIS
    Creates a client certificate for a FunnelCloud agent, signed by the CA.

.DESCRIPTION
    Generates an agent certificate signed by the FunnelCloud CA using OpenSSL.
    Each agent needs its own certificate for mTLS authentication.

.PARAMETER AgentId
    Unique identifier for the agent (e.g., "dev-workstation", "build-server").
    Will be embedded in the certificate's Common Name.

.PARAMETER CAPath
    Path to the CA certificate directory. Defaults to ../certs/ca

.PARAMETER OutputPath
    Directory to output the agent certificate. Defaults to ../certs/agents/<AgentId>

.PARAMETER ValidDays
    How many days the certificate should be valid. Defaults to 730 (2 years).

.PARAMETER Force
    Overwrite existing certificate if present.

.EXAMPLE
    .\New-AgentCertificate.ps1 -AgentId "dev-workstation"
    Creates certificate for dev-workstation agent

.EXAMPLE
    .\New-AgentCertificate.ps1 -AgentId "build-server" -ValidDays 365
    Creates certificate valid for 1 year
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$AgentId,
    
  [string]$CAPath = (Join-Path $PSScriptRoot "..\certs\ca"),
  [string]$OutputPath,
  [int]$ValidDays = 730,
  [switch]$Force
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($env:FUNNEL_CERT_PASSWORD)) {
  throw "Set FUNNEL_CERT_PASSWORD before generating certificates."
}

# Default output path based on agent ID
if (-not $OutputPath) {
  $OutputPath = Join-Path $PSScriptRoot "..\certs\agents\$AgentId"
}

# Find OpenSSL
$openssl = $null
$possiblePaths = @(
  "openssl",  # System PATH
  "C:\Program Files\Git\usr\bin\openssl.exe",
  "C:\Program Files\OpenSSL-Win64\bin\openssl.exe",
  "C:\OpenSSL\bin\openssl.exe"
)

foreach ($path in $possiblePaths) {
  try {
    $testResult = & $path version 2>$null
    if ($LASTEXITCODE -eq 0) {
      $openssl = $path
      break
    }
  }
  catch {
    continue
  }
}

if (-not $openssl) {
  Write-Error "OpenSSL not found. Install OpenSSL or Git (includes OpenSSL)."
  exit 1
}

Write-Host "Using OpenSSL: $openssl" -ForegroundColor Gray

# Verify CA exists
$caCertPath = Join-Path $CAPath "ca.crt"
$caKeyPath = Join-Path $CAPath "ca.key"

if (-not (Test-Path $caCertPath)) {
  Write-Error "CA certificate not found at $caCertPath. Run New-CACertificate.ps1 first."
  exit 1
}

if (-not (Test-Path $caKeyPath)) {
  Write-Error "CA private key not found at $caKeyPath. Cannot sign agent certificate."
  exit 1
}

# Ensure output directory exists
if (-not (Test-Path $OutputPath)) {
  New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
  Write-Host "Created directory: $OutputPath" -ForegroundColor Green
}

$agentKeyPath = Join-Path $OutputPath "agent.key"
$agentCsrPath = Join-Path $OutputPath "agent.csr"
$agentCertPath = Join-Path $OutputPath "agent.crt"
$agentPfxPath = Join-Path $OutputPath "agent.pfx"
$fingerprintPath = Join-Path $OutputPath "agent-fingerprint.txt"
$configPath = Join-Path $OutputPath "agent.cnf"

# Check if agent cert already exists
if ((Test-Path $agentCertPath) -and -not $Force) {
  Write-Host "Agent certificate already exists at $agentCertPath" -ForegroundColor Yellow
  Write-Host "Use -Force to overwrite" -ForegroundColor Yellow
  exit 0
}

Write-Host "Generating certificate for agent: $AgentId" -ForegroundColor Cyan

# Create OpenSSL config file for agent certificate
$hostname = [Environment]::MachineName
$agentConfig = @"
[ req ]
default_bits = 2048
prompt = no
default_md = sha256
distinguished_name = dn
req_extensions = v3_req

[ dn ]
CN = $AgentId
O = FunnelCloud Agent
OU = $hostname

[ v3_req ]
subjectKeyIdentifier = hash
basicConstraints = critical, CA:FALSE
keyUsage = critical, digitalSignature, keyEncipherment
extendedKeyUsage = serverAuth, clientAuth

[ v3_ca ]
subjectKeyIdentifier = hash
authorityKeyIdentifier = keyid,issuer
basicConstraints = critical, CA:FALSE
keyUsage = critical, digitalSignature, keyEncipherment
extendedKeyUsage = serverAuth, clientAuth
"@

Set-Content -Path $configPath -Value $agentConfig

# Generate agent private key
Write-Host "Generating 2048-bit RSA key..." -ForegroundColor Gray
& $openssl genrsa -out $agentKeyPath 2048 2>$null
if ($LASTEXITCODE -ne 0) {
  Write-Error "Failed to generate agent private key"
  exit 1
}
Write-Host "  Generated: $agentKeyPath" -ForegroundColor Green

# Generate certificate signing request
Write-Host "Creating certificate signing request..." -ForegroundColor Gray
& $openssl req -new -key $agentKeyPath -out $agentCsrPath -config $configPath
if ($LASTEXITCODE -ne 0) {
  Write-Error "Failed to generate CSR"
  exit 1
}
Write-Host "  Generated: $agentCsrPath" -ForegroundColor Green

# Sign the certificate with CA
Write-Host "Signing certificate with CA..." -ForegroundColor Gray
$signOutput = & $openssl x509 -req -in $agentCsrPath -CA $caCertPath -CAkey $caKeyPath -CAcreateserial `
  -out $agentCertPath -days $ValidDays -sha256 -extensions v3_ca -extfile $configPath 2>&1
if (-not (Test-Path $agentCertPath)) {
  Write-Error "Failed to sign certificate: $signOutput"
  exit 1
}
Write-Host "  Generated: $agentCertPath" -ForegroundColor Green

# Verify certificate chain
Write-Host "Verifying certificate chain..." -ForegroundColor Gray
$verifyOutput = & $openssl verify -CAfile $caCertPath $agentCertPath 2>&1
if ($verifyOutput -notmatch "OK") {
  Write-Warning "Certificate chain verification issue: $verifyOutput"
}

# Display certificate info
$certInfo = & $openssl x509 -in $agentCertPath -noout -subject -issuer -dates -fingerprint -sha256 2>&1
Write-Host "`nAgent Certificate Info:" -ForegroundColor Green
$certInfo | ForEach-Object { Write-Host "  $_" }

# Generate PFX for .NET applications
Write-Host "`nCreating PFX bundle..." -ForegroundColor Gray
$pfxOutput = & $openssl pkcs12 -export -out $agentPfxPath -inkey $agentKeyPath -in $agentCertPath `
  -certfile $caCertPath -passout env:FUNNEL_CERT_PASSWORD 2>&1
if (-not (Test-Path $agentPfxPath)) {
  Write-Error "Failed to generate PFX: $pfxOutput"
  exit 1
}
Write-Host "  Generated: $agentPfxPath" -ForegroundColor Green

# Extract and save fingerprint
$fingerprintLine = & $openssl x509 -in $agentCertPath -noout -fingerprint -sha256
$fingerprint = "SHA256:" + ($fingerprintLine -replace "sha256 Fingerprint=", "" -replace ":", "")
Set-Content -Path $fingerprintPath -Value $fingerprint -NoNewline
Write-Host "  Generated: $fingerprintPath" -ForegroundColor Green

Write-Host ""
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host " Agent Fingerprint:" -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "  $fingerprint" -ForegroundColor Yellow

Write-Host ""
Write-Host "Agent certificate generation complete!" -ForegroundColor Green
Write-Host "To use this certificate:" -ForegroundColor Cyan
Write-Host "  1. Set FUNNEL_CERT_PATH=$agentPfxPath"
Write-Host "  2. Set FUNNEL_CERT_PASSWORD to the configured certificate password"
Write-Host "  3. The agent will verify the CA fingerprint from embedded config"

# Cleanup temporary files
Remove-Item $agentCsrPath -ErrorAction SilentlyContinue
Remove-Item $configPath -ErrorAction SilentlyContinue
