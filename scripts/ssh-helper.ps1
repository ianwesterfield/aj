# SSH Helper - Uses stored password from secrets/ssh.txt
# Usage: .\ssh-helper.ps1 -Host postfix01 -Command "uname -a"
#        .\ssh-helper.ps1 -Host plex01 -Scp -LocalPath ".\file.txt" -RemotePath "/tmp/"

[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$TargetHost,
  [string]$User = "ian",
  [string]$Command,
  [switch]$Scp,
  [string]$LocalPath,
  [string]$RemotePath,
  [switch]$Recurse
)

$ErrorActionPreference = "Stop"
$secretsPath = Join-Path $PSScriptRoot "..\secrets\ssh.txt"
$password = (Get-Content $secretsPath -Raw).Trim()

# Check if SSH key auth works first
$keyTest = ssh -o BatchMode=yes -o ConnectTimeout=3 "${User}@${TargetHost}" "echo ok" 2>$null
if ($keyTest -eq "ok") {
  Write-Host "[SSH] Using key authentication" -ForegroundColor Green
  if ($Scp) {
    $args = @("-o", "StrictHostKeyChecking=no")
    if ($Recurse) { $args += "-r" }
    $args += @($LocalPath, "${User}@${TargetHost}:${RemotePath}")
    scp @args
  }
  else {
    ssh -o StrictHostKeyChecking=no "${User}@${TargetHost}" $Command
  }
  return
}

# Fall back to password via expect in WSL
Write-Host "[SSH] Using password authentication via WSL expect" -ForegroundColor Yellow

# Ensure expect is installed
$hasExpect = wsl -e which expect 2>$null
if (-not $hasExpect) {
  Write-Host "[SSH] Installing expect in WSL..." -ForegroundColor Cyan
  wsl -e bash -c "sudo apt-get update && sudo apt-get install -y expect" 2>$null
}

if ($Scp) {
  $recurseFlag = if ($Recurse) { "-r" } else { "" }
  $wslLocalPath = wsl -e wslpath -a $LocalPath
  $expectScript = @"
spawn scp -o StrictHostKeyChecking=no $recurseFlag $wslLocalPath ${User}@${TargetHost}:${RemotePath}
expect {
    "password:" { send "$password\r"; exp_continue }
    eof
}
"@
}
else {
  $expectScript = @"
spawn ssh -o StrictHostKeyChecking=no ${User}@${TargetHost} "$Command"
expect {
    "password:" { send "$password\r"; exp_continue }
    eof
}
"@
}

$expectScript | wsl -e expect -
