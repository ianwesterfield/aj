<#
.SYNOPSIS
    Push-model deploy of the FunnelCloud agent to a Linux client over SSH (key auth).
    Mirrors scripts/deploy-linux-agents.py but runs non-interactively on a build agent.
    Requires scoped NOPASSWD sudoers on the target (/etc/sudoers.d/funnelcloud-deploy).
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string]$TargetHost,
  [Parameter(Mandatory)] [string]$AgentId,
  [Parameter(Mandatory)] [string]$SshKeyPath,
  [Parameter(Mandatory)] [string]$SourceDir,
  [string]$CertDir,
  [string]$SshUser = 'ian',
  [string]$InstallPath = '/opt/funnelcloud'
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:FUNNEL_CERT_PASSWORD)) {
  throw 'Set FUNNEL_CERT_PASSWORD before deploying an agent.'
}
if ([string]::IsNullOrWhiteSpace($env:FUNNEL_ALLOWED_CLIENT_SHA256)) {
  throw 'Set FUNNEL_ALLOWED_CLIENT_SHA256 before deploying an agent.'
}
$clientFingerprints = @($env:FUNNEL_ALLOWED_CLIENT_SHA256 -split ',' | ForEach-Object { $_.Trim() })
foreach ($fingerprint in $clientFingerprints) {
  if (($fingerprint -replace '(?i)^SHA256:', '' -replace ':', '') -notmatch '^[A-Fa-f0-9]{64}$') {
    throw 'FUNNEL_ALLOWED_CLIENT_SHA256 contains an invalid fingerprint.'
  }
}
$clientFingerprints = $clientFingerprints -join ','
foreach ($name in @('agent.pfx', 'ca.crt')) {
  if (-not $CertDir -or -not (Test-Path -LiteralPath (Join-Path $CertDir $name))) {
    throw "Certificate directory must contain $name."
  }
}

# Windows OpenSSH refuses keys readable by other accounts; secure-file downloads are world-inherited
$me = (whoami)
icacls $SshKeyPath /inheritance:r /grant:r "${me}:R" | Out-Null

$knownHosts = Join-Path ([IO.Path]::GetTempPath()) 'fc_known_hosts'
$sshArgs = @('-i', $SshKeyPath, '-o', 'StrictHostKeyChecking=accept-new', '-o', "UserKnownHostsFile=$knownHosts", '-o', 'BatchMode=yes')
$dest = "$SshUser@$TargetHost"

function Invoke-Remote([string]$cmd) {
  $out = ssh @sshArgs $dest $cmd 2>&1
  if ($LASTEXITCODE -ne 0) { throw "ssh failed ($LASTEXITCODE): $cmd`n$out" }
  $out
}

# Stage: single tarball beats 500 small scp transfers
$tarball = Join-Path ([IO.Path]::GetTempPath()) 'fc-stage.tgz'
tar -czf $tarball -C $SourceDir .
if ($LASTEXITCODE -ne 0) { throw 'tar failed' }
Write-Host "Package: $((Get-Item $tarball).Length / 1MB -as [int]) MB"

Invoke-Remote 'rm -rf /tmp/funnelcloud-stage /tmp/fc-stage.tgz && mkdir -m 700 /tmp/funnelcloud-stage'
scp @sshArgs $tarball "${dest}:/tmp/fc-stage.tgz"
if ($LASTEXITCODE -ne 0) { throw 'scp failed' }
Invoke-Remote 'tar -xzf /tmp/fc-stage.tgz -C /tmp/funnelcloud-stage && rm /tmp/fc-stage.tgz && chmod +x /tmp/funnelcloud-stage/FunnelCloud.Agent'

Invoke-Remote 'mkdir -m 700 /tmp/funnelcloud-stage/Certificates'
foreach ($name in @('agent.pfx', 'ca.crt')) {
  scp @sshArgs (Join-Path $CertDir $name) "${dest}:/tmp/funnelcloud-stage/Certificates/$name"
  if ($LASTEXITCODE -ne 0) { throw "scp failed: $name" }
}
$passwordPath = Join-Path ([IO.Path]::GetTempPath()) "fc-pfx-password-$([guid]::NewGuid().ToString('N'))"
try {
  [IO.File]::WriteAllText($passwordPath, $env:FUNNEL_CERT_PASSWORD, [Text.UTF8Encoding]::new($false))
  scp @sshArgs $passwordPath "${dest}:/tmp/funnelcloud-stage/Certificates/pfx-password"
  if ($LASTEXITCODE -ne 0) { throw 'scp failed: certificate password' }
}
finally {
  Remove-Item -LiteralPath $passwordPath -Force -ErrorAction SilentlyContinue
}
Invoke-Remote 'chmod 600 /tmp/funnelcloud-stage/Certificates/agent.pfx /tmp/funnelcloud-stage/Certificates/pfx-password'

$service = @"
[Unit]
Description=FunnelCloud Agent - $AgentId
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$SshUser
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
"@

# Exact command forms below must match /etc/sudoers.d/funnelcloud-deploy
Invoke-Remote 'sudo /usr/bin/systemctl stop funnelcloud-agent || true'
$service -replace "`r" | ssh @sshArgs $dest 'sudo /usr/bin/tee /etc/systemd/system/funnelcloud-agent.service > /dev/null'
if ($LASTEXITCODE -ne 0) { throw 'service file write failed' }
Invoke-Remote 'sudo /bin/mkdir -p /opt/funnelcloud'
Invoke-Remote 'sudo /bin/cp -r /tmp/funnelcloud-stage/. /opt/funnelcloud/'
Invoke-Remote "sudo /bin/chown -R ${SshUser}:${SshUser} /opt/funnelcloud"
Invoke-Remote 'sudo /usr/bin/systemctl daemon-reload'
Invoke-Remote 'sudo /usr/bin/systemctl enable funnelcloud-agent'
Invoke-Remote 'sudo /usr/bin/systemctl restart funnelcloud-agent'
Invoke-Remote 'rm -rf /tmp/funnelcloud-stage'

Start-Sleep 3
$status = Invoke-Remote 'systemctl is-active funnelcloud-agent || true'
Write-Host "Service status: $status"
if ($status -notmatch 'active') { throw "funnelcloud-agent not active on $TargetHost" }
Write-Host "Deploy to $TargetHost complete."
