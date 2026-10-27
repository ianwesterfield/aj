using AJ.Orchestrator.Abstractions.Models.Workspace;

namespace AJ.Orchestrator.Abstractions.Services;

/// <summary>
/// Task planner that decomposes intents into steps and manages workspace context.
/// </summary>
public interface ITaskPlanner
{
  /// <summary>
  /// Set the active workspace directory.
  /// </summary>
  Task<WorkspaceContext> SetWorkspaceAsync(string cwd, string? userId = null);

  /// <summary>
  /// Clone a git repository and set as workspace.
  /// </summary>
  Task<CloneWorkspaceResponse> CloneWorkspaceAsync(CloneWorkspaceRequest request);

  /// <summary>
  /// Get current workspace context.
  /// </summary>
  WorkspaceContext? GetCurrentWorkspace();
}
