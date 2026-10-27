using System.Text.Json.Serialization;

namespace FunnelCloud.Shared.Ipc;

/// <summary>
/// Request sent by a client (tray, CLI) as the first line after connecting
/// to the agent's event pipe. Controls how many recent events to replay
/// before entering live streaming mode.
/// </summary>
public record SubscribeRequest
{
  /// <summary>Protocol op name; must be "subscribe".</summary>
  [JsonPropertyName("op")]
  public string Op { get; init; } = "subscribe";

  /// <summary>
  /// Number of recent events to replay from the agent's in-memory ring
  /// buffer before entering live streaming mode. Clamped to
  /// <see cref="AgentEventSerializer.MaxReplay"/>. Set to 0 to skip replay
  /// (equivalent to <c>funnel tail</c>). Use <c>int.MaxValue</c> for "all".
  /// </summary>
  [JsonPropertyName("replay")]
  public int Replay { get; init; } = 0;

  /// <summary>
  /// If true, the server sends the replay events and then closes the
  /// connection instead of entering live streaming (one-shot list mode).
  /// </summary>
  [JsonPropertyName("one_shot")]
  public bool OneShot { get; init; } = false;
}
