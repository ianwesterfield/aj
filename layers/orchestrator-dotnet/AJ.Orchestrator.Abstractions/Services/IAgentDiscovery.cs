using AJ.Shared.Contracts;

namespace AJ.Orchestrator.Abstractions.Services;

/// <summary>
/// Service for discovering FunnelCloud agents on the network.
/// </summary>
public interface IAgentDiscovery
{
  /// <summary>
  /// Discover all agents via multicast/gossip.
  /// </summary>
  Task<List<AgentCapabilities>> DiscoverAgentsAsync(CancellationToken ct = default);

  /// <summary>
  /// Get a specific agent by ID.
  /// </summary>
  AgentCapabilities? GetAgent(string agentId);

  /// <summary>
  /// Get all known agents (cached).
  /// </summary>
  IReadOnlyList<AgentCapabilities> GetAllAgents();

  /// <summary>
  /// Refresh the agent list.
  /// </summary>
  Task RefreshAsync(CancellationToken ct = default);
}
