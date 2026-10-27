using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FunnelCloud.Shared.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FunnelCloud.Agent.Services;

/// <summary>
/// UDP listener that responds to FunnelCloud discovery messages.
/// 
/// Protocol:
/// 1. "FUNNEL_DISCOVER" - Agent responds with its own capabilities
/// 2. "FUNNEL_DISCOVER_PEERS" - Agent does multicast discovery and returns ALL found agents
/// 3. "FUNNEL_GOSSIP:{json}" - Receive peer list from another agent (gossip protocol)
/// 
/// The DISCOVER_PEERS command allows the orchestrator to use any reachable agent
/// as a discovery proxy when multicast doesn't work from its network.
/// 
/// Gossip protocol enables cross-subnet peer discovery:
/// - Agents share their known peers with each other
/// - Peers are cached with TTL for fast lookups
/// - Discovery returns both cached + fresh multicast results
/// </summary>
public class DiscoveryListener : BackgroundService
{
  private readonly ILogger<DiscoveryListener> _logger;
  private readonly AgentCapabilities _capabilities;
  private readonly PeerDiscoveryService _peerDiscovery;
  private readonly PeerRegistry _peerRegistry;
  private readonly int _port;
  private readonly string _multicastGroup;

  private const string GossipPrefix = "FUNNEL_GOSSIP:";

  public DiscoveryListener(
      ILogger<DiscoveryListener> logger,
      AgentCapabilities capabilities,
      PeerDiscoveryService peerDiscovery,
      PeerRegistry peerRegistry,
      int? port = null)
  {
    _logger = logger;
    _capabilities = capabilities;
    _peerDiscovery = peerDiscovery;
    _peerRegistry = peerRegistry;
    _port = port ?? TrustConfig.DiscoveryPort;
    _multicastGroup = TrustConfig.MulticastGroup;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    _logger.LogInformation(
        "Discovery listener starting on UDP port {Port}, multicast group {MulticastGroup} for agent {AgentId}",
        _port, _multicastGroup, _capabilities.AgentId);

    using var udpClient = new UdpClient();

    // Allow address reuse for multiple listeners on same machine
    udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

    // Bind to the discovery port on all interfaces
    udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, _port));

    // Join the multicast group on ALL network interfaces for cross-subnet discovery
    var multicastAddress = IPAddress.Parse(_multicastGroup);
    var joinedInterfaces = 0;

    try
    {
      // Get all network interfaces and join multicast on each
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
            // Join multicast group on this specific interface
            udpClient.JoinMulticastGroup(multicastAddress, addr.Address);
            joinedInterfaces++;
            _logger.LogInformation("Joined multicast group {MulticastGroup} on interface {Interface} ({IP})",
                _multicastGroup, ni.Name, addr.Address);
          }
          catch (Exception ex)
          {
            _logger.LogDebug("Failed to join multicast on {Interface} ({IP}): {Error}",
                ni.Name, addr.Address, ex.Message);
          }
        }
      }

      if (joinedInterfaces == 0)
      {
        // Fallback to default interface
        udpClient.JoinMulticastGroup(multicastAddress);
        _logger.LogWarning("No interfaces joined, falling back to default multicast join");
      }
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to join multicast group {MulticastGroup}, falling back to unicast only", _multicastGroup);
    }

    // Also accept direct unicast discovery (for Docker host.docker.internal)
    _logger.LogInformation("Also listening for direct unicast discovery on port {Port}", _port);

    try
    {
      while (!stoppingToken.IsCancellationRequested)
      {
        try
        {
          // Wait for incoming message (multicast or unicast)
          var result = await udpClient.ReceiveAsync(stoppingToken);
          var message = Encoding.UTF8.GetString(result.Buffer);

          _logger.LogDebug(
              "Received UDP message from {RemoteEndPoint}: {Message}",
              result.RemoteEndPoint, message);

          // Check for discovery commands (order matters - check longer prefixes first)
          if (message.StartsWith("FUNNEL_DISCOVER_PEERS"))
          {
            // Peer discovery proxy request - do multicast and return all agents
            await HandlePeerDiscoveryRequest(udpClient, result.RemoteEndPoint);
          }
          else if (message.StartsWith(GossipPrefix))
          {
            // Gossip message - merge peer list into our registry
            HandleGossipMessage(message, result.RemoteEndPoint);
          }
          else if (message.StartsWith(TrustConfig.DiscoveryMagic))
          {
            // Standard self-discovery request
            await HandleDiscoveryRequest(udpClient, result.RemoteEndPoint, message);
          }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
          break;
        }
        catch (SocketException ex)
        {
          _logger.LogWarning(ex, "Socket error in discovery listener");
          await Task.Delay(1000, stoppingToken); // Brief pause before retry
        }
      }
    }
    finally
    {
      // Leave multicast group on shutdown
      try
      {
        var multicastAddr = IPAddress.Parse(_multicastGroup);
        udpClient.DropMulticastGroup(multicastAddr);
      }
      catch { /* ignore cleanup errors */ }

      _logger.LogInformation("Discovery listener stopped");
    }
  }

  private async Task HandleDiscoveryRequest(UdpClient client, IPEndPoint remoteEndPoint, string message)
  {
    _logger.LogInformation(
        "Discovery request from {RemoteEndPoint}",
        remoteEndPoint);

    try
    {
      // Serialize capabilities to JSON
      var response = JsonSerializer.Serialize(_capabilities, new JsonSerializerOptions
      {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
      });

      var responseBytes = Encoding.UTF8.GetBytes(response);

      // Send response back to the requester (unicast)
      await client.SendAsync(responseBytes, responseBytes.Length, remoteEndPoint);

      _logger.LogDebug(
          "Sent discovery response to {RemoteEndPoint}: {AgentId}",
          remoteEndPoint, _capabilities.AgentId);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to send discovery response to {RemoteEndPoint}", remoteEndPoint);
    }
  }

  private async Task HandlePeerDiscoveryRequest(UdpClient client, IPEndPoint remoteEndPoint)
  {
    _logger.LogInformation(
        "Peer discovery proxy request from {RemoteEndPoint}",
        remoteEndPoint);

    try
    {
      // Do multicast discovery to find fresh peers
      var freshPeers = await _peerDiscovery.DiscoverPeersAsync(2.0);

      // Add fresh peers to our registry
      foreach (var peer in freshPeers)
      {
        _peerRegistry.AddOrUpdate(peer);
      }

      // Get ALL known peers (fresh + cached from gossip)
      var allPeers = _peerRegistry.GetAllPeers();

      // Also include ourselves
      if (!allPeers.Any(p => p.AgentId == _capabilities.AgentId))
      {
        allPeers.Insert(0, _capabilities);
      }

      _logger.LogInformation("Peer discovery: {Fresh} fresh + {Cached} cached = {Total} total agent(s)",
          freshPeers.Count, allPeers.Count - freshPeers.Count, allPeers.Count);

      // Build response with all discovered agents
      var response = new
      {
        discoveredBy = _capabilities.AgentId,
        agents = allPeers,
        count = allPeers.Count
      };

      var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
      {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
      });

      var responseBytes = Encoding.UTF8.GetBytes(json);

      // UDP datagram size limit is ~65KB, but practical limit is ~1400 bytes for MTU
      // For large responses, we may need to split - but for typical agent counts this is fine
      if (responseBytes.Length > 60000)
      {
        _logger.LogWarning("Peer discovery response is very large ({Size} bytes), may be truncated", responseBytes.Length);
      }

      await client.SendAsync(responseBytes, responseBytes.Length, remoteEndPoint);

      _logger.LogDebug(
          "Sent peer discovery response to {RemoteEndPoint}: {Count} agents",
          remoteEndPoint, allPeers.Count);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to handle peer discovery request from {RemoteEndPoint}", remoteEndPoint);
    }
  }

  private void HandleGossipMessage(string message, IPEndPoint remoteEndPoint)
  {
    try
    {
      // Extract JSON payload after prefix
      var json = message.Substring(GossipPrefix.Length);
      var gossip = JsonSerializer.Deserialize<GossipPayload>(json, new JsonSerializerOptions
      {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
      });

      if (gossip?.Peers == null || gossip.Peers.Count == 0)
      {
        _logger.LogDebug("Received empty gossip from {RemoteEndPoint}", remoteEndPoint);
        return;
      }

      var added = _peerRegistry.MergeGossip(gossip.Peers, gossip.SourceAgentId ?? "unknown");

      _logger.LogInformation(
          "Received gossip from {Source} ({RemoteEndPoint}): {Received} peers, {Added} new",
          gossip.SourceAgentId, remoteEndPoint, gossip.Peers.Count, added);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to process gossip from {RemoteEndPoint}", remoteEndPoint);
    }
  }

  private class GossipPayload
  {
    public string? SourceAgentId { get; set; }
    public List<AgentCapabilities> Peers { get; set; } = new();
  }
}
