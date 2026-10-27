namespace FunnelCloud.Shared.Contracts;

/// <summary>
/// Capabilities advertised by a FunnelCloud agent during discovery.
/// Serialized as JSON in UDP discovery responses.
/// </summary>
public record AgentCapabilities
{
  /// <summary>
  /// Unique identifier for this agent (e.g., "dev-workstation").
  /// Used for routing only - NOT for authentication (mTLS handles that).
  /// </summary>
  public required string AgentId { get; init; }

  /// <summary>
  /// Machine hostname (e.g., "DESKTOP-AJ01").
  /// </summary>
  public required string Hostname { get; init; }

  /// <summary>
  /// Operating system platform: "windows", "linux", or "macos".
  /// </summary>
  public required string Platform { get; init; }

  /// <summary>
  /// List of available capabilities (e.g., ["powershell", "dotnet", "git", "docker"]).
  /// </summary>
  public required string[] Capabilities { get; init; }

  /// <summary>
  /// Workspace root directories the agent can access (e.g., ["C:\\Code", "D:\\Projects"]).
  /// </summary>
  public required string[] WorkspaceRoots { get; init; }

  /// <summary>
  /// SHA256 fingerprint of the agent's client certificate.
  /// Used during mTLS handshake verification.
  /// </summary>
  public required string CertificateFingerprint { get; init; }

  /// <summary>
  /// UDP port the agent is listening on for discovery.
  /// </summary>
  public int DiscoveryPort { get; init; } = 41420;

  /// <summary>
  /// gRPC port for task execution after mTLS handshake.
  /// </summary>
  public int GrpcPort { get; init; } = 41235;

  /// <summary>
  /// IP address of the agent (filled in by discovery service from response source).
  /// This is the address the orchestrator should use to connect to this agent.
  /// </summary>
  public string? IpAddress { get; init; }
}