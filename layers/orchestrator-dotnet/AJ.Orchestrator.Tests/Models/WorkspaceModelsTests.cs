using AJ.Orchestrator.Abstractions.Models.Workspace;
using FluentAssertions;

namespace AJ.Orchestrator.Tests.Models;

/// <summary>
/// Tests for workspace-related models.
/// </summary>
public class WorkspaceModelsTests
{
  [Fact]
  public void WorkspaceContext_DefaultValues_ShouldBeCorrect()
  {
    var context = new WorkspaceContext
    {
      Cwd = "/test",
      WorkspaceRoot = "/test"
    };

    context.ParallelEnabled.Should().BeFalse();
    context.MaxParallelTasks.Should().Be(4);
    context.AllowedLanguages.Should().BeEquivalentTo(new[] { "python", "powershell", "node" });
    context.AllowCodeExecution.Should().BeFalse();
    context.AllowFileWrite.Should().BeFalse();
    context.AllowShellCommands.Should().BeFalse();
    context.MaxExecutionTime.Should().Be(30);
    context.AvailablePaths.Should().BeEmpty();
  }

  [Fact]
  public void WorkspaceContext_WithCustomValues_ShouldRetainThem()
  {
    var context = new WorkspaceContext
    {
      Cwd = "/home/user/project",
      WorkspaceRoot = "/home/user",
      ParallelEnabled = true,
      MaxParallelTasks = 8,
      AllowedLanguages = new List<string> { "python" },
      AllowCodeExecution = true,
      AllowFileWrite = true,
      AllowShellCommands = true,
      MaxExecutionTime = 60,
      AvailablePaths = new List<string> { "src", "tests", "docs" }
    };

    context.Cwd.Should().Be("/home/user/project");
    context.WorkspaceRoot.Should().Be("/home/user");
    context.ParallelEnabled.Should().BeTrue();
    context.MaxParallelTasks.Should().Be(8);
    context.AllowedLanguages.Should().BeEquivalentTo(new[] { "python" });
    context.AllowCodeExecution.Should().BeTrue();
    context.AllowFileWrite.Should().BeTrue();
    context.AllowShellCommands.Should().BeTrue();
    context.MaxExecutionTime.Should().Be(60);
    context.AvailablePaths.Should().BeEquivalentTo(new[] { "src", "tests", "docs" });
  }

  [Fact]
  public void SetWorkspaceRequest_ShouldStoreValues()
  {
    var request = new SetWorkspaceRequest { Cwd = "/home/project", UserId = "user-123" };

    request.Cwd.Should().Be("/home/project");
    request.UserId.Should().Be("user-123");
  }

  [Fact]
  public void SetWorkspaceRequest_WithNullUserId_ShouldBeValid()
  {
    var request = new SetWorkspaceRequest { Cwd = "/home/project" };

    request.Cwd.Should().Be("/home/project");
    request.UserId.Should().BeNull();
  }

  [Fact]
  public void SetWorkspaceResponse_Success_ShouldHaveCorrectProperties()
  {
    var response = new SetWorkspaceResponse
    {
      Success = true,
      WorkspacePath = "/home/project",
      SessionId = "session-123"
    };

    response.Success.Should().BeTrue();
    response.WorkspacePath.Should().Be("/home/project");
    response.SessionId.Should().Be("session-123");
    response.Error.Should().BeNull();
  }

  [Fact]
  public void SetWorkspaceResponse_Failure_ShouldHaveError()
  {
    var response = new SetWorkspaceResponse
    {
      Success = false,
      Error = "Directory not found"
    };

    response.Success.Should().BeFalse();
    response.Error.Should().Be("Directory not found");
    response.WorkspacePath.Should().BeNull();
  }

  [Fact]
  public void CloneWorkspaceRequest_ShouldStoreAllValues()
  {
    var request = new CloneWorkspaceRequest
    {
      RepoUrl = "https://github.com/user/repo.git",
      Branch = "develop",
      TargetName = "my-repo",
      UserId = "user-123"
    };

    request.RepoUrl.Should().Be("https://github.com/user/repo.git");
    request.Branch.Should().Be("develop");
    request.TargetName.Should().Be("my-repo");
    request.UserId.Should().Be("user-123");
  }

  [Fact]
  public void CloneWorkspaceRequest_WithDefaults_ShouldHaveNullOptionals()
  {
    var request = new CloneWorkspaceRequest { RepoUrl = "https://github.com/user/repo.git" };

    request.RepoUrl.Should().Be("https://github.com/user/repo.git");
    request.Branch.Should().BeNull();
    request.TargetName.Should().BeNull();
    request.UserId.Should().BeNull();
  }

  [Fact]
  public void CloneWorkspaceResponse_Success_ShouldHaveContext()
  {
    var context = new WorkspaceContext
    {
      Cwd = "/home/user/repo",
      WorkspaceRoot = "/home/user/repo"
    };

    var response = new CloneWorkspaceResponse(
        true,
        "/home/user/repo",
        "Cloned successfully",
        context
    );

    response.Success.Should().BeTrue();
    response.WorkspacePath.Should().Be("/home/user/repo");
    response.Message.Should().Be("Cloned successfully");
    response.Context.Should().NotBeNull();
    response.Context!.Cwd.Should().Be("/home/user/repo");
  }

  [Fact]
  public void CloneWorkspaceResponse_Failure_ShouldHaveErrorMessage()
  {
    var response = new CloneWorkspaceResponse(
        false,
        "",
        "Authentication failed"
    );

    response.Success.Should().BeFalse();
    response.WorkspacePath.Should().BeEmpty();
    response.Message.Should().Be("Authentication failed");
    response.Context.Should().BeNull();
  }
}
