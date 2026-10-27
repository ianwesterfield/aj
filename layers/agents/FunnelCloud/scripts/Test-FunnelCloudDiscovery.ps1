# Test FunnelCloud Discovery on r730xd (Multicast)
param(
  [string]$ComputerName = "r730xd",
  [string]$MulticastGroup = "239.255.77.77",
  [int]$Port = 41420
)

Write-Host "=== Checking firewall rule ===" -ForegroundColor Cyan
Invoke-Command -ComputerName $ComputerName -ScriptBlock {
  $rule = Get-NetFirewallRule -DisplayName "FunnelCloud Discovery" -ErrorAction SilentlyContinue
  if (-not $rule) {
    New-NetFirewallRule -DisplayName "FunnelCloud Discovery" -Direction Inbound -Protocol UDP -LocalPort 41420 -Action Allow
    Write-Host "Created firewall rule" -ForegroundColor Green
  }
  else {
    Write-Host "Rule exists: Enabled=$($rule.Enabled)" -ForegroundColor Green
  }
}

Write-Host "`n=== Testing local discovery on $ComputerName ===" -ForegroundColor Cyan
Invoke-Command -ComputerName $ComputerName -ScriptBlock {
  param($Port)
  $udp = New-Object System.Net.Sockets.UdpClient
  $udp.Client.ReceiveTimeout = 2000
  $ep = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Loopback, $Port)
  $msg = [Text.Encoding]::UTF8.GetBytes("FUNNEL_DISCOVER")
  [void]$udp.Send($msg, $msg.Length, $ep)
  try { 
    $r = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
    $response = [Text.Encoding]::UTF8.GetString($udp.Receive([ref]$r))
    Write-Host "Local discovery WORKS!" -ForegroundColor Green
    Write-Host $response
  }
  catch { 
    Write-Host "No local response: $_" -ForegroundColor Red
  }
  $udp.Close()
} -ArgumentList $Port

Write-Host "`n=== Testing multicast discovery ===" -ForegroundColor Cyan
Write-Host "Multicast group: $MulticastGroup`:$Port"

try {
  $udp = New-Object System.Net.Sockets.UdpClient
  $udp.Client.ReceiveTimeout = 3000
  
  # Set multicast TTL to allow crossing routers (32 = site-local scope)
  $udp.Client.SetSocketOption(
    [System.Net.Sockets.SocketOptionLevel]::IP,
    [System.Net.Sockets.SocketOptionName]::MulticastTimeToLive,
    32
  )
  
  # Send to multicast group
  $ep = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse($MulticastGroup), $Port)
  $msg = [Text.Encoding]::UTF8.GetBytes("FUNNEL_DISCOVER")
  [void]$udp.Send($msg, $msg.Length, $ep)
  Write-Host "Sent discovery to multicast group ${MulticastGroup}:${Port}..."
  
  # Wait for responses
  $r = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
  $response = [Text.Encoding]::UTF8.GetString($udp.Receive([ref]$r))
  Write-Host "Multicast discovery WORKS!" -ForegroundColor Green
  Write-Host "Response from: $($r.Address):$($r.Port)"
  Write-Host $response
}
catch { 
  Write-Host "No multicast response: $_" -ForegroundColor Yellow
  Write-Host "Note: Multicast requires IGMP snooping/PIM on network equipment for cross-VLAN" -ForegroundColor Gray
}
finally {
  if ($udp) { $udp.Close() }
}

Write-Host "`n=== Testing direct unicast discovery ===" -ForegroundColor Cyan
$targetIP = (Resolve-DnsName $ComputerName | Where-Object { $_.Type -eq 'A' } | Select-Object -First 1).IPAddress
Write-Host "Target IP: $targetIP"

$udp = New-Object System.Net.Sockets.UdpClient
$udp.Client.ReceiveTimeout = 3000
$ep = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse($targetIP), $Port)
$msg = [Text.Encoding]::UTF8.GetBytes("FUNNEL_DISCOVER")
[void]$udp.Send($msg, $msg.Length, $ep)
Write-Host "Sent direct discovery to ${targetIP}:${Port}..."
try { 
  $r = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
  $response = [Text.Encoding]::UTF8.GetString($udp.Receive([ref]$r))
  Write-Host "Direct unicast discovery WORKS!" -ForegroundColor Green
  Write-Host $response
}
catch { 
  Write-Host "No direct response - check firewall" -ForegroundColor Yellow
}
$udp.Close()
