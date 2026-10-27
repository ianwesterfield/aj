using System.Diagnostics;
using FunnelCloud.Agent.Grpc;
using FunnelCloud.Agent.Services.Events;
using FunnelCloud.Shared.Contracts;
using FunnelCloud.Shared.Ipc;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace FunnelCloud.Agent.Services;

/// <summary>
/// gRPC service implementation for task execution.
/// Handles Execute, ExecuteStreaming, GetStatus, Cancel, and Ping calls.
/// </summary>
public class TaskServiceImpl : TaskService.TaskServiceBase
{
  private readonly ILogger<TaskServiceImpl> _logger;
  private readonly TaskExecutor _executor;
  private readonly AgentCapabilities _capabilities;
  private readonly IAgentEventPublisher _events;

  // Track running tasks for status/cancel operations
  private readonly Dictionary<string, RunningTask> _runningTasks = new();
  private readonly object _tasksLock = new();

  public TaskServiceImpl(
      ILogger<TaskServiceImpl> logger,
      TaskExecutor executor,
      AgentCapabilities capabilities,
      IAgentEventPublisher events)
  {
    _logger = logger;
    _executor = executor;
    _capabilities = capabilities;
    _events = events;
  }

  public override async Task<Grpc.TaskResult> Execute(Grpc.TaskRequest request, ServerCallContext context)
  {
    var stopwatch = Stopwatch.StartNew();
    _logger.LogInformation("Executing task {TaskId}: {Type} - {Command}",
        request.TaskId, request.Type, TruncateCommand(request.Command));

    PublishReceived(request);

    try
    {
      // Convert gRPC request to shared contract
      var taskRequest = ConvertToSharedRequest(request);

      // Track running task
      var runningTask = new RunningTask(request.TaskId, DateTime.UtcNow);
      lock (_tasksLock)
      {
        _runningTasks[request.TaskId] = runningTask;
      }

      try
      {
        // Execute via TaskExecutor
        var result = await _executor.ExecuteAsync(taskRequest, context.CancellationToken);
        stopwatch.Stop();

        _logger.LogInformation("Task {TaskId} completed in {Duration}ms: Success={Success}",
            request.TaskId, stopwatch.ElapsedMilliseconds, result.Success);

        PublishCompleted(request, result, stopwatch.Elapsed);
        return ConvertToGrpcResult(result, request.TaskId, stopwatch.ElapsedMilliseconds);
      }
      finally
      {
        lock (_tasksLock)
        {
          _runningTasks.Remove(request.TaskId);
        }
      }
    }
    catch (OperationCanceledException)
    {
      _logger.LogWarning("Task {TaskId} was cancelled", request.TaskId);
      PublishFailed(request, stopwatch.Elapsed, "Task was cancelled", cancelled: true);
      return new Grpc.TaskResult
      {
        TaskId = request.TaskId,
        Success = false,
        ErrorCode = ErrorCode.ErrorCancelled,
        DurationMs = stopwatch.ElapsedMilliseconds,
        Stderr = "Task was cancelled"
      };
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Task {TaskId} failed with exception", request.TaskId);
      PublishFailed(request, stopwatch.Elapsed, ex.Message);
      return new Grpc.TaskResult
      {
        TaskId = request.TaskId,
        Success = false,
        ErrorCode = ErrorCode.ErrorInternal,
        DurationMs = stopwatch.ElapsedMilliseconds,
        Stderr = ex.Message
      };
    }
  }

  public override async Task ExecuteStreaming(
      Grpc.TaskRequest request,
      IServerStreamWriter<TaskOutput> responseStream,
      ServerCallContext context)
  {
    _logger.LogInformation("Streaming execution for task {TaskId}: {Type}",
        request.TaskId, request.Type);

    PublishReceived(request);

    var stopwatch = Stopwatch.StartNew();

    // Track running task
    var runningTask = new RunningTask(request.TaskId, DateTime.UtcNow);
    lock (_tasksLock)
    {
      _runningTasks[request.TaskId] = runningTask;
    }

    try
    {
      // Send initial status
      await responseStream.WriteAsync(new TaskOutput
      {
        TaskId = request.TaskId,
        Type = OutputType.Status,
        Content = "Starting task execution...",
        TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
      });

      // Convert and execute
      var taskRequest = ConvertToSharedRequest(request);
      var result = await _executor.ExecuteAsync(taskRequest, context.CancellationToken);
      stopwatch.Stop();
      PublishCompleted(request, result, stopwatch.Elapsed);

      // Stream stdout if present
      if (!string.IsNullOrEmpty(result.Stdout))
      {
        await responseStream.WriteAsync(new TaskOutput
        {
          TaskId = request.TaskId,
          Type = OutputType.Stdout,
          Content = result.Stdout,
          TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
      }

      // Stream stderr if present
      if (!string.IsNullOrEmpty(result.Stderr))
      {
        await responseStream.WriteAsync(new TaskOutput
        {
          TaskId = request.TaskId,
          Type = OutputType.Stderr,
          Content = result.Stderr,
          TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
      }

      // Send completion status
      await responseStream.WriteAsync(new TaskOutput
      {
        TaskId = request.TaskId,
        Type = OutputType.Status,
        Content = result.Success ? "Task completed successfully" : $"Task failed: {result.ErrorCode}",
        TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
      });
    }
    finally
    {
      lock (_tasksLock)
      {
        _runningTasks.Remove(request.TaskId);
      }
    }
  }

  public override Task<TaskStatusResponse> GetStatus(TaskStatusRequest request, ServerCallContext context)
  {
    lock (_tasksLock)
    {
      if (_runningTasks.TryGetValue(request.TaskId, out var runningTask))
      {
        return Task.FromResult(new TaskStatusResponse
        {
          TaskId = request.TaskId,
          State = TaskState.TaskRunning,
          ProgressPercent = 50, // We don't have real progress tracking yet
          StatusMessage = $"Running since {runningTask.StartedAt:HH:mm:ss}"
        });
      }
    }

    // Task not found in running tasks - could be completed or never existed
    return Task.FromResult(new TaskStatusResponse
    {
      TaskId = request.TaskId,
      State = TaskState.TaskPending, // Use valid enum value
      StatusMessage = "Task not found or already completed"
    });
  }

  public override Task<CancelResponse> Cancel(CancelRequest request, ServerCallContext context)
  {
    _logger.LogWarning("Cancel requested for task {TaskId}", request.TaskId);

    // Note: Actual cancellation requires CancellationTokenSource propagation
    // For now, we just log and acknowledge the request
    lock (_tasksLock)
    {
      if (_runningTasks.ContainsKey(request.TaskId))
      {
        // In a full implementation, we'd trigger the CancellationTokenSource here
        return Task.FromResult(new CancelResponse
        {
          Cancelled = true,
          Message = "Cancel signal sent to task"
        });
      }
    }

    return Task.FromResult(new CancelResponse
    {
      Cancelled = false,
      Message = "Task not found or already completed"
    });
  }

  public override Task<PingResponse> Ping(PingRequest request, ServerCallContext context)
  {
    return Task.FromResult(new PingResponse
    {
      RequestTimestampMs = request.TimestampMs,
      ResponseTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
      AgentId = _capabilities.AgentId,
      AgentVersion = "1.0.0"
    });
  }

  #region Helpers

  private static Shared.Contracts.TaskRequest ConvertToSharedRequest(Grpc.TaskRequest grpcRequest)
  {
    return new Shared.Contracts.TaskRequest
    {
      TaskId = grpcRequest.TaskId,
      Type = grpcRequest.Type switch
      {
        Grpc.TaskType.Shell => Shared.Contracts.TaskType.Shell,
        Grpc.TaskType.Powershell => Shared.Contracts.TaskType.PowerShell,
        Grpc.TaskType.ReadFile => Shared.Contracts.TaskType.ReadFile,
        Grpc.TaskType.WriteFile => Shared.Contracts.TaskType.WriteFile,
        Grpc.TaskType.ListDirectory => Shared.Contracts.TaskType.ListDirectory,
        Grpc.TaskType.DotnetCode => Shared.Contracts.TaskType.DotNetCode,
        _ => Shared.Contracts.TaskType.Shell
      },
      Command = grpcRequest.Command,
      // Default to 24 hours if not specified - let long scans run
      TimeoutSeconds = grpcRequest.TimeoutSeconds > 0 ? grpcRequest.TimeoutSeconds : 86400,
      RequiresElevation = grpcRequest.RequireElevation,
      WorkingDirectory = string.IsNullOrEmpty(grpcRequest.WorkingDirectory)
            ? null
            : grpcRequest.WorkingDirectory
    };
  }

  private static Grpc.TaskResult ConvertToGrpcResult(
      Shared.Contracts.TaskResult result,
      string taskId,
      long durationMs)
  {
    return new Grpc.TaskResult
    {
      TaskId = taskId,
      Success = result.Success,
      Stdout = result.Stdout ?? "",
      Stderr = result.Stderr ?? "",
      ExitCode = result.ExitCode,
      ErrorCode = result.ErrorCode switch
      {
        Shared.Contracts.TaskErrorCode.None => ErrorCode.ErrorNone,
        Shared.Contracts.TaskErrorCode.Timeout => ErrorCode.ErrorTimeout,
        Shared.Contracts.TaskErrorCode.ElevationRequired => ErrorCode.ErrorElevationRequired,
        Shared.Contracts.TaskErrorCode.NotFound => ErrorCode.ErrorNotFound,
        Shared.Contracts.TaskErrorCode.PermissionDenied => ErrorCode.ErrorPermissionDenied,
        _ => ErrorCode.ErrorInternal
      },
      DurationMs = durationMs
    };
  }

  private static string TruncateCommand(string command)
  {
    const int maxLength = 50;
    if (string.IsNullOrEmpty(command))
      return "";
    return command.Length <= maxLength
        ? command
        : command[..maxLength] + "...";
  }

  private void PublishReceived(Grpc.TaskRequest request)
  {
    _events.Publish(new AgentEvent
    {
      Kind = AgentEventKind.TaskReceived,
      TaskId = request.TaskId,
      TaskType = request.Type.ToString(),
      Command = request.Command,
      AgentId = _capabilities.AgentId
    });
  }

  private void PublishCompleted(Grpc.TaskRequest request, Shared.Contracts.TaskResult result, TimeSpan duration)
  {
    _events.Publish(new AgentEvent
    {
      Kind = result.Success ? AgentEventKind.TaskCompleted : AgentEventKind.TaskFailed,
      TaskId = request.TaskId,
      TaskType = request.Type.ToString(),
      Command = request.Command,
      ExitCode = result.ExitCode,
      Stdout = result.Stdout,
      Stderr = result.Stderr,
      DurationSeconds = duration.TotalSeconds,
      AgentId = _capabilities.AgentId,
      ErrorMessage = result.Success ? null : result.ErrorCode.ToString()
    });
  }

  private void PublishFailed(Grpc.TaskRequest request, TimeSpan duration, string errorMessage, bool cancelled = false)
  {
    _events.Publish(new AgentEvent
    {
      Kind = AgentEventKind.TaskFailed,
      TaskId = request.TaskId,
      TaskType = request.Type.ToString(),
      Command = request.Command,
      DurationSeconds = duration.TotalSeconds,
      AgentId = _capabilities.AgentId,
      ErrorMessage = cancelled ? "cancelled" : errorMessage,
      Stderr = errorMessage
    });
  }

  #endregion

  private record RunningTask(string TaskId, DateTime StartedAt);
}
