using AJ.Orchestrator.Abstractions.Models.Batch;
using AJ.Orchestrator.Abstractions.Models.Tasks;
using AJ.Orchestrator.Abstractions.Models.Workspace;
using FluentAssertions;

namespace AJ.Orchestrator.Tests.Models;

/// <summary>
/// Tests for batch execution models.
/// </summary>
public class BatchModelsTests
{
  [Fact]
  public void ExecuteBatchRequest_ShouldStoreSteps()
  {
    var steps = new List<Step>
        {
            new() { StepId = "step-1", Tool = "execute" },
            new() { StepId = "step-2", Tool = "execute" }
        };

    var workspace = new WorkspaceContext
    {
      Cwd = "/project",
      WorkspaceRoot = "/project"
    };

    var request = new ExecuteBatchRequest(steps, "batch-1", workspace, "user-123");

    request.Steps.Should().HaveCount(2);
    request.BatchId.Should().Be("batch-1");
    request.WorkspaceContext.Should().NotBeNull();
    request.UserId.Should().Be("user-123");
  }

  [Fact]
  public void ExecuteBatchRequest_WithDefaults_ShouldHaveNullOptionals()
  {
    var steps = new List<Step> { new() { StepId = "step-1", Tool = "think" } };
    var request = new ExecuteBatchRequest(steps, "batch-1");

    request.WorkspaceContext.Should().BeNull();
    request.UserId.Should().BeNull();
  }

  [Fact]
  public void BatchResult_EmptyBatch_ShouldHaveZeroCounts()
  {
    var result = new BatchResult
    {
      BatchId = "batch-1",
      Duration = 0.0
    };

    result.BatchId.Should().Be("batch-1");
    result.Successful.Should().BeEmpty();
    result.Failed.Should().BeEmpty();
    result.SuccessfulCount.Should().Be(0);
    result.FailedCount.Should().Be(0);
  }

  [Fact]
  public void BatchResult_WithSuccessfulSteps_ShouldCountThem()
  {
    var successful = new List<StepResult>
        {
            new() { StepId = "step-1", Status = StepStatus.Success },
            new() { StepId = "step-2", Status = StepStatus.Success }
        };

    var result = new BatchResult
    {
      BatchId = "batch-1",
      Successful = successful,
      Duration = 1.5
    };

    result.SuccessfulCount.Should().Be(2);
    result.FailedCount.Should().Be(0);
    result.Duration.Should().Be(1.5);
  }

  [Fact]
  public void BatchResult_WithFailedSteps_ShouldCountThem()
  {
    var failed = new List<ErrorMetadata>
        {
            new() { StepId = "step-1", Error = "Timeout", ErrorType = ErrorType.Timeout },
            new() { StepId = "step-2", Error = "Permission denied", ErrorType = ErrorType.PermissionDenied }
        };

    var result = new BatchResult
    {
      BatchId = "batch-1",
      Failed = failed,
      Duration = 30.0
    };

    result.SuccessfulCount.Should().Be(0);
    result.FailedCount.Should().Be(2);
  }

  [Fact]
  public void BatchResult_MixedResults_ShouldCountBoth()
  {
    var successful = new List<StepResult>
        {
            new() { StepId = "step-1", Status = StepStatus.Success },
            new() { StepId = "step-3", Status = StepStatus.Success }
        };

    var failed = new List<ErrorMetadata>
        {
            new() { StepId = "step-2", Error = "Failed" }
        };

    var result = new BatchResult
    {
      BatchId = "batch-1",
      Successful = successful,
      Failed = failed,
      Duration = 5.0
    };

    result.SuccessfulCount.Should().Be(2);
    result.FailedCount.Should().Be(1);
  }
}
