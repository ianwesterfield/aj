<#
.SYNOPSIS
    Generates a FunnelCloud agent certificate signed by the CA — pure .NET, no OpenSSL.
    Requires PowerShell 7+. Drop-in replacement for New-AgentCertificate.ps1 on build agents.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string]$AgentId,
  [Parameter(Mandatory)] [string]$CACertPath,
  [Parameter(Mandatory)] [string]$CAKeyPath,
  [Parameter(Mandatory)] [string]$OutDir,
  [Parameter(Mandatory)] [string]$PfxPassword,
  [int]$ValidDays = 730
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Requires PowerShell 7+ (ImportFromPem).' }

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

# Load CA cert + key
$caCert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPemFile($CACertPath, $CAKeyPath)

# Agent key + CSR
$key = [System.Security.Cryptography.RSA]::Create(2048)
$dn = "CN=$AgentId, O=FunnelCloud Agent, OU=$([Environment]::MachineName)"
$req = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
  $dn, $key,
  [System.Security.Cryptography.HashAlgorithmName]::SHA256,
  [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

$req.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
$req.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor
    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment, $true))
$eku = [System.Security.Cryptography.OidCollection]::new()
$null = $eku.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))  # serverAuth
$null = $eku.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.2'))  # clientAuth
$req.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku, $false))
$req.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($req.PublicKey, $false))

$serial = [byte[]]::new(12)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($serial)
$notBefore = [DateTimeOffset]::UtcNow.AddMinutes(-5)
$caNotBefore = [DateTimeOffset]::new($caCert.NotBefore.ToUniversalTime())
if ($notBefore -lt $caNotBefore) { $notBefore = $caNotBefore }
$cert = $req.Create($caCert, $notBefore, $notBefore.AddDays($ValidDays), $serial)
$certWithKey = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey($cert, $key)

# PFX bundle (agent cert + CA, matching the OpenSSL script's layout)
$chain = [System.Security.Cryptography.X509Certificates.X509Certificate2Collection]::new()
$null = $chain.Add($certWithKey)
$null = $chain.Add([System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem((Get-Content $CACertPath -Raw)))
[IO.File]::WriteAllBytes((Join-Path $OutDir 'agent.pfx'), $chain.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12, $PfxPassword))

# PEM pieces + fingerprint + CA copy
Set-Content (Join-Path $OutDir 'agent.crt') ($cert.ExportCertificatePem()) -NoNewline
Set-Content (Join-Path $OutDir 'agent.key') ($key.ExportRSAPrivateKeyPem()) -NoNewline
$fingerprint = 'SHA256:' + $cert.GetCertHashString([System.Security.Cryptography.HashAlgorithmName]::SHA256)
Set-Content (Join-Path $OutDir 'agent-fingerprint.txt') $fingerprint -NoNewline
Copy-Item $CACertPath (Join-Path $OutDir 'ca.crt')

Write-Host "Generated cert for '$AgentId' valid $ValidDays days"
Write-Host "Fingerprint: $fingerprint"
