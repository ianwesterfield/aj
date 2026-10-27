<#
.SYNOPSIS
    Creates a self-signed CA certificate for FunnelCloud mTLS using OpenSSL.

.DESCRIPTION
    Generates a root CA certificate that will sign all FunnelCloud agent certificates.
    The CA fingerprint is embedded at build time in both Mesosync and agents for pinning.

.PARAMETER OutputPath
    Directory to output the CA certificate files. Defaults to ../certs/ca

.PARAMETER ValidDays
    How many days the CA certificate should be valid. Defaults to 3650 (10 years).

.PARAMETER Force
    Overwrite existing CA certificate if present.

.EXAMPLE
    .\New-CACertificate.ps1
    Creates CA cert in default location (../certs/ca)
#>

[CmdletBinding()]
param(
  [string]$OutputPath = (Join-Path $PSScriptRoot "..\certs\ca"),
  [int]$ValidDays = 3650,
  [switch]$Force
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($env:FUNNEL_CERT_PASSWORD)) {
  throw "Set FUNNEL_CERT_PASSWORD before generating certificates."
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

# Ensure output directory exists
if (-not (Test-Path $OutputPath)) {
  New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
  Write-Host "Created directory: $OutputPath" -ForegroundColor Green
}

$caKeyPath = Join-Path $OutputPath "ca.key"
$caCertPath = Join-Path $OutputPath "ca.crt"
$caPfxPath = Join-Path $OutputPath "ca.pfx"
$fingerprintPath = Join-Path $OutputPath "ca-fingerprint.txt"
$configPath = Join-Path $OutputPath "ca.cnf"

# Check if CA already exists
if ((Test-Path $caCertPath) -and -not $Force) {
  Write-Host "CA certificate already exists at $caCertPath" -ForegroundColor Yellow
  Write-Host "Use -Force to overwrite" -ForegroundColor Yellow
  exit 0
}

Write-Host "Generating FunnelCloud CA Certificate..." -ForegroundColor Cyan

# Create OpenSSL config file for CA
$caConfig = @"
[ req ]
default_bits = 4096
prompt = no
default_md = sha256
distinguished_name = dn
x509_extensions = v3_ca

[ dn ]
CN = FunnelCloud CA
O = Mesosync
OU = Trust Infrastructure

[ v3_ca ]
subjectKeyIdentifier = hash
authorityKeyIdentifier = keyid:always,issuer
basicConstraints = critical, CA:TRUE, pathlen:0
keyUsage = critical, keyCertSign, cRLSign
"@

Set-Content -Path $configPath -Value $caConfig
Write-Host "Created config: $configPath" -ForegroundColor Gray

# Generate CA private key
Write-Host "Generating 4096-bit RSA key..." -ForegroundColor Gray
& $openssl genrsa -out $caKeyPath 4096 2>$null
if ($LASTEXITCODE -ne 0) {
  Write-Error "Failed to generate CA private key"
  exit 1
}
Write-Host "  Generated: $caKeyPath" -ForegroundColor Green

# Generate self-signed CA certificate
Write-Host "Creating self-signed CA certificate..." -ForegroundColor Gray
& $openssl req -x509 -new -nodes -key $caKeyPath -sha256 -days $ValidDays -out $caCertPath -config $configPath
if ($LASTEXITCODE -ne 0) {
  Write-Error "Failed to generate CA certificate"
  exit 1
}
Write-Host "  Generated: $caCertPath" -ForegroundColor Green

# Display certificate info
$certInfo = & $openssl x509 -in $caCertPath -noout -subject -dates -fingerprint -sha256 2>&1
Write-Host "`nCA Certificate Info:" -ForegroundColor Green
$certInfo | ForEach-Object { Write-Host "  $_" }

# Generate PFX for .NET applications
Write-Host "`nCreating PFX bundle..." -ForegroundColor Gray
& $openssl pkcs12 -export -out $caPfxPath -inkey $caKeyPath -in $caCertPath -passout env:FUNNEL_CERT_PASSWORD
if ($LASTEXITCODE -ne 0) {
  Write-Error "Failed to generate PFX"
  exit 1
}
Write-Host "  Generated: $caPfxPath" -ForegroundColor Green

# Extract and save fingerprint for pinning
$fingerprintLine = & $openssl x509 -in $caCertPath -noout -fingerprint -sha256
$fingerprint = "SHA256:" + ($fingerprintLine -replace "sha256 Fingerprint=", "" -replace ":", "")
Set-Content -Path $fingerprintPath -Value $fingerprint -NoNewline
Write-Host "  Generated: $fingerprintPath" -ForegroundColor Green

Write-Host ""
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host " CA Fingerprint (embed this in agents at build time):" -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "  $fingerprint" -ForegroundColor Yellow

Write-Host ""
Write-Host "CA certificate generation complete!" -ForegroundColor Green
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Run: .\New-AgentCertificate.ps1 -AgentId 'agent-name'" -ForegroundColor White
Write-Host "  2. Copy ca-fingerprint.txt to FunnelCloud.Agent/Certificates/"
Write-Host "  3. Keep ca.key SECURE - it can sign new agent certificates"

# Cleanup config (key and cert files remain)
Remove-Item $configPath -ErrorAction SilentlyContinue
