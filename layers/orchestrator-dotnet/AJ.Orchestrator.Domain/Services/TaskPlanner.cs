using AJ.Orchestrator.Abstractions.Models.Workspace;
using AJ.Orchestrator.Abstractions.Services;
using Microsoft.Extensions.Logging;

namespace AJ.Orchestrator.Domain.Services;

/// <summary>
/// Task planner implementation for workspace management.
/// </summary>
public class TaskPlanner : ITaskPlanner
{
  private readonly ILogger<TaskPlanner> _logger;
  private WorkspaceContext? _currentWorkspace;

  public TaskPlanner(ILogger<TaskPlanner> logger)
  {
    _logger = logger;
  }

  public Task<WorkspaceContext> SetWorkspaceAsync(string cwd, string? userId = null)
  {
    if (!Directory.Exists(cwd))
    {
      throw new DirectoryNotFoundException($"Workspace directory not found: {cwd}");
    }

    var context = new WorkspaceContext
    {
      Cwd = cwd,
      WorkspaceRoot = cwd,
      AvailablePaths = GetAvailablePaths(cwd),
      AllowShellCommands = true,
      AllowFileWrite = true,
      AllowCodeExecution = true
    };

    _currentWorkspace = context;
    _logger.LogInformation("Workspace set to {Cwd}", cwd);

    return Task.FromResult(context);
  }

  public async Task<CloneWorkspaceResponse> CloneWorkspaceAsync(CloneWorkspaceRequest request)
  {
    try
    {
      var targetDir = Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
          "Code",
          request.TargetName ?? GetRepoName(request.RepoUrl));

      if (Directory.Exists(targetDir))
      {
        // Pull instead of clone
        var pullResult = await RunGitCommandAsync($"-C \"{targetDir}\" pull");
        if (!pullResult.Success)
        {
          return new CloneWorkspaceResponse(false, targetDir, $"Git pull failed: {pullResult.Error}");
        }
      }
      else
      {
        // Clone
        var branch = request.Branch != null ? $"-b {request.Branch}" : "";
        var cloneResult = await RunGitCommandAsync($"clone {branch} {request.RepoUrl} \"{targetDir}\"");
        if (!cloneResult.Success)
        {
          return new CloneWorkspaceResponse(false, targetDir, $"Git clone failed: {cloneResult.Error}");
        }
      }

      var context = await SetWorkspaceAsync(targetDir, request.UserId);
      return new CloneWorkspaceResponse(true, targetDir, "Workspace cloned successfully", context);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to clone workspace");
      return new CloneWorkspaceResponse(false, "", ex.Message);
    }
  }

  public WorkspaceContext? GetCurrentWorkspace() => _currentWorkspace;

  private static List<string> GetAvailablePaths(string root)
  {
    try
    {
      return Directory.GetFileSystemEntries(root)
          .Select(Path.GetFileName)
          .Where(n => n != null)
          .Select(n => n!)
          .Take(100)
          .ToList();
    }
    catch
    {
      return new List<string>();
    }
  }

  private static string GetRepoName(string repoUrl)
  {
    var name = repoUrl.Split('/').Last();
    if (name.EndsWith(".git"))
    {
      name = name[..^4];
    }
    return name;
  }

  private static async Task<(bool Success, string Output, string? Error)> RunGitCommandAsync(string args)
  {
    var psi = new System.Diagnostics.ProcessStartInfo
    {
      FileName = "git",
      Arguments = args,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };

    using var process = System.Diagnostics.Process.Start(psi);
    if (process == null)
    {
      return (false, "", "Failed to start git process");
    }

    var output = await process.StandardOutput.ReadToEndAsync();
    var error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    return (process.ExitCode == 0, output, string.IsNullOrEmpty(error) ? null : error);
  }
}
