namespace FunnelCloud.Shared.Contracts;

/// <summary>
/// Request to execute a task on a FunnelCloud agent.
/// Sent from Mesosync orchestrator over gRPC.
/// </summary>
public record TaskRequest
{
  /// <summary>
  /// Unique identifier for this task execution.
  /// </summary>
  public required string TaskId { get; init; }

  /// <summary>
  /// Type of task to execute.
  /// </summary>
  public required TaskType Type { get; init; }

  /// <summary>
  /// Command or script to execute.
  /// For Shell/PowerShell: the command string.
  /// For ReadFile/WriteFile: the file path.
  /// </summary>
  public required string Command { get; init; }

  /// <summary>
  /// Optional content for write operations.
  /// </summary>
  public string? Content { get; init; }

  /// <summary>
  /// Working directory for command execution.
  /// </summary>
  public string? WorkingDirectory { get; init; }

  /// <summary>
  /// Timeout in seconds (default 30).
  /// </summary>
  public int TimeoutSeconds { get; init; } = 30;

  /// <summary>
  /// Whether this task requires elevation (sudo/admin).
  /// If true and no credentials cached, agent returns ELEVATION_REQUIRED.
  /// </summary>
  public bool RequiresElevation { get; init; }

  /// <summary>
  /// Session ID for credential scoping.
  /// </summary>
  public string? SessionId { get; init; }
}

/// <summary>
/// Types of tasks a FunnelCloud agent can execute.
/// </summary>
public enum TaskType
{
  /// <summary>Execute a shell command (bash on Linux, cmd on Windows).</summary>
  Shell,

  /// <summary>Execute a PowerShell command/script.</summary>
  PowerShell,

  /// <summary>Read a file and return its contents.</summary>
  ReadFile,

  /// <summary>Write content to a file.</summary>
  WriteFile,

  /// <summary>List directory contents.</summary>
  ListDirectory,

  /// <summary>Execute arbitrary .NET code (sandboxed).</summary>
  DotNetCode
}
