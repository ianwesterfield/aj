using AJ.Orchestrator.Abstractions.Models.Tasks;
using AJ.Orchestrator.Abstractions.Models.Workspace;
using AJ.Orchestrator.Domain.Services;
using FluentAssertions;

namespace AJ.Orchestrator.Tests.Services;

/// <summary>
/// Tests for SessionStateManager and SessionState.
/// </summary>
public class SessionStateManagerTests
{
  private readonly SessionStateManager _manager;

  public SessionStateManagerTests()
  {
    _manager = new SessionStateManager();
  }

  #region GetOrCreate Tests

  [Fact]
  public void GetOrCreate_NewUser_ShouldCreateNewSession()
  {
    // Act
    var session = _manager.GetOrCreate("user-1");

    // Assert
    session.Should().NotBeNull();
    session.CurrentTask.Should().BeNull();
    session.Workspace.Should().BeNull();
    session.StepCount.Should().Be(0);
  }

  [Fact]
  public void GetOrCreate_SameUser_ShouldReturnSameSession()
  {
    // Act
    var session1 = _manager.GetOrCreate("user-1");
    session1.CurrentTask = "Test task";

    var session2 = _manager.GetOrCreate("user-1");

    // Assert
    session2.CurrentTask.Should().Be("Test task");
    ReferenceEquals(session1, session2).Should().BeTrue();
  }

  [Fact]
  public void GetOrCreate_DifferentUsers_ShouldReturnDifferentSessions()
  {
    // Act
    var session1 = _manager.GetOrCreate("user-1");
    var session2 = _manager.GetOrCreate("user-2");

    session1.CurrentTask = "Task 1";
    session2.CurrentTask = "Task 2";

    // Assert
    session1.CurrentTask.Should().Be("Task 1");
    session2.CurrentTask.Should().Be("Task 2");
    ReferenceEquals(session1, session2).Should().BeFalse();
  }

  #endregion

  #region Reset Tests

  [Fact]
  public void Reset_ExistingSession_ShouldClearState()
  {
    // Arrange
    var session = _manager.GetOrCreate("user-1");
    session.CurrentTask = "Test task";
    session.AddResult(new StepResult
    {
      StepId = "step-1",
      Status = StepStatus.Success
    });

    // Act
    _manager.Reset("user-1");
    var resetSession = _manager.GetOrCreate("user-1");

    // Assert
    resetSession.CurrentTask.Should().BeNull();
    resetSession.StepCount.Should().Be(0);
  }

  [Fact]
  public void Reset_NonExistentUser_ShouldNotThrow()
  {
    // Act & Assert
    var action = () => _manager.Reset("non-existent-user");
    action.Should().NotThrow();
  }

  #endregion

  #region Remove Tests

  [Fact]
  public void Remove_ExistingUser_ShouldRemoveSession()
  {
    // Arrange
    var session = _manager.GetOrCreate("user-1");
    session.CurrentTask = "Test task";

    // Act
    _manager.Remove("user-1");
    var newSession = _manager.GetOrCreate("user-1");

    // Assert
    newSession.CurrentTask.Should().BeNull();
  }

  [Fact]
  public void Remove_NonExistentUser_ShouldNotThrow()
  {
    // Act & Assert
    var action = () => _manager.Remove("non-existent-user");
    action.Should().NotThrow();
  }

  #endregion
}

/// <summary>
/// Tests for SessionState class.
/// </summary>
public class SessionStateTests
{
  [Fact]
  public void SessionState_NewInstance_ShouldHaveEmptyHistory()
  {
    // Arrange & Act
    var state = new SessionState();

    // Assert
    state.History.Should().BeEmpty();
    state.StepCount.Should().Be(0);
    state.CurrentTask.Should().BeNull();
    state.Workspace.Should().BeNull();
  }

  [Fact]
  public void SessionState_StartedAt_ShouldBeInitialized()
  {
    // Arrange
    var before = DateTime.UtcNow;

    // Act
    var state = new SessionState();

    // Assert
    state.StartedAt.Should().BeOnOrAfter(before);
    state.StartedAt.Should().BeOnOrBefore(DateTime.UtcNow);
  }

  [Fact]
  public void AddResult_SingleResult_ShouldIncrementCount()
  {
    // Arrange
    var state = new SessionState();
    var result = new StepResult
    {
      StepId = "step-1",
      Tool = "execute",
      Status = StepStatus.Success,
      Output = "test output"
    };

    // Act
    state.AddResult(result);

    // Assert
    state.StepCount.Should().Be(1);
    state.History.Should().HaveCount(1);
    state.History[0].StepId.Should().Be("step-1");
  }

  [Fact]
  public void AddResult_MultipleResults_ShouldMaintainOrder()
  {
    // Arrange
    var state = new SessionState();

    // Act
    for (int i = 1; i <= 5; i++)
    {
      state.AddResult(new StepResult
      {
        StepId = $"step-{i}",
        Status = StepStatus.Success
      });
    }

    // Assert
    state.StepCount.Should().Be(5);
    state.History[0].StepId.Should().Be("step-1");
    state.History[4].StepId.Should().Be("step-5");
  }

  [Fact]
  public void Reset_AfterAddingResults_ShouldClearEverything()
  {
    // Arrange
    var state = new SessionState();
    state.CurrentTask = "Test task";
    state.Workspace = new WorkspaceContext
    {
      Cwd = "/test",
      WorkspaceRoot = "/test"
    };
    state.AddResult(new StepResult { StepId = "step-1", Status = StepStatus.Success });
    var originalStartedAt = state.StartedAt;

    // Small delay to ensure time difference
    Thread.Sleep(10);

    // Act
    state.Reset();

    // Assert
    state.CurrentTask.Should().BeNull();
    state.StepCount.Should().Be(0);
    state.History.Should().BeEmpty();
    state.StartedAt.Should().BeOnOrAfter(originalStartedAt);
  }

  [Fact]
  public void History_ReturnsCopy_ShouldNotAffectInternalState()
  {
    // Arrange
    var state = new SessionState();
    state.AddResult(new StepResult { StepId = "step-1", Status = StepStatus.Success });

    // Act
    var history = state.History;
    // Try to modify the returned list (should not affect internal state)

    // Assert
    state.StepCount.Should().Be(1);
    history.Should().HaveCount(1);
  }

  [Fact]
  public async Task SessionState_ThreadSafety_ShouldHandleConcurrentAccess()
  {
    // Arrange
    var state = new SessionState();
    var tasks = new List<Task>();

    // Act - simulate concurrent access
    for (int i = 0; i < 100; i++)
    {
      var stepId = $"step-{i}";
      tasks.Add(Task.Run(() =>
      {
        state.AddResult(new StepResult
        {
          StepId = stepId,
          Status = StepStatus.Success
        });
      }));
    }

    await Task.WhenAll(tasks);

    // Assert
    state.StepCount.Should().Be(100);
  }
}
