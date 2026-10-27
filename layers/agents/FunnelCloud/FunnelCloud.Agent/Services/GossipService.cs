using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FunnelCloud.Shared.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FunnelCloud.Agent.Services;

/// <summary>
/// Background service that periodically gossips known peers to other agents.
/// This enables cross-subnet peer discovery by sharing information between
/// agents that can reach each other via unicast.
/// </summary>
public class GossipService : BackgroundService
{
  private readonly ILogger<GossipService> _logger;
  private readonly PeerRegistry _peerRegistry;
  private readonly PeerDiscoveryService _peerDiscovery;
  private readonly AgentCapabilities _selfCapabilities;
  private readonly TimeSpan _gossipInterval = TimeSpan.FromSeconds(30);
  private readonly int _discoveryPort;

  private const string GossipPrefix = "FUNNEL_GOSSIP:";

  public GossipService(
      ILogger<GossipService> logger,
      PeerRegistry peerRegistry,
      PeerDiscoveryService peerDiscovery,
      AgentCapabilities selfCapabilities)
  {
    _logger = logger;
    _peerRegistry = peerRegistry;
    _peerDiscovery = peerDiscovery;
    _selfCapabilities = selfCapabilities;
    _discoveryPort = TrustConfig.DiscoveryPort;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    _logger.LogInformation("Gossip service starting with {Interval}s interval", _gossipInterval.TotalSeconds);

    // Initial delay to let discovery listener start
    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

    // Do initial multicast discovery to populate registry
    await DoInitialDiscovery(stoppingToken);

    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await GossipToKnownPeers(stoppingToken);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        break;
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Gossip round failed");
      }

      await Task.Delay(_gossipInterval, stoppingToken);
    }

    _logger.LogInformation("Gossip service stopped");
  }

  private async Task DoInitialDiscovery(CancellationToken ct)
  {
    try
    {
      _logger.LogInformation("Performing initial peer discovery...");
      var peers = await _peerDiscovery.DiscoverPeersAsync(3.0);

      foreach (var peer in peers)
      {
        _peerRegistry.AddOrUpdate(peer);
      }

      _logger.LogInformation("Initial discovery found {Count} peer(s)", peers.Count);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Initial discovery failed");
    }
  }

  private async Task GossipToKnownPeers(CancellationToken ct)
  {
    var peers = _peerRegistry.GetAllPeers();
    if (peers.Count == 0)
    {
      _logger.LogDebug("No known peers to gossip with");
      return;
    }

    // Build gossip payload - include all known peers + ourselves
    var allPeers = new List<AgentCapabilities>(peers) { _selfCapabilities };
    var payload = new
    {
      sourceAgentId = _selfCapabilities.AgentId,
      peers = allPeers
    };

    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });
    var message = GossipPrefix + json;
    var messageBytes = Encoding.UTF8.GetBytes(message);

    // UDP limit check
    if (messageBytes.Length > 60000)
    {
      _logger.LogWarning("Gossip payload too large ({Size} bytes), skipping", messageBytes.Length);
      return;
    }

    using var udpClient = new UdpClient();
    var gossipedTo = 0;

    foreach (var peer in peers)
    {
      if (string.IsNullOrEmpty(peer.IpAddress))
        continue;

      try
      {
        var endpoint = new IPEndPoint(IPAddress.Parse(peer.IpAddress), _discoveryPort);
        await udpClient.SendAsync(messageBytes, messageBytes.Length, endpoint);
        gossipedTo++;
        _logger.LogDebug("Gossiped to {AgentId} at {IP}", peer.AgentId, peer.IpAddress);
      }
      catch (Exception ex)
      {
        _logger.LogDebug("Failed to gossip to {AgentId}: {Error}", peer.AgentId, ex.Message);
      }
    }

    if (gossipedTo > 0)
    {
      _logger.LogDebug("Gossiped {PeerCount} peers to {GossipedTo} agent(s)", allPeers.Count, gossipedTo);
    }
  }
}
