using System.Text.Json.Serialization;

namespace AJ.Orchestrator.Abstractions.Models.Workspace;

/// <summary>
/// Workspace scoping model.
/// Defines the active directory and permissions for all operations.
/// </summary>
public record WorkspaceContext
{
  /// <summary>Current working directory.</summary>
  public required string Cwd { get; init; }

  /// <summary>Root directory (sandbox boundary).</summary>
  public required string WorkspaceRoot { get; init; }

  /// <summary>Cached directory listing.</summary>
  public List<string> AvailablePaths { get; init; } = new();

  /// <summary>Admin valve: allow parallel execution.</summary>
  public bool ParallelEnabled { get; init; }

  /// <summary>Max concurrent tasks.</summary>
  public int MaxParallelTasks { get; init; } = 4;

  /// <summary>Languages user can execute.</summary>
  public List<string> AllowedLanguages { get; init; } = new() { "python", "powershell", "node" };

  /// <summary>Can run arbitrary code.</summary>
  public bool AllowCodeExecution { get; init; }

  /// <summary>Can write files.</summary>
  public bool AllowFileWrite { get; init; }

  /// <summary>Can run shell commands.</summary>
  public bool AllowShellCommands { get; init; }

  /// <summary>Timeout per task (seconds).</summary>
  public int MaxExecutionTime { get; init; } = 30;
}

public record SetWorkspaceRequest
{
  [JsonPropertyName("cwd")]
  public required string Cwd { get; init; }

  [JsonPropertyName("user_id")]
  public string? UserId { get; init; }
}

public record SetWorkspaceResponse
{
  [JsonPropertyName("success")]
  public required bool Success { get; init; }

  [JsonPropertyName("workspace_path")]
  public string? WorkspacePath { get; init; }

  [JsonPropertyName("session_id")]
  public string? SessionId { get; init; }

  [JsonPropertyName("error")]
  public string? Error { get; init; }
}

public record CloneWorkspaceRequest
{
  [JsonPropertyName("repo_url")]
  public required string RepoUrl { get; init; }

  [JsonPropertyName("branch")]
  public string? Branch { get; init; }

  [JsonPropertyName("target_name")]
  public string? TargetName { get; init; }

  [JsonPropertyName("user_id")]
  public string? UserId { get; init; }
}

public record CloneWorkspaceResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("workspace_path")] string WorkspacePath,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("context")] WorkspaceContext? Context = null
);
