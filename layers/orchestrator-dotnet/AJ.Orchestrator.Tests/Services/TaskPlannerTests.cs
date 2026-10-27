using AJ.Orchestrator.Domain.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AJ.Orchestrator.Tests.Services;

/// <summary>
/// Tests for TaskPlanner service.
/// </summary>
public class TaskPlannerTests
{
  private readonly Mock<ILogger<TaskPlanner>> _loggerMock;
  private readonly TaskPlanner _planner;

  public TaskPlannerTests()
  {
    _loggerMock = new Mock<ILogger<TaskPlanner>>();
    _planner = new TaskPlanner(_loggerMock.Object);
  }

  #region SetWorkspaceAsync Tests

  [Fact]
  public async Task SetWorkspaceAsync_ValidDirectory_ShouldReturnContext()
  {
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    Directory.CreateDirectory(tempDir);

    try
    {
      // Act
      var context = await _planner.SetWorkspaceAsync(tempDir);

      // Assert
      context.Should().NotBeNull();
      context.Cwd.Should().Be(tempDir);
      context.WorkspaceRoot.Should().Be(tempDir);
      context.AllowShellCommands.Should().BeTrue();
      context.AllowFileWrite.Should().BeTrue();
      context.AllowCodeExecution.Should().BeTrue();
    }
    finally
    {
      Directory.Delete(tempDir, true);
    }
  }

  [Fact]
  public async Task SetWorkspaceAsync_InvalidDirectory_ShouldThrowException()
  {
    // Arrange
    var nonExistentDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    // Act & Assert
    await Assert.ThrowsAsync<DirectoryNotFoundException>(
        () => _planner.SetWorkspaceAsync(nonExistentDir));
  }

  [Fact]
  public async Task SetWorkspaceAsync_WithUserId_ShouldSetWorkspace()
  {
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    Directory.CreateDirectory(tempDir);

    try
    {
      // Act
      var context = await _planner.SetWorkspaceAsync(tempDir, "user-123");

      // Assert
      context.Should().NotBeNull();
      context.Cwd.Should().Be(tempDir);
    }
    finally
    {
      Directory.Delete(tempDir, true);
    }
  }

  [Fact]
  public async Task SetWorkspaceAsync_DirectoryWithFiles_ShouldPopulateAvailablePaths()
  {
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    Directory.CreateDirectory(tempDir);
    File.WriteAllText(Path.Combine(tempDir, "file1.txt"), "content");
    File.WriteAllText(Path.Combine(tempDir, "file2.txt"), "content");
    Directory.CreateDirectory(Path.Combine(tempDir, "subdir"));

    try
    {
      // Act
      var context = await _planner.SetWorkspaceAsync(tempDir);

      // Assert
      context.AvailablePaths.Should().HaveCountGreaterThanOrEqualTo(3);
      context.AvailablePaths.Should().Contain("file1.txt");
      context.AvailablePaths.Should().Contain("file2.txt");
      context.AvailablePaths.Should().Contain("subdir");
    }
    finally
    {
      Directory.Delete(tempDir, true);
    }
  }

  #endregion

  #region GetCurrentWorkspace Tests

  [Fact]
  public void GetCurrentWorkspace_NoWorkspaceSet_ShouldReturnNull()
  {
    // Act
    var workspace = _planner.GetCurrentWorkspace();

    // Assert
    workspace.Should().BeNull();
  }

  [Fact]
  public async Task GetCurrentWorkspace_AfterSetWorkspace_ShouldReturnContext()
  {
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    Directory.CreateDirectory(tempDir);

    try
    {
      await _planner.SetWorkspaceAsync(tempDir);

      // Act
      var workspace = _planner.GetCurrentWorkspace();

      // Assert
      workspace.Should().NotBeNull();
      workspace!.Cwd.Should().Be(tempDir);
    }
    finally
    {
      Directory.Delete(tempDir, true);
    }
  }

  [Fact]
  public async Task SetWorkspaceAsync_MultipleCalls_ShouldUpdateCurrentWorkspace()
  {
    // Arrange
    var tempDir1 = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    var tempDir2 = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    Directory.CreateDirectory(tempDir1);
    Directory.CreateDirectory(tempDir2);

    try
    {
      // Act
      await _planner.SetWorkspaceAsync(tempDir1);
      var first = _planner.GetCurrentWorkspace();

      await _planner.SetWorkspaceAsync(tempDir2);
      var second = _planner.GetCurrentWorkspace();

      // Assert
      first!.Cwd.Should().Be(tempDir1);
      second!.Cwd.Should().Be(tempDir2);
    }
    finally
    {
      Directory.Delete(tempDir1, true);
      Directory.Delete(tempDir2, true);
    }
  }

  #endregion

  #region CloneWorkspaceAsync Tests

  [Fact]
  public async Task CloneWorkspaceAsync_InvalidRepoUrl_ShouldReturnFailure()
  {
    // Arrange
    var request = new AJ.Orchestrator.Abstractions.Models.Workspace.CloneWorkspaceRequest
    {
      RepoUrl = "https://invalid-url-that-does-not-exist.git"
    };

    // Act
    var result = await _planner.CloneWorkspaceAsync(request);

    // Assert
    result.Success.Should().BeFalse();
    result.Message.Should().NotBeEmpty();
  }

  #endregion
}
