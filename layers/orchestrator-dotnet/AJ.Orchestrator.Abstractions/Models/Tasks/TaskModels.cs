using System.Text.Json.Serialization;

namespace AJ.Orchestrator.Abstractions.Models.Tasks;

/// <summary>
/// Status of a step execution.
/// </summary>
public enum StepStatus
{
  Pending,
  Running,
  Success,
  Failed,
  Skipped
}

/// <summary>
/// Categories of execution errors.
/// </summary>
public enum ErrorType
{
  Timeout,
  PermissionDenied,
  InvalidParams,
  ExecutionError,
  SandboxViolation,
  ResourceLimit,
  Unknown
}

/// <summary>
/// Single tool invocation step.
/// Represents one atomic operation in a task decomposition.
/// </summary>
public record Step
{
  public required string StepId { get; init; }
  public required string Tool { get; init; }
  public Dictionary<string, object?> Params { get; init; } = new();
  public string? BatchId { get; init; }
  public string Reasoning { get; init; } = "";
  public StepStatus Status { get; init; } = StepStatus.Pending;
  public List<string> DependsOn { get; init; } = new();
}

/// <summary>
/// Result of executing a single step.
/// </summary>
public record StepResult
{
  public required string StepId { get; init; }
  public string? Tool { get; init; }
  public Dictionary<string, object?>? Params { get; init; }
  public StepStatus Status { get; init; }
  public string? Output { get; init; }
  public string? Error { get; init; }
  public double ExecutionTime { get; init; }
}

/// <summary>
/// Detailed error information for failed steps.
/// </summary>
public record ErrorMetadata
{
  public required string StepId { get; init; }
  public required string Error { get; init; }
  public ErrorType ErrorType { get; init; } = ErrorType.Unknown;
  public bool Recoverable { get; init; }
  public string? Suggestion { get; init; }
}

/// <summary>
/// Request to run a complete task with streaming updates.
/// </summary>
public record RunTaskRequest
{
  [JsonPropertyName("task")]
  public required string Task { get; init; }

  [JsonPropertyName("workspace_root")]
  public required string WorkspaceRoot { get; init; }

  [JsonPropertyName("user_id")]
  public string? UserId { get; init; }

  [JsonPropertyName("memory_context")]
  public List<Dictionary<string, object?>>? MemoryContext { get; init; }

  [JsonPropertyName("max_steps")]
  public int MaxSteps { get; init; } = 15;

  [JsonPropertyName("preserve_state")]
  public bool PreserveState { get; init; } = false;

  [JsonPropertyName("model")]
  public string? Model { get; init; }
}

/// <summary>
/// Server-sent event during task execution.
/// </summary>
public record TaskEvent
{
  [JsonPropertyName("event_type")]
  public string EventType { get; init; } = "";

  // Alias for EventType for simpler code
  [JsonIgnore]
  public string Type { get => EventType; init => EventType = value; }

  [JsonPropertyName("step_num")]
  public int StepNum { get; init; }

  [JsonPropertyName("tool")]
  public string? Tool { get; init; }

  [JsonPropertyName("params")]
  public Dictionary<string, object?>? Params { get; init; }

  [JsonPropertyName("status")]
  public string Status { get; init; } = "";

  [JsonPropertyName("content")]
  public string? Content { get; init; }

  [JsonPropertyName("result")]
  public Dictionary<string, object?>? Result { get; init; }

  [JsonPropertyName("done")]
  public bool Done { get; init; }

  [JsonPropertyName("error")]
  public string? Error { get; init; }
}

/// <summary>
/// Request for generating the next step.
/// </summary>
public record NextStepRequest
{
  [JsonPropertyName("task")]
  public required string Task { get; init; }

  [JsonPropertyName("history")]
  public List<StepResult>? History { get; init; }

  [JsonPropertyName("workspace_context")]
  public Workspace.WorkspaceContext? WorkspaceContext { get; init; }

  [JsonPropertyName("user_id")]
  public string? UserId { get; init; }
}

/// <summary>
/// Response containing the next step to execute.
/// </summary>
public record NextStepResponse(
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("params")] Dictionary<string, object?> Params,
    [property: JsonPropertyName("batch_id")] string? BatchId,
    [property: JsonPropertyName("reasoning")] string Reasoning,
    [property: JsonPropertyName("is_batch")] bool IsBatch = false,
    [property: JsonPropertyName("thinking_content")] string? ThinkingContent = null
);
