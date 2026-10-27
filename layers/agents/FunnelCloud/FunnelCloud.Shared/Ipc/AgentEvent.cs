using System.Text.Json;
using System.Text.Json.Serialization;

namespace FunnelCloud.Shared.Ipc;

/// <summary>
/// Kind of agent event published over the IPC pipe.
/// Lowercase snake_case for cross-language readability on the wire.
/// </summary>
public static class AgentEventKind
{
  public const string TaskReceived = "task_received";
  public const string TaskCompleted = "task_completed";
  public const string TaskFailed = "task_failed";
  public const string AgentStatus = "agent_status";
}

/// <summary>
/// Event published by the agent over the named pipe to subscribers
/// (Windows tray app, Linux notify CLI, etc.). Wire format is one
/// JSON object per line (newline-delimited JSON / NDJSON).
/// </summary>
public record AgentEvent
{
  [JsonPropertyName("event_id")]
  public string EventId { get; init; } = Guid.NewGuid().ToString("N");

  [JsonPropertyName("timestamp")]
  public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

  /// <summary>See <see cref="AgentEventKind"/> for defined values.</summary>
  [JsonPropertyName("kind")]
  public string Kind { get; init; } = "";

  [JsonPropertyName("task_id")]
  public string? TaskId { get; init; }

  [JsonPropertyName("task_type")]
  public string? TaskType { get; init; }

  [JsonPropertyName("command")]
  public string? Command { get; init; }

  [JsonPropertyName("exit_code")]
  public int? ExitCode { get; init; }

  [JsonPropertyName("stdout")]
  public string? Stdout { get; init; }

  [JsonPropertyName("stderr")]
  public string? Stderr { get; init; }

  [JsonPropertyName("duration_seconds")]
  public double? DurationSeconds { get; init; }

  [JsonPropertyName("agent_id")]
  public string? AgentId { get; init; }

  [JsonPropertyName("error_message")]
  public string? ErrorMessage { get; init; }
}

/// <summary>
/// Shared JSON serialization options for <see cref="AgentEvent"/>.
/// Uses snake_case property names and omits nulls to keep the wire compact.
/// </summary>
public static class AgentEventSerializer
{
  public const string PipeName = "funnelcloud-agent-events";

  /// <summary>Upper bound on replay requests; protects the agent from absurd values.</summary>
  public const int MaxReplay = 500;

  public static readonly JsonSerializerOptions Options = new()
  {
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false
  };

  public static string Serialize(AgentEvent evt) => JsonSerializer.Serialize(evt, Options);

  public static AgentEvent? Deserialize(string json) =>
      JsonSerializer.Deserialize<AgentEvent>(json, Options);

  public static string Serialize(SubscribeRequest req) => JsonSerializer.Serialize(req, Options);

  public static string SerializeSubscribe(SubscribeRequest req) => Serialize(req);

  public static SubscribeRequest? DeserializeSubscribe(string json) =>
      JsonSerializer.Deserialize<SubscribeRequest>(json, Options);
}
