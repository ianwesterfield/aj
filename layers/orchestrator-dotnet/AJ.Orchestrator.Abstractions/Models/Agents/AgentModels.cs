namespace AJ.Orchestrator.Abstractions.Models.Agents;

/// <summary>
/// Information about a FunnelCloud agent.
/// </summary>
public record AgentInfo
{
  public required string AgentId { get; init; }
  public required string Hostname { get; init; }
  public required string Platform { get; init; }
  public required List<string> Capabilities { get; init; }
  public required int GrpcPort { get; init; }
  public required string IpAddress { get; init; }
}

/// <summary>
/// Response containing discovered agents.
/// </summary>
public record AgentsResponse
{
  public required List<AgentInfo> Agents { get; init; }
}
