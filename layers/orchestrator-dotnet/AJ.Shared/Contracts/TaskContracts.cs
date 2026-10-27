namespace AJ.Shared.Contracts;

/// <summary>
/// Request to execute a task on a FunnelCloud agent.
/// </summary>
public record TaskRequest
{
  public required string TaskId { get; init; }
  public required TaskType Type { get; init; }
  public required string Command { get; init; }
  public string? Content { get; init; }
  public string? WorkingDirectory { get; init; }
  public int TimeoutSeconds { get; init; } = 30;
  public bool RequiresElevation { get; init; }
  public string? SessionId { get; init; }
}

/// <summary>
/// Types of tasks a FunnelCloud agent can execute.
/// </summary>
public enum TaskType
{
  Shell,
  PowerShell,
  ReadFile,
  WriteFile,
  ListDirectory,
  DotNetCode
}

/// <summary>
/// Result of executing a task on a FunnelCloud agent.
/// </summary>
public record TaskResult
{
  public required string TaskId { get; init; }
  public required bool Success { get; init; }
  public string? Stdout { get; init; }
  public string? Stderr { get; init; }
  public int ExitCode { get; init; }
  public TaskErrorCode? ErrorCode { get; init; }
  public string? ErrorMessage { get; init; }
  public long DurationMs { get; init; }
  public required string AgentId { get; init; }
}

/// <summary>
/// Error codes for task execution failures.
/// </summary>
public enum TaskErrorCode
{
  None = 0,
  Timeout = 1,
  ElevationRequired = 2,
  NotFound = 3,
  PermissionDenied = 4,
  SyntaxError = 5,
  InvalidWorkingDirectory = 6,
  InternalError = 99
}
