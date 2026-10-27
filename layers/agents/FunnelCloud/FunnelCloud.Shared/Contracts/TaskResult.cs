namespace FunnelCloud.Shared.Contracts;

/// <summary>
/// Result of executing a task on a FunnelCloud agent.
/// Returned from agent to Mesosync orchestrator.
/// </summary>
public record TaskResult
{
  /// <summary>
  /// The task ID this result corresponds to.
  /// </summary>
  public required string TaskId { get; init; }

  /// <summary>
  /// Whether the task completed successfully.
  /// </summary>
  public required bool Success { get; init; }

  /// <summary>
  /// Standard output from command execution.
  /// </summary>
  public string? Stdout { get; init; }

  /// <summary>
  /// Standard error from command execution.
  /// </summary>
  public string? Stderr { get; init; }

  /// <summary>
  /// Exit code from command execution (0 = success).
  /// </summary>
  public int ExitCode { get; init; }

  /// <summary>
  /// Error code if the task failed.
  /// </summary>
  public TaskErrorCode? ErrorCode { get; init; }

  /// <summary>
  /// Human-readable error message if the task failed.
  /// </summary>
  public string? ErrorMessage { get; init; }

  /// <summary>
  /// Execution duration in milliseconds.
  /// </summary>
  public long DurationMs { get; init; }

  /// <summary>
  /// Agent ID that executed this task.
  /// </summary>
  public required string AgentId { get; init; }
}

/// <summary>
/// Error codes for task execution failures.
/// </summary>
public enum TaskErrorCode
{
  /// <summary>No error.</summary>
  None = 0,

  /// <summary>Task timed out.</summary>
  Timeout = 1,

  /// <summary>Task requires elevated permissions. Prompt user for credentials.</summary>
  ElevationRequired = 2,

  /// <summary>File or path not found.</summary>
  NotFound = 3,

  /// <summary>Permission denied (even with elevation).</summary>
  PermissionDenied = 4,

  /// <summary>Command/script syntax error.</summary>
  SyntaxError = 5,

  /// <summary>Working directory does not exist.</summary>
  InvalidWorkingDirectory = 6,

  /// <summary>Internal agent error.</summary>
  InternalError = 99
}
