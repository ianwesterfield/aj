<#
.SYNOPSIS
    Push-model deploy of the FunnelCloud agent to a Windows client over WinRM.
    Updates binaries + certs of an existing FunnelCloudAgent service installation
    (first-time installs: run Deploy-FunnelCloudAgent.ps1 on the client once).
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string]$ComputerName,
  [Parameter(Mandatory)] [string]$User,
  [Parameter(Mandatory)] [string]$Password,
  [Parameter(Mandatory)] [string]$SourceDir,
  [string]$CertDir,
  [string]$InstallPath = 'C:\FunnelCloud\Agent',
  [string]$ServiceName = 'FunnelCloudAgent'
)

$ErrorActionPreference = 'Stop'

if ($CertDir) {
  if ([string]::IsNullOrWhiteSpace($env:FUNNEL_CERT_PASSWORD) -or [string]::IsNullOrWhiteSpace($env:FUNNEL_ALLOWED_CLIENT_SHA256)) {
    throw 'Set FUNNEL_CERT_PASSWORD and FUNNEL_ALLOWED_CLIENT_SHA256 before deploying certificates.'
  }
  $clientFingerprints = @($env:FUNNEL_ALLOWED_CLIENT_SHA256 -split ',' | ForEach-Object { $_.Trim() })
  foreach ($fingerprint in $clientFingerprints) {
    if (($fingerprint -replace '(?i)^SHA256:', '' -replace ':', '') -notmatch '^[A-Fa-f0-9]{64}$') {
      throw 'FUNNEL_ALLOWED_CLIENT_SHA256 contains an invalid fingerprint.'
    }
  }
  $clientFingerprints = $clientFingerprints -join ','
  foreach ($name in @('agent.pfx', 'ca.crt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $CertDir $name))) { throw "Certificate directory must contain $name." }
  }
}

$cred = [pscredential]::new($User, (ConvertTo-SecureString $Password -AsPlainText -Force))
$session = New-PSSession -ComputerName $ComputerName -Credential $cred

try {
  $zip = Join-Path ([IO.Path]::GetTempPath()) "fc-deploy-$([guid]::NewGuid().ToString('N')).zip"
  Compress-Archive -Path "$SourceDir\*" -DestinationPath $zip -Force
  Write-Host "Package: $((Get-Item $zip).Length / 1MB -as [int]) MB"

  $remoteZip = "C:\Windows\Temp\fc-deploy.zip"
  Copy-Item $zip -Destination $remoteZip -ToSession $session -Force
  if ($CertDir) {
    Invoke-Command -Session $session -ScriptBlock {
      $directory = 'C:\Windows\Temp\fc-certs'
      Remove-Item $directory -Recurse -Force -ErrorAction SilentlyContinue
      New-Item -ItemType Directory -Path $directory -Force | Out-Null
      & icacls $directory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
      if ($LASTEXITCODE -ne 0) { throw 'Could not restrict certificate staging directory permissions.' }
    }
    foreach ($name in @('agent.pfx', 'ca.crt')) {
      Copy-Item (Join-Path $CertDir $name) -Destination "C:\Windows\Temp\fc-certs\$name" -ToSession $session -Force
    }
  }

  Invoke-Command -Session $session -ScriptBlock {
    param($remoteZip, $installPath, $serviceName, $hasCerts, $certPassword, $clientFingerprints)

    $svc = Get-Service $serviceName -ErrorAction SilentlyContinue
    if (-not $svc) { throw "Service '$serviceName' not found on $env:COMPUTERNAME - run Deploy-FunnelCloudAgent.ps1 once for first-time install." }

    Stop-Service $serviceName -Force
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Service $serviceName).Status -ne 'Stopped' -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
    if ((Get-Service $serviceName).Status -ne 'Stopped') { throw "Could not stop $serviceName" }

    Expand-Archive $remoteZip -DestinationPath $installPath -Force
    if ($hasCerts) {
      $certTarget = Join-Path $installPath 'Certificates'
      New-Item -ItemType Directory -Path $certTarget -Force | Out-Null
      & icacls $certTarget /reset | Out-Null
      if ($LASTEXITCODE -ne 0) { throw 'Could not reset certificate directory permissions.' }
      & icacls $certTarget /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
      if ($LASTEXITCODE -ne 0) { throw 'Could not restrict certificate directory permissions.' }
      Copy-Item 'C:\Windows\Temp\fc-certs\*' $certTarget -Force
      $passwordPath = Join-Path $certTarget 'pfx-password'
      [IO.File]::WriteAllText($passwordPath, $certPassword, [Text.UTF8Encoding]::new($false))
      foreach ($name in @('agent.pfx', 'ca.crt', 'pfx-password')) {
        & icacls (Join-Path $certTarget $name) /reset | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not restrict permissions on $name." }
      }
      Remove-Item 'C:\Windows\Temp\fc-certs' -Recurse -Force
      $serviceParameters = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName\Parameters"
      if (-not (Test-Path $serviceParameters)) { throw "NSSM parameters not found for service '$serviceName'." }
      $serviceEnvironment = @(
        "FUNNEL_CERT_PATH=$(Join-Path $certTarget 'agent.pfx')",
        "FUNNEL_CERT_PASSWORD_FILE=$passwordPath",
        "FUNNEL_CA_PATH=$(Join-Path $certTarget 'ca.crt')",
        "FUNNEL_ALLOWED_CLIENT_SHA256=$clientFingerprints"
      )
      New-ItemProperty -LiteralPath $serviceParameters -Name 'AppEnvironmentExtra' -PropertyType MultiString -Value $serviceEnvironment -Force | Out-Null
    }
    Remove-Item $remoteZip -Force

    Start-Service $serviceName
    Start-Sleep 3
    $svc = Get-Service $serviceName
    Write-Host "Service status: $($svc.Status)"
    if ($svc.Status -ne 'Running') { throw "$serviceName failed to start after update" }
  } -ArgumentList $remoteZip, $InstallPath, $ServiceName, [bool]$CertDir, $env:FUNNEL_CERT_PASSWORD, $clientFingerprints

  Write-Host "Deploy to $ComputerName complete."
}
finally {
  Remove-PSSession $session -ErrorAction SilentlyContinue
  Remove-Item $zip -Force -ErrorAction SilentlyContinue
}
