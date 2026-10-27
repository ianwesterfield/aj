using AJ.Orchestrator.Abstractions.Models.Tasks;
using AJ.Orchestrator.Abstractions.Models.Workspace;
using FluentAssertions;

namespace AJ.Orchestrator.Tests.Models;

/// <summary>
/// Tests for task execution models.
/// </summary>
public class TaskModelsTests
{
  #region Step Tests

  [Fact]
  public void Step_DefaultValues_ShouldBeCorrect()
  {
    var step = new Step
    {
      StepId = "step-1",
      Tool = "execute"
    };

    step.Params.Should().BeEmpty();
    step.BatchId.Should().BeNull();
    step.Reasoning.Should().BeEmpty();
    step.Status.Should().Be(StepStatus.Pending);
    step.DependsOn.Should().BeEmpty();
  }

  [Fact]
  public void Step_WithAllProperties_ShouldRetainValues()
  {
    var step = new Step
    {
      StepId = "step-1",
      Tool = "execute",
      Params = new Dictionary<string, object?> { ["command"] = "ls -la", ["agent_id"] = "local" },
      BatchId = "batch-1",
      Reasoning = "List directory contents",
      Status = StepStatus.Running,
      DependsOn = new List<string> { "step-0" }
    };

    step.StepId.Should().Be("step-1");
    step.Tool.Should().Be("execute");
    step.Params.Should().ContainKey("command");
    step.Params["command"].Should().Be("ls -la");
    step.BatchId.Should().Be("batch-1");
    step.Reasoning.Should().Be("List directory contents");
    step.Status.Should().Be(StepStatus.Running);
    step.DependsOn.Should().Contain("step-0");
  }

  [Theory]
  [InlineData(StepStatus.Pending)]
  [InlineData(StepStatus.Running)]
  [InlineData(StepStatus.Success)]
  [InlineData(StepStatus.Failed)]
  [InlineData(StepStatus.Skipped)]
  public void StepStatus_AllValues_ShouldBeValid(StepStatus status)
  {
    var step = new Step
    {
      StepId = "test",
      Tool = "execute",
      Status = status
    };

    step.Status.Should().Be(status);
  }

  #endregion

  #region StepResult Tests

  [Fact]
  public void StepResult_SuccessfulExecution_ShouldHaveOutput()
  {
    var result = new StepResult
    {
      StepId = "step-1",
      Tool = "execute",
      Params = new Dictionary<string, object?> { ["command"] = "echo hello" },
      Status = StepStatus.Success,
      Output = "hello\n",
      ExecutionTime = 0.5
    };

    result.StepId.Should().Be("step-1");
    result.Status.Should().Be(StepStatus.Success);
    result.Output.Should().Be("hello\n");
    result.Error.Should().BeNull();
    result.ExecutionTime.Should().Be(0.5);
  }

  [Fact]
  public void StepResult_FailedExecution_ShouldHaveError()
  {
    var result = new StepResult
    {
      StepId = "step-1",
      Tool = "execute",
      Status = StepStatus.Failed,
      Error = "Command not found",
      ExecutionTime = 0.1
    };

    result.Status.Should().Be(StepStatus.Failed);
    result.Error.Should().Be("Command not found");
    result.Output.Should().BeNull();
  }

  #endregion

  #region ErrorMetadata Tests

  [Fact]
  public void ErrorMetadata_DefaultValues_ShouldBeCorrect()
  {
    var error = new ErrorMetadata
    {
      StepId = "step-1",
      Error = "Something went wrong"
    };

    error.ErrorType.Should().Be(ErrorType.Unknown);
    error.Recoverable.Should().BeFalse();
    error.Suggestion.Should().BeNull();
  }

  [Theory]
  [InlineData(ErrorType.Timeout, "Increase timeout")]
  [InlineData(ErrorType.PermissionDenied, "Check permissions")]
  [InlineData(ErrorType.InvalidParams, "Fix parameters")]
  [InlineData(ErrorType.ExecutionError, "Check command")]
  [InlineData(ErrorType.SandboxViolation, "Stay within sandbox")]
  [InlineData(ErrorType.ResourceLimit, "Reduce resource usage")]
  public void ErrorMetadata_WithSuggestion_ShouldRetainValues(ErrorType errorType, string suggestion)
  {
    var error = new ErrorMetadata
    {
      StepId = "step-1",
      Error = "Error occurred",
      ErrorType = errorType,
      Recoverable = true,
      Suggestion = suggestion
    };

    error.ErrorType.Should().Be(errorType);
    error.Recoverable.Should().BeTrue();
    error.Suggestion.Should().Be(suggestion);
  }

  #endregion

  #region RunTaskRequest Tests

  [Fact]
  public void RunTaskRequest_WithDefaults_ShouldHaveCorrectValues()
  {
    var request = new RunTaskRequest { Task = "List all files", WorkspaceRoot = "/home/user" };

    request.Task.Should().Be("List all files");
    request.WorkspaceRoot.Should().Be("/home/user");
    request.UserId.Should().BeNull();
    request.MemoryContext.Should().BeNull();
    request.MaxSteps.Should().Be(15);
    request.PreserveState.Should().BeFalse();
    request.Model.Should().BeNull();
  }

  [Fact]
  public void RunTaskRequest_WithAllOptions_ShouldRetainValues()
  {
    var memoryContext = new List<Dictionary<string, object?>>
        {
            new() { ["role"] = "user", ["content"] = "previous message" }
        };

    var request = new RunTaskRequest
    {
      Task = "Complex task",
      WorkspaceRoot = "/workspace",
      UserId = "user-123",
      MemoryContext = memoryContext,
      MaxSteps = 50,
      PreserveState = true,
      Model = "gpt-4"
    };

    request.Task.Should().Be("Complex task");
    request.WorkspaceRoot.Should().Be("/workspace");
    request.UserId.Should().Be("user-123");
    request.MemoryContext.Should().HaveCount(1);
    request.MaxSteps.Should().Be(50);
    request.PreserveState.Should().BeTrue();
    request.Model.Should().Be("gpt-4");
  }

  #endregion

  #region TaskEvent Tests

  [Fact]
  public void TaskEvent_StatusEvent_ShouldHaveCorrectType()
  {
    var evt = new TaskEvent
    {
      EventType = "status",
      StepNum = 1,
      Status = "Reasoning..."
    };

    evt.EventType.Should().Be("status");
    evt.Type.Should().Be("status"); // Alias
    evt.StepNum.Should().Be(1);
    evt.Done.Should().BeFalse();
  }

  [Fact]
  public void TaskEvent_StepEvent_ShouldIncludeResult()
  {
    var result = new Dictionary<string, object?>
    {
      ["tool"] = "execute",
      ["params"] = new Dictionary<string, object?> { ["command"] = "ls" }
    };

    var evt = new TaskEvent
    {
      EventType = "step",
      StepNum = 2,
      Tool = "execute",
      Status = "Executing",
      Result = result
    };

    evt.EventType.Should().Be("step");
    evt.Tool.Should().Be("execute");
    evt.Result.Should().ContainKey("tool");
    evt.Done.Should().BeFalse();
  }

  [Fact]
  public void TaskEvent_CompleteEvent_ShouldBeDone()
  {
    var evt = new TaskEvent
    {
      EventType = "complete",
      StepNum = 3,
      Tool = "complete",
      Status = "Task completed",
      Done = true,
      Result = new Dictionary<string, object?> { ["answer"] = "Here is the result" }
    };

    evt.EventType.Should().Be("complete");
    evt.Done.Should().BeTrue();
    evt.Result!["answer"].Should().Be("Here is the result");
  }

  [Fact]
  public void TaskEvent_ErrorEvent_ShouldHaveErrorMessage()
  {
    var evt = new TaskEvent
    {
      EventType = "error",
      StepNum = 1,
      Status = "Failed",
      Error = "Timeout exceeded",
      Done = true
    };

    evt.EventType.Should().Be("error");
    evt.Error.Should().Be("Timeout exceeded");
    evt.Done.Should().BeTrue();
  }

  #endregion

  #region NextStepRequest/Response Tests

  [Fact]
  public void NextStepRequest_WithHistory_ShouldStoreResults()
  {
    var history = new List<StepResult>
        {
            new()
            {
                StepId = "step-1",
                Tool = "execute",
                Status = StepStatus.Success,
                Output = "result"
            }
        };

    var workspace = new WorkspaceContext
    {
      Cwd = "/project",
      WorkspaceRoot = "/project"
    };

    var request = new NextStepRequest
    {
      Task = "Continue task",
      History = history,
      WorkspaceContext = workspace,
      UserId = "user-1"
    };

    request.Task.Should().Be("Continue task");
    request.History.Should().HaveCount(1);
    request.WorkspaceContext.Should().NotBeNull();
    request.UserId.Should().Be("user-1");
  }

  [Fact]
  public void NextStepResponse_ExecuteTool_ShouldHaveParams()
  {
    var response = new NextStepResponse(
        "execute",
        new Dictionary<string, object?> { ["command"] = "ls", ["agent_id"] = "local" },
        null,
        "List files to understand structure"
    );

    response.Tool.Should().Be("execute");
    response.Params.Should().ContainKeys("command", "agent_id");
    response.Reasoning.Should().Contain("List files");
    response.IsBatch.Should().BeFalse();
  }

  [Fact]
  public void NextStepResponse_BatchExecution_ShouldHaveBatchId()
  {
    var response = new NextStepResponse(
        "execute",
        new Dictionary<string, object?> { ["command"] = "cmd1" },
        "batch-1",
        "Part of batch execution",
        true
    );

    response.BatchId.Should().Be("batch-1");
    response.IsBatch.Should().BeTrue();
  }

  #endregion
}
