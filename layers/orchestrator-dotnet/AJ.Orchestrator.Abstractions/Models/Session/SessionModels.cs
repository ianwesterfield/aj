using System.Text.Json.Serialization;

namespace AJ.Orchestrator.Abstractions.Models.Session;

/// <summary>
/// Request to reset session state.
/// </summary>
public record ResetStateRequest
{
  [JsonPropertyName("session_id")]
  public string? SessionId { get; init; }
}

/// <summary>
/// Response after resetting session state.
/// </summary>
public record ResetStateResponse
{
  [JsonPropertyName("success")]
  public required bool Success { get; init; }

  [JsonPropertyName("message")]
  public string? Message { get; init; }
}
