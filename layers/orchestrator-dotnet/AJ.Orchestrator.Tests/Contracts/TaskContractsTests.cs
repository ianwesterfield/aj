using AJ.Shared.Contracts;
using FluentAssertions;

namespace AJ.Orchestrator.Tests.Contracts;

/// <summary>
/// Tests for TaskRequest, TaskResult, and related contracts - AJ.Shared layer.
/// </summary>
public class TaskContractsTests
{
  #region TaskRequest Tests

  [Fact]
  public void TaskRequest_RequiredProperties_CanBeSet()
  {
    var request = new TaskRequest
    {
      TaskId = "task-123",
      Type = TaskType.PowerShell,
      Command = "Get-Process"
    };

    request.TaskId.Should().Be("task-123");
    request.Type.Should().Be(TaskType.PowerShell);
    request.Command.Should().Be("Get-Process");
  }

  [Fact]
  public void TaskRequest_DefaultTimeout_Is30Seconds()
  {
    var request = new TaskRequest
    {
      TaskId = "task-123",
      Type = TaskType.Shell,
      Command = "ls"
    };

    request.TimeoutSeconds.Should().Be(30);
  }

  [Fact]
  public void TaskRequest_CustomTimeout_CanBeSet()
  {
    var request = new TaskRequest
    {
      TaskId = "task-123",
      Type = TaskType.Shell,
      Command = "long-running-script.sh",
      TimeoutSeconds = 300
    };

    request.TimeoutSeconds.Should().Be(300);
  }

  [Fact]
  public void TaskRequest_OptionalProperties_AreNull()
  {
    var request = new TaskRequest
    {
      TaskId = "task-123",
      Type = TaskType.Shell,
      Command = "ls"
    };

    request.Content.Should().BeNull();
    request.WorkingDirectory.Should().BeNull();
    request.SessionId.Should().BeNull();
  }

  [Fact]
  public void TaskRequest_RequiresElevation_DefaultsFalse()
  {
    var request = new TaskRequest
    {
      TaskId = "task-123",
      Type = TaskType.PowerShell,
      Command = "Get-Service"
    };

    request.RequiresElevation.Should().BeFalse();
  }

  [Fact]
  public void TaskRequest_WriteFile_CanSetContent()
  {
    var request = new TaskRequest
    {
      TaskId = "task-write",
      Type = TaskType.WriteFile,
      Command = "/path/to/file.txt",
      Content = "File content here"
    };

    request.Type.Should().Be(TaskType.WriteFile);
    request.Content.Should().Be("File content here");
  }

  [Theory]
  [InlineData(TaskType.Shell)]
  [InlineData(TaskType.PowerShell)]
  [InlineData(TaskType.ReadFile)]
  [InlineData(TaskType.WriteFile)]
  [InlineData(TaskType.ListDirectory)]
  [InlineData(TaskType.DotNetCode)]
  public void TaskRequest_AllTaskTypes_AreValid(TaskType taskType)
  {
    var request = new TaskRequest
    {
      TaskId = $"task-{taskType}",
      Type = taskType,
      Command = "test"
    };

    request.Type.Should().Be(taskType);
  }

  #endregion

  #region TaskResult Tests

  [Fact]
  public void TaskResult_SuccessfulExecution_HasCorrectProperties()
  {
    var result = new TaskResult
    {
      TaskId = "task-123",
      Success = true,
      Stdout = "Process output here",
      ExitCode = 0,
      DurationMs = 150,
      AgentId = "agent-1"
    };

    result.Success.Should().BeTrue();
    result.ExitCode.Should().Be(0);
    result.Stdout.Should().Be("Process output here");
    result.Stderr.Should().BeNull();
    result.ErrorCode.Should().BeNull();
  }

  [Fact]
  public void TaskResult_FailedExecution_HasErrorInfo()
  {
    var result = new TaskResult
    {
      TaskId = "task-456",
      Success = false,
      Stderr = "Command not found",
      ExitCode = 127,
      ErrorCode = TaskErrorCode.NotFound,
      ErrorMessage = "The command was not found",
      DurationMs = 50,
      AgentId = "agent-1"
    };

    result.Success.Should().BeFalse();
    result.ExitCode.Should().Be(127);
    result.ErrorCode.Should().Be(TaskErrorCode.NotFound);
    result.ErrorMessage.Should().Contain("not found");
  }

  [Fact]
  public void TaskResult_Timeout_HasCorrectErrorCode()
  {
    var result = new TaskResult
    {
      TaskId = "task-timeout",
      Success = false,
      ExitCode = 1,
      ErrorCode = TaskErrorCode.Timeout,
      ErrorMessage = "Command timed out after 30 seconds",
      DurationMs = 30000,
      AgentId = "agent-1"
    };

    result.ErrorCode.Should().Be(TaskErrorCode.Timeout);
    result.DurationMs.Should().Be(30000);
  }

  [Fact]
  public void TaskResult_BothStdoutAndStderr_CanBePresent()
  {
    var result = new TaskResult
    {
      TaskId = "task-mixed",
      Success = true,
      Stdout = "Normal output",
      Stderr = "Warning: deprecated",
      ExitCode = 0,
      DurationMs = 100,
      AgentId = "agent-1"
    };

    result.Stdout.Should().NotBeNullOrEmpty();
    result.Stderr.Should().NotBeNullOrEmpty();
    result.Success.Should().BeTrue(); // Warnings don't mean failure
  }

  #endregion

  #region TaskErrorCode Tests

  [Fact]
  public void TaskErrorCode_None_IsZero()
  {
    TaskErrorCode.None.Should().Be(0);
  }

  [Fact]
  public void TaskErrorCode_InternalError_Is99()
  {
    TaskErrorCode.InternalError.Should().Be((TaskErrorCode)99);
  }

  [Theory]
  [InlineData(TaskErrorCode.Timeout, 1)]
  [InlineData(TaskErrorCode.ElevationRequired, 2)]
  [InlineData(TaskErrorCode.NotFound, 3)]
  [InlineData(TaskErrorCode.PermissionDenied, 4)]
  [InlineData(TaskErrorCode.SyntaxError, 5)]
  [InlineData(TaskErrorCode.InvalidWorkingDirectory, 6)]
  public void TaskErrorCode_Values_AreSequential(TaskErrorCode errorCode, int expectedValue)
  {
    ((int)errorCode).Should().Be(expectedValue);
  }

  #endregion

  #region TaskType Tests

  [Fact]
  public void TaskType_Shell_IsFirstValue()
  {
    ((int)TaskType.Shell).Should().Be(0);
  }

  [Fact]
  public void TaskType_AllValues_AreDefined()
  {
    var taskTypes = Enum.GetValues<TaskType>();

    taskTypes.Should().HaveCount(6);
    taskTypes.Should().Contain(TaskType.Shell);
    taskTypes.Should().Contain(TaskType.PowerShell);
    taskTypes.Should().Contain(TaskType.ReadFile);
    taskTypes.Should().Contain(TaskType.WriteFile);
    taskTypes.Should().Contain(TaskType.ListDirectory);
    taskTypes.Should().Contain(TaskType.DotNetCode);
  }

  #endregion

  #region Integration Scenarios

  [Fact]
  public void TaskRequest_ToTaskResult_RoundTrip()
  {
    var taskId = Guid.NewGuid().ToString();
    var request = new TaskRequest
    {
      TaskId = taskId,
      Type = TaskType.PowerShell,
      Command = "Get-Date",
      WorkingDirectory = "C:\\Temp",
      TimeoutSeconds = 10
    };

    var result = new TaskResult
    {
      TaskId = request.TaskId,
      Success = true,
      Stdout = "Tuesday, March 25, 2026",
      ExitCode = 0,
      DurationMs = 45,
      AgentId = "local-agent"
    };

    result.TaskId.Should().Be(request.TaskId);
    result.Success.Should().BeTrue();
  }

  [Fact]
  public void TaskRequest_ElevatedCommand_SetsFlag()
  {
    var request = new TaskRequest
    {
      TaskId = "elevated-task",
      Type = TaskType.PowerShell,
      Command = "Set-ExecutionPolicy Unrestricted",
      RequiresElevation = true
    };

    request.RequiresElevation.Should().BeTrue();
  }

  [Fact]
  public void TaskResult_ElevationRequired_ReturnsError()
  {
    var result = new TaskResult
    {
      TaskId = "failed-elevation",
      Success = false,
      ExitCode = 1,
      ErrorCode = TaskErrorCode.ElevationRequired,
      ErrorMessage = "Administrator privileges required",
      DurationMs = 10,
      AgentId = "agent-1"
    };

    result.ErrorCode.Should().Be(TaskErrorCode.ElevationRequired);
    result.ErrorMessage.Should().Contain("Administrator");
  }

  #endregion
}
