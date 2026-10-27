namespace AJ.Orchestrator.Abstractions.Models.Infrastructure;

/// <summary>
/// Health check response.
/// </summary>
public record HealthResponse
{
  public required string Status { get; init; }
  public required string Service { get; init; }
  public required string Version { get; init; }
}
