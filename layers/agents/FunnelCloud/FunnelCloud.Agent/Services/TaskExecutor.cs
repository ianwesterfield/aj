using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using FunnelCloud.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace FunnelCloud.Agent.Services;

/// <summary>
/// Executes tasks using PowerShell Core.
/// 
/// Uses System.Management.Automation to run PowerShell scripts in-process,
/// avoiding shell injection vulnerabilities and providing rich output.
/// </summary>
public class TaskExecutor : IDisposable
{
  private readonly ILogger<TaskExecutor> _logger;
  private readonly string _agentId;
  private readonly Runspace _runspace;
  private bool _disposed;

  public TaskExecutor(ILogger<TaskExecutor> logger, string agentId)
  {
    _logger = logger;
    _agentId = agentId;

    // Create a minimal runspace - we'll primarily use pwsh.exe for commands
    // CreateDefault2() avoids loading snap-ins that may not exist in SDK deployment
    var initialState = InitialSessionState.CreateDefault2();
    _runspace = RunspaceFactory.CreateRunspace(initialState);
    _runspace.Open();

    _logger.LogInformation("TaskExecutor initialized for agent {AgentId}", agentId);
  }

  /// <summary>
  /// Execute a task and return the result.
  /// </summary>
  public async Task<TaskResult> ExecuteAsync(TaskRequest request, CancellationToken cancellationToken = default)
  {
    var stopwatch = Stopwatch.StartNew();

    _logger.LogInformation(
        "Executing task {TaskId} of type {TaskType}: {Command}",
        request.TaskId, request.Type, request.Command);

    try
    {
      return request.Type switch
      {
        TaskType.PowerShell => await ExecutePowerShellAsync(request, cancellationToken),
        TaskType.Shell => await ExecuteShellAsync(request, cancellationToken),
        TaskType.ReadFile => await ExecuteReadFileAsync(request),
        TaskType.WriteFile => await ExecuteWriteFileAsync(request),
        TaskType.ListDirectory => await ExecuteListDirectoryAsync(request),
        _ => CreateErrorResult(request, TaskErrorCode.InternalError, $"Unsupported task type: {request.Type}")
      };
    }
    catch (OperationCanceledException)
    {
      return CreateErrorResult(request, TaskErrorCode.Timeout, "Task was cancelled");
    }
    catch (UnauthorizedAccessException ex)
    {
      // Check if this might be an elevation issue
      if (ex.Message.Contains("access", StringComparison.OrdinalIgnoreCase))
      {
        return CreateErrorResult(request, TaskErrorCode.ElevationRequired,
            "Operation requires elevated permissions");
      }
      return CreateErrorResult(request, TaskErrorCode.PermissionDenied, ex.Message);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Task {TaskId} failed with exception", request.TaskId);
      return CreateErrorResult(request, TaskErrorCode.InternalError, ex.Message);
    }
    finally
    {
      stopwatch.Stop();
      _logger.LogInformation(
          "Task {TaskId} completed in {DurationMs}ms",
          request.TaskId, stopwatch.ElapsedMilliseconds);
    }
  }

  private async Task<TaskResult> ExecutePowerShellAsync(TaskRequest request, CancellationToken cancellationToken)
  {
    // Use pwsh.exe process for full PowerShell functionality
    // The SDK's in-process runspace doesn't have access to all modules

    // First, find PowerShell executable
    var pwshPath = FindPowerShellExecutable();
    if (pwshPath == null)
    {
      _logger.LogError("PowerShell Core (pwsh) not found in PATH or common locations");
      return CreateErrorResult(request, TaskErrorCode.NotFound,
          "PowerShell Core (pwsh) not found. Ensure PowerShell 7+ is installed.");
    }

    _logger.LogDebug("Using PowerShell at: {Path}", pwshPath);

    var startInfo = new ProcessStartInfo
    {
      FileName = pwshPath,
      Arguments = $"-NoProfile -NonInteractive -Command \"{EscapePowerShellCommand(request.Command)}\"",
      UseShellExecute = false,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      CreateNoWindow = true,
      WorkingDirectory = string.IsNullOrEmpty(request.WorkingDirectory)
          ? Environment.CurrentDirectory
          : request.WorkingDirectory
    };

    using var process = new Process { StartInfo = startInfo };
    var outputBuilder = new System.Text.StringBuilder();
    var errorBuilder = new System.Text.StringBuilder();

    process.OutputDataReceived += (sender, e) =>
    {
      if (e.Data != null)
        outputBuilder.AppendLine(e.Data);
    };
    process.ErrorDataReceived += (sender, e) =>
    {
      if (e.Data != null)
        errorBuilder.AppendLine(e.Data);
    };

    var stopwatch = Stopwatch.StartNew();

    try
    {
      process.Start();
      process.BeginOutputReadLine();
      process.BeginErrorReadLine();

      using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      cts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));

      await process.WaitForExitAsync(cts.Token);
      stopwatch.Stop();

      return new TaskResult
      {
        TaskId = request.TaskId,
        Success = process.ExitCode == 0,
        Stdout = outputBuilder.ToString().TrimEnd(),
        Stderr = errorBuilder.Length > 0 ? errorBuilder.ToString().TrimEnd() : null,
        ExitCode = process.ExitCode,
        ErrorCode = process.ExitCode == 0 ? TaskErrorCode.None : TaskErrorCode.InternalError,
        DurationMs = stopwatch.ElapsedMilliseconds,
        AgentId = _agentId
      };
    }
    catch (OperationCanceledException)
    {
      try { process.Kill(entireProcessTree: true); } catch { }
      return CreateErrorResult(request, TaskErrorCode.Timeout,
          $"Task timed out after {request.TimeoutSeconds} seconds");
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "PowerShell execution failed for task {TaskId}", request.TaskId);
      return CreateErrorResult(request, TaskErrorCode.InternalError, ex.Message);
    }
  }

  private static string? FindPowerShellExecutable()
  {
    // Try common PowerShell locations - prefer pwsh (PowerShell Core) but fallback to powershell.exe
    var possiblePaths = new[]
    {
      "pwsh",  // In PATH
      "pwsh.exe",  // In PATH (Windows)
      @"C:\Program Files\PowerShell\7\pwsh.exe",
      @"C:\Program Files\PowerShell\7-preview\pwsh.exe",
      "/usr/bin/pwsh",
      "/usr/local/bin/pwsh",
      // Windows PowerShell fallback
      "powershell",
      "powershell.exe",
      @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
    };

    foreach (var path in possiblePaths)
    {
      try
      {
        // Try to start process briefly to verify it exists
        var psi = new ProcessStartInfo
        {
          FileName = path,
          Arguments = "-Version",
          UseShellExecute = false,
          RedirectStandardOutput = true,
          RedirectStandardError = true,
          CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process != null)
        {
          process.WaitForExit(2000);
          return path;  // Found working PowerShell
        }
      }
      catch
      {
        // Try next path
      }
    }

    return null;
  }

  private static string EscapePowerShellCommand(string command)
  {
    // Escape double quotes for command line
    return command.Replace("\"", "\\\"");
  }

  private async Task<TaskResult> ExecuteShellAsync(TaskRequest request, CancellationToken cancellationToken)
  {
    // For shell commands, we use Process to execute native shell
    var isWindows = OperatingSystem.IsWindows();
    var shell = isWindows ? "cmd.exe" : "/bin/bash";
    var shellArg = isWindows ? "/c" : "-c";

    var psi = new ProcessStartInfo
    {
      FileName = shell,
      Arguments = $"{shellArg} \"{request.Command.Replace("\"", "\\\"")}\"",
      WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };

    using var process = new Process { StartInfo = psi };
    var stopwatch = Stopwatch.StartNew();

    process.Start();

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    cts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));

    try
    {
      await process.WaitForExitAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
      process.Kill(entireProcessTree: true);
      return CreateErrorResult(request, TaskErrorCode.Timeout,
          $"Task timed out after {request.TimeoutSeconds} seconds");
    }

    stopwatch.Stop();

    var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
    var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);

    return new TaskResult
    {
      TaskId = request.TaskId,
      Success = process.ExitCode == 0,
      Stdout = stdout.TrimEnd(),
      Stderr = string.IsNullOrEmpty(stderr) ? null : stderr.TrimEnd(),
      ExitCode = process.ExitCode,
      DurationMs = stopwatch.ElapsedMilliseconds,
      AgentId = _agentId
    };
  }

  private async Task<TaskResult> ExecuteReadFileAsync(TaskRequest request)
  {
    var path = request.Command; // Command field holds the file path for file operations

    if (!File.Exists(path))
    {
      return CreateErrorResult(request, TaskErrorCode.NotFound, $"File not found: {path}");
    }

    var content = await File.ReadAllTextAsync(path);

    return new TaskResult
    {
      TaskId = request.TaskId,
      Success = true,
      Stdout = content,
      ExitCode = 0,
      AgentId = _agentId
    };
  }

  private async Task<TaskResult> ExecuteWriteFileAsync(TaskRequest request)
  {
    var path = request.Command;
    var content = request.Content ?? "";

    // Ensure directory exists
    var dir = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
    {
      Directory.CreateDirectory(dir);
    }

    await File.WriteAllTextAsync(path, content);

    return new TaskResult
    {
      TaskId = request.TaskId,
      Success = true,
      Stdout = $"Wrote {content.Length} bytes to {path}",
      ExitCode = 0,
      AgentId = _agentId
    };
  }

  private Task<TaskResult> ExecuteListDirectoryAsync(TaskRequest request)
  {
    var path = request.Command;

    if (!Directory.Exists(path))
    {
      return Task.FromResult(CreateErrorResult(request, TaskErrorCode.NotFound, $"Directory not found: {path}"));
    }

    var entries = Directory.GetFileSystemEntries(path)
        .Select(e =>
        {
          var isDir = Directory.Exists(e);
          var name = Path.GetFileName(e);
          return isDir ? $"{name}/" : name;
        })
        .OrderBy(e => !e.EndsWith('/')) // Directories first
        .ThenBy(e => e);

    return Task.FromResult(new TaskResult
    {
      TaskId = request.TaskId,
      Success = true,
      Stdout = string.Join("\n", entries),
      ExitCode = 0,
      AgentId = _agentId
    });
  }

  private TaskResult CreateErrorResult(TaskRequest request, TaskErrorCode errorCode, string message)
  {
    return new TaskResult
    {
      TaskId = request.TaskId,
      Success = false,
      ErrorCode = errorCode,
      ErrorMessage = message,
      ExitCode = (int)errorCode,
      AgentId = _agentId
    };
  }

  public void Dispose()
  {
    if (_disposed) return;

    _runspace.Close();
    _runspace.Dispose();
    _disposed = true;

    GC.SuppressFinalize(this);
  }
}
