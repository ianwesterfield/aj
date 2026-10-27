using FunnelCloud.Shared.Ipc;

namespace FunnelCloud.Agent.Services.Events;

/// <summary>
/// Publishes agent events to any connected IPC subscribers (e.g. the Windows
/// tray app or the Linux notify CLI). Implementations must be safe to call
/// from any thread; publishing must never block the calling code for long
/// and must never throw.
/// </summary>
public interface IAgentEventPublisher
{
  /// <summary>
  /// Fire-and-forget publish. Serializes the event and fans it out to all
  /// connected subscribers. Never throws; logs failures instead.
  /// </summary>
  void Publish(AgentEvent evt);
}
