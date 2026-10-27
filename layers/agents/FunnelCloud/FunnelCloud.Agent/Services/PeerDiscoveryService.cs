using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FunnelCloud.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace FunnelCloud.Agent.Services;

/// <summary>
/// Service to discover peer FunnelCloud agents via UDP multicast.
/// 
/// Used by the orchestrator (via HTTP API on local agent) to discover
/// all agents on the network when multicast doesn't work from the
/// orchestrator's network (e.g., WSL NAT, Docker bridge).
/// 
/// Protocol:
/// 1. Send "FUNNEL_DISCOVER" to multicast group 239.255.77.77:41420
/// 2. Collect responses for timeout duration
/// 3. Return list of discovered AgentCapabilities
/// </summary>
public class PeerDiscoveryService
{
  private readonly ILogger<PeerDiscoveryService> _logger;
  private readonly AgentCapabilities _selfCapabilities;

  public PeerDiscoveryService(
      ILogger<PeerDiscoveryService> logger,
      AgentCapabilities selfCapabilities)
  {
    _logger = logger;
    _selfCapabilities = selfCapabilities;
  }

  /// <summary>
  /// Discover all FunnelCloud agents on the network via multicast.
  /// </summary>
  /// <param name="timeoutSeconds">How long to wait for responses</param>
  /// <returns>List of discovered agent capabilities (including self)</returns>
  public async Task<List<AgentCapabilities>> DiscoverPeersAsync(double timeoutSeconds = 2.0)
  {
    var agents = new List<AgentCapabilities>();
    var seenIds = new HashSet<string>();

    _logger.LogInformation("Starting peer discovery via multicast group {MulticastGroup}:{Port}",
        TrustConfig.MulticastGroup, TrustConfig.DiscoveryPort);

    try
    {
      using var udpClient = new UdpClient();

      // Allow address reuse
      udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

      // Bind to any available port for receiving responses
      udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
      var localPort = ((IPEndPoint)udpClient.Client.LocalEndPoint!).Port;
      _logger.LogDebug("Discovery socket bound to port {Port}", localPort);

      // Set multicast TTL for cross-subnet discovery
      udpClient.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 32);

      // Send discovery message to multicast group on ALL interfaces
      var message = Encoding.UTF8.GetBytes(TrustConfig.DiscoveryMagic);
      var multicastEndpoint = new IPEndPoint(IPAddress.Parse(TrustConfig.MulticastGroup), TrustConfig.DiscoveryPort);

      // Send multicast on each network interface for cross-subnet discovery
      var sentCount = 0;
      foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
      {
        if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
          continue;
        if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
          continue;
        if (!ni.SupportsMulticast)
          continue;

        var ipProps = ni.GetIPProperties();
        foreach (var addr in ipProps.UnicastAddresses)
        {
          if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
            continue;

          try
          {
            // Set the outgoing interface for this multicast send
            udpClient.Client.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.MulticastInterface,
                addr.Address.GetAddressBytes());

            await udpClient.SendAsync(message, message.Length, multicastEndpoint);
            sentCount++;
            _logger.LogDebug("Sent discovery multicast via {Interface} ({IP})", ni.Name, addr.Address);
          }
          catch (Exception ex)
          {
            _logger.LogDebug("Failed to send multicast via {Interface}: {Error}", ni.Name, ex.Message);
          }
        }
      }

      if (sentCount == 0)
      {
        // Fallback to default interface
        await udpClient.SendAsync(message, message.Length, multicastEndpoint);
        _logger.LogDebug("Sent discovery multicast via default interface");
      }
      else
      {
        _logger.LogDebug("Sent discovery multicast on {Count} interface(s)", sentCount);
      }

      // Also send to broadcast for same-subnet discovery (backup)
      try
      {
        var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, TrustConfig.DiscoveryPort);
        udpClient.EnableBroadcast = true;
        await udpClient.SendAsync(message, message.Length, broadcastEndpoint);
        _logger.LogDebug("Sent discovery broadcast to {Endpoint}", broadcastEndpoint);
      }
      catch (Exception ex)
      {
        _logger.LogDebug("Broadcast failed (expected on some networks): {Error}", ex.Message);
      }

      // Collect responses with timeout
      var endTime = DateTime.UtcNow.AddSeconds(timeoutSeconds);
      udpClient.Client.ReceiveTimeout = (int)(timeoutSeconds * 1000);

      while (DateTime.UtcNow < endTime)
      {
        try
        {
          var remainingMs = (int)(endTime - DateTime.UtcNow).TotalMilliseconds;

          if (remainingMs <= 0) break;

          udpClient.Client.ReceiveTimeout = Math.Min(500, remainingMs);

          var result = await Task.Run<(byte[]? Data, IPEndPoint? RemoteEndPoint)>(() =>
          {
            try
            {
              IPEndPoint? remoteEp = null;
              var data = udpClient.Receive(ref remoteEp);
              return (Data: data, RemoteEndPoint: remoteEp);
            }
            catch (SocketException)
            {
              return (Data: null, RemoteEndPoint: null);
            }
          });

          if (result.Data == null || result.RemoteEndPoint == null) continue;

          try
          {
            var json = Encoding.UTF8.GetString(result.Data);
            var agent = JsonSerializer.Deserialize<AgentCapabilities>(json, new JsonSerializerOptions
            {
              PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            if (agent != null && !seenIds.Contains(agent.AgentId))
            {
              // Create a new agent with the IP address from the UDP response
              var agentWithIp = agent with { IpAddress = result.RemoteEndPoint.Address.ToString() };
              seenIds.Add(agentWithIp.AgentId);
              agents.Add(agentWithIp);
              _logger.LogInformation("Discovered peer: {AgentId} at {IP} ({Platform})",
                  agentWithIp.AgentId, agentWithIp.IpAddress, agentWithIp.Platform);
            }
          }
          catch (JsonException ex)
          {
            _logger.LogDebug("Invalid JSON from {RemoteEndPoint}: {Error}",
                result.RemoteEndPoint, ex.Message);
          }
        }
        catch (SocketException)
        {
          // Timeout, continue
        }
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Peer discovery failed");
    }

    // Always include self in the results with our local IP
    if (!seenIds.Contains(_selfCapabilities.AgentId))
    {
      // Get our best local IP for other agents to reach us
      var selfIp = GetLocalIpAddress();
      var selfWithIp = _selfCapabilities with { IpAddress = selfIp };
      agents.Add(selfWithIp);
      _logger.LogDebug("Added self to discovery results: {AgentId} at {IP}",
          _selfCapabilities.AgentId, selfIp);
    }

    _logger.LogInformation("Peer discovery complete: found {Count} agent(s)", agents.Count);
    return agents;
  }

  /// <summary>
  /// Get the best local IP address for external connections.
  /// Prefers non-loopback IPv4 addresses that other hosts can reach.
  /// </summary>
  private string GetLocalIpAddress()
  {
    try
    {
      // Get all IPv4 addresses, prefer non-loopback
      var addresses = Dns.GetHostAddresses(Dns.GetHostName())
          .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
          .Where(a => !IPAddress.IsLoopback(a))
          .ToList();

      // If we have multiple addresses, prefer ones that look like LAN addresses
      // (192.168.x.x, 10.x.x.x, 172.16-31.x.x)
      var lanAddress = addresses.FirstOrDefault(a =>
      {
        var bytes = a.GetAddressBytes();
        return bytes[0] == 192 && bytes[1] == 168  // 192.168.x.x
            || bytes[0] == 10                       // 10.x.x.x
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31);  // 172.16-31.x.x
      });

      if (lanAddress != null)
        return lanAddress.ToString();

      // Fall back to first non-loopback address
      if (addresses.Any())
        return addresses.First().ToString();

      return "127.0.0.1";
    }
    catch
    {
      return "127.0.0.1";
    }
  }
}
