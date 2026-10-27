namespace AJ.Orchestrator.Abstractions.Models.Batch;

/// <summary>
/// Request to execute multiple steps in a batch.
/// </summary>
public record ExecuteBatchRequest(
    List<Tasks.Step> Steps,
    string BatchId,
    Workspace.WorkspaceContext? WorkspaceContext = null,
    string? UserId = null
);

/// <summary>
/// Result of batch execution.
/// </summary>
public record BatchResult
{
  public required string BatchId { get; init; }
  public List<Tasks.StepResult> Successful { get; init; } = new();
  public List<Tasks.ErrorMetadata> Failed { get; init; } = new();
  public double Duration { get; init; }

  public int SuccessfulCount => Successful.Count;
  public int FailedCount => Failed.Count;
}
