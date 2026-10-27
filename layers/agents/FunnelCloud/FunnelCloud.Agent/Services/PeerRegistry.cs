using System.Collections.Concurrent;
using FunnelCloud.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace FunnelCloud.Agent.Services;

/// <summary>
/// Thread-safe registry of known peers with TTL-based expiration.
/// Supports gossip protocol for cross-subnet peer discovery.
/// </summary>
public class PeerRegistry
{
  private readonly ILogger<PeerRegistry> _logger;
  private readonly ConcurrentDictionary<string, PeerEntry> _peers = new();
  private readonly TimeSpan _defaultTtl = TimeSpan.FromMinutes(5);
  private readonly string _selfAgentId;

  public PeerRegistry(ILogger<PeerRegistry> logger, AgentCapabilities selfCapabilities)
  {
    _logger = logger;
    _selfAgentId = selfCapabilities.AgentId;
  }

  /// <summary>
  /// Add or update a peer in the registry.
  /// </summary>
  public void AddOrUpdate(AgentCapabilities peer)
  {
    if (peer.AgentId == _selfAgentId)
      return; // Don't add ourselves

    var entry = new PeerEntry
    {
      Capabilities = peer,
      LastSeen = DateTime.UtcNow,
      ExpiresAt = DateTime.UtcNow.Add(_defaultTtl)
    };

    _peers.AddOrUpdate(peer.AgentId, entry, (_, existing) =>
    {
      // Update if newer or if IP changed
      if (entry.LastSeen > existing.LastSeen ||
              peer.IpAddress != existing.Capabilities.IpAddress)
      {
        _logger.LogDebug("Updated peer {AgentId} at {IP}", peer.AgentId, peer.IpAddress);
        return entry;
      }
      return existing;
    });
  }

  /// <summary>
  /// Add multiple peers from a gossip message.
  /// </summary>
  public int MergeGossip(IEnumerable<AgentCapabilities> peers, string sourceAgentId)
  {
    var added = 0;
    foreach (var peer in peers)
    {
      if (peer.AgentId == _selfAgentId)
        continue;

      var isNew = !_peers.ContainsKey(peer.AgentId);
      AddOrUpdate(peer);
      if (isNew)
      {
        added++;
        _logger.LogInformation("Learned about peer {AgentId} at {IP} via gossip from {Source}",
            peer.AgentId, peer.IpAddress, sourceAgentId);
      }
    }
    return added;
  }

  /// <summary>
  /// Get all known peers (excluding expired).
  /// </summary>
  public List<AgentCapabilities> GetAllPeers()
  {
    var now = DateTime.UtcNow;
    var result = new List<AgentCapabilities>();

    foreach (var kvp in _peers)
    {
      if (kvp.Value.ExpiresAt > now)
      {
        result.Add(kvp.Value.Capabilities);
      }
      else
      {
        // Remove expired
        _peers.TryRemove(kvp.Key, out _);
        _logger.LogDebug("Peer {AgentId} expired and removed", kvp.Key);
      }
    }

    return result;
  }

  /// <summary>
  /// Get peer count.
  /// </summary>
  public int Count => _peers.Count(kvp => kvp.Value.ExpiresAt > DateTime.UtcNow);

  /// <summary>
  /// Check if we know about a specific peer.
  /// </summary>
  public bool Contains(string agentId) =>
      _peers.TryGetValue(agentId, out var entry) && entry.ExpiresAt > DateTime.UtcNow;

  /// <summary>
  /// Get a specific peer if known.
  /// </summary>
  public AgentCapabilities? GetPeer(string agentId)
  {
    if (_peers.TryGetValue(agentId, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
      return entry.Capabilities;
    return null;
  }

  /// <summary>
  /// Refresh TTL for a peer (call when we successfully communicate with them).
  /// </summary>
  public void Touch(string agentId)
  {
    if (_peers.TryGetValue(agentId, out var entry))
    {
      entry.LastSeen = DateTime.UtcNow;
      entry.ExpiresAt = DateTime.UtcNow.Add(_defaultTtl);
    }
  }

  /// <summary>
  /// Remove a peer (e.g., when communication fails).
  /// </summary>
  public void Remove(string agentId)
  {
    if (_peers.TryRemove(agentId, out _))
    {
      _logger.LogDebug("Removed peer {AgentId}", agentId);
    }
  }

  private class PeerEntry
  {
    public required AgentCapabilities Capabilities { get; init; }
    public DateTime LastSeen { get; set; }
    public DateTime ExpiresAt { get; set; }
  }
}
