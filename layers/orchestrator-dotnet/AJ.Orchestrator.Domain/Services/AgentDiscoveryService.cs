using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using AJ.Orchestrator.Abstractions.Services;
using AJ.Shared.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AJ.Orchestrator.Domain.Services;

/// <summary>
/// Response envelope from FunnelCloud /discover-peers endpoint.
/// </summary>
internal record DiscoverPeersResponse
{
  public List<AgentCapabilities> Agents { get; init; } = new();
  public int Count { get; init; }
  public string? DiscoveredBy { get; init; }
  public int FreshCount { get; init; }
  public int CachedCount { get; init; }
}

/// <summary>
/// Service for discovering FunnelCloud agents via the local agent's HTTP API.
/// Supports cross-subnet discovery via a configurable gossip seed host.
/// </summary>
public class AgentDiscoveryService : IAgentDiscovery
{
  private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

  private readonly IHttpClientFactory _httpClientFactory;
  private readonly ILogger<AgentDiscoveryService> _logger;
  private readonly ConcurrentDictionary<string, AgentCapabilities> _agents = new();
  private readonly string? _gossipSeedHost;

  public AgentDiscoveryService(
      IHttpClientFactory httpClientFactory,
      IConfiguration configuration,
      ILogger<AgentDiscoveryService> logger)
  {
    _httpClientFactory = httpClientFactory;
    _logger = logger;
    _gossipSeedHost = configuration["FunnelCloud:GossipSeedHost"];
  }

  public async Task<List<AgentCapabilities>> DiscoverAgentsAsync(CancellationToken ct = default)
  {
    // Clear cache - always perform fresh discovery
    _agents.Clear();
    
    // Step 1: Query the local FunnelCloud agent
    await QueryDiscoverPeersAsync("funnel", ct);

    // Step 2: Bootstrap cross-subnet by adding seed to local agent's registry
    if (!string.IsNullOrEmpty(_gossipSeedHost))
    {
      await BootstrapCrossSubnetAsync(ct);
      // Step 3: Query the seed host directly for its full peer list
      await QueryDiscoverPeersAsync("funnel-seed", ct);
    }

    _logger.LogInformation("Discovery complete: {Count} agent(s) (fresh)", _agents.Count);
    return _agents.Values.ToList();
  }

  private async Task QueryDiscoverPeersAsync(string clientName, CancellationToken ct)
  {
    try
    {
      var client = _httpClientFactory.CreateClient(clientName);
      client.Timeout = TimeSpan.FromSeconds(5);

      var response = await client.GetAsync("discover-peers?timeout=3", ct);
      if (!response.IsSuccessStatusCode)
      {
        _logger.LogWarning("Discovery via {Client} returned {StatusCode}", clientName, response.StatusCode);
        return;
      }

      var result = await response.Content.ReadFromJsonAsync<DiscoverPeersResponse>(s_jsonOptions, ct);
      if (result?.Agents == null) return;

      foreach (var peer in result.Agents)
      {
        _agents[peer.AgentId] = peer;
      }

      _logger.LogInformation("Discovered {Count} agents via {Client} ({Fresh} fresh, {Cached} cached)",
          result.Count, clientName, result.FreshCount, result.CachedCount);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to discover agents via {Client}", clientName);
    }
  }

  private async Task BootstrapCrossSubnetAsync(CancellationToken ct)
  {
    try
    {
      var client = _httpClientFactory.CreateClient("funnel");
      client.Timeout = TimeSpan.FromSeconds(5);

      var response = await client.PostAsync($"add-peer?ip={_gossipSeedHost}", null, ct);
      if (!response.IsSuccessStatusCode)
      {
        _logger.LogWarning("Cross-subnet bootstrap returned {StatusCode}", response.StatusCode);
        return;
      }

      _logger.LogInformation("Bootstrapped cross-subnet peer at {SeedHost}", _gossipSeedHost);
    }
    catch (Exception ex)
    {
      _logger.LogDebug(ex, "Cross-subnet bootstrap failed (agent may not support /add-peer)");
    }
  }

  public AgentCapabilities? GetAgent(string agentId)
  {
    return _agents.TryGetValue(agentId, out var agent) ? agent : null;
  }

  public IReadOnlyList<AgentCapabilities> GetAllAgents()
  {
    return _agents.Values.ToList();
  }

  public Task RefreshAsync(CancellationToken ct = default)
  {
    return DiscoverAgentsAsync(ct);
  }
}
