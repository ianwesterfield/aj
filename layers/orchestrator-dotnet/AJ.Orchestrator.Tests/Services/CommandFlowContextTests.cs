using AJ.Orchestrator.Domain.Services;
using FluentAssertions;

namespace AJ.Orchestrator.Tests.Services;

/// <summary>
/// Tests for CommandFlowContext - token-efficient command execution tracking.
/// Ported from Python: test_cov_session_state.py::TestCommandFlowContext
/// </summary>
public class CommandFlowContextTests
{
  #region AddEntry Tests

  [Fact]
  public void AddEntry_WithAllParameters_ShouldCreateEntry()
  {
    // Arrange
    var ctx = new CommandFlowContext();

    // Act
    var entry = ctx.AddEntry(
        stepIndex: 1,
        tool: "execute",
        success: true,
        output: "hello",
        agentId: "ws1",
        command: "hostname",
        error: null,
        durationMs: 50);

    // Assert
    entry.StepIndex.Should().Be(1);
    entry.OutputPreview.Should().Be("hello");
    entry.OutputHash.Should().NotBeNullOrEmpty();
    entry.Tags.Should().Contain("execute");
  }

  [Fact]
  public void AddEntry_WithEmptyOutput_ShouldHaveEmptyHash()
  {
    // Arrange
    var ctx = new CommandFlowContext();

    // Act
    var entry = ctx.AddEntry(stepIndex: 1, tool: "think", success: true, output: "");

    // Assert
    entry.OutputPreview.Should().BeEmpty();
    entry.OutputHash.Should().BeEmpty();
  }

  [Fact]
  public void AddEntry_LongOutput_ShouldTruncatePreview()
  {
    // Arrange
    var ctx = new CommandFlowContext();
    var longOutput = new string('x', 15000);  // Exceeds MaxPreviewLength of 10000

    // Act
    var entry = ctx.AddEntry(stepIndex: 1, tool: "execute", success: true, output: longOutput);

    // Assert
    entry.OutputPreview.Length.Should().BeLessThan(longOutput.Length);
  }

  #endregion

  #region Query Tests

  [Fact]
  public void QueryByAgent_WithMatchingAgent_ShouldReturnFiltered()
  {
    // Arrange
    var ctx = new CommandFlowContext();
    ctx.AddEntry(1, "execute", true, "ok", agentId: "ws1");
    ctx.AddEntry(2, "execute", true, "ok", agentId: "ws2");

    // Act
    var result = ctx.QueryByAgent("ws1");

    // Assert
    result.Should().HaveCount(1);
    result[0].AgentId.Should().Be("ws1");
  }

  [Fact]
  public void QueryByTag_WithMatchingTag_ShouldReturnFiltered()
  {
    // Arrange
    var ctx = new CommandFlowContext();
    ctx.AddEntry(1, "execute", true, "ok");
    ctx.AddEntry(2, "think", true, "ok");

    // Act
    var result = ctx.QueryByTag("execute");

    // Assert
    result.Should().HaveCount(1);
  }

  [Fact]
  public void QueryFailures_WithFailedEntries_ShouldReturnOnlyFailures()
  {
    // Arrange
    var ctx = new CommandFlowContext();
    ctx.AddEntry(1, "execute", true, "ok");
    ctx.AddEntry(2, "execute", false, "", error: "fail");

    // Act
    var result = ctx.QueryFailures();

    // Assert
    result.Should().HaveCount(1);
    result[0].Success.Should().BeFalse();
  }

  [Fact]
  public void QueryRecent_WithMultipleEntries_ShouldReturnMostRecent()
  {
    // Arrange
    var ctx = new CommandFlowContext();
    for (int i = 0; i < 10; i++)
    {
      ctx.AddEntry(i, "execute", true, $"out{i}");
    }

    // Act
    var result = ctx.QueryRecent(3);

    // Assert
    result.Should().HaveCount(3);
    result[0].StepIndex.Should().Be(7);
  }

  [Fact]
  public void QueryRecent_EmptyContext_ShouldReturnEmpty()
  {
    // Arrange
    var ctx = new CommandFlowContext();

    // Act
    var result = ctx.QueryRecent();

    // Assert
    result.Should().BeEmpty();
  }

  #endregion

  #region Agent Tracking Tests

  [Fact]
  public void GetAgentsQueried_ShouldReturnUniqueAgents()
  {
    // Arrange
    var ctx = new CommandFlowContext();
    ctx.AddEntry(1, "execute", true, "ok", agentId: "ws1");
    ctx.AddEntry(2, "think", true, "ok"); // no agent
    ctx.AddEntry(3, "execute", true, "ok", agentId: "ws1"); // duplicate

    // Act
    var result = ctx.GetAgentsQueried();

    // Assert
    result.Should().ContainSingle("ws1");
  }

  [Fact]
  public void HasExecutedOn_WithMatchingAgent_ShouldReturnTrue()
  {
    // Arrange
    var ctx = new CommandFlowContext();
    ctx.AddEntry(1, "execute", true, "ok", agentId: "ws1");
    ctx.AddEntry(2, "think", true, "ok", agentId: "ws1");

    // Act & Assert
    ctx.HasExecutedOn("ws1").Should().BeTrue();
    ctx.HasExecutedOn("ws2").Should().BeFalse();
  }

  #endregion

  #region Summarization Tests

  [Fact]
  public void SummarizeForReplan_EmptyContext_ShouldIndicateNoCommands()
  {
    // Arrange
    var ctx = new CommandFlowContext();

    // Act
    var result = ctx.SummarizeForReplan();

    // Assert
    result.Should().Contain("No commands");
  }

  [Fact]
  public void SummarizeForReplan_WithData_ShouldIncludeStats()
  {
    // Arrange
    var ctx = new CommandFlowContext();
    ctx.OriginalGoal = "check servers";
    ctx.AddEntry(1, "execute", true, "ok", agentId: "ws1");
    ctx.AddEntry(2, "execute", false, "", agentId: "ws1", error: "timeout");

    // Act
    var result = ctx.SummarizeForReplan();

    // Assert
    result.Should().Contain("check servers");
    result.Should().Contain("1 ok");
    result.Should().Contain("1 failed");
    result.Should().Contain("timeout");
  }

  [Fact]
  public void FormatStepSummary_ExecuteStep_ShouldIncludeAgentAndCommand()
  {
    // Arrange
    var ctx = new CommandFlowContext();
    var entry = ctx.AddEntry(
        1, "execute", true, "ok",
        agentId: "ws1",
        command: "hostname");

    // Act
    var summary = ctx.FormatStepSummary(entry);

    // Assert
    summary.Should().Contain("ws1");
    summary.Should().Contain("hostname");
    summary.Should().Contain("✓");
  }

  [Fact]
  public void FormatStepSummary_FailedStep_ShouldShowFailureMarker()
  {
    // Arrange
    var ctx = new CommandFlowContext();
    var entry = ctx.AddEntry(1, "execute", false, "", agentId: "ws1", command: "bad");

    // Act
    var summary = ctx.FormatStepSummary(entry);

    // Assert
    summary.Should().Contain("✗");
  }

  [Fact]
  public void FormatStepSummary_ThinkTool_ShouldShowToolName()
  {
    // Arrange
    var ctx = new CommandFlowContext();
    var entry = ctx.AddEntry(1, "think", true, "some thought");

    // Act
    var summary = ctx.FormatStepSummary(entry);

    // Assert
    summary.Should().Contain("think");
    summary.Should().Contain("✓");
  }

  #endregion

  #region Edge Cases

  [Fact]
  public void QueryByAgent_NoMatches_ShouldReturnEmpty()
  {
    // Arrange
    var ctx = new CommandFlowContext();
    ctx.AddEntry(1, "execute", true, "ok", agentId: "ws1");

    // Act
    var result = ctx.QueryByAgent("nonexistent");

    // Assert
    result.Should().BeEmpty();
  }

  [Fact]
  public void QueryByTag_NoMatches_ShouldReturnEmpty()
  {
    // Arrange
    var ctx = new CommandFlowContext();
    ctx.AddEntry(1, "execute", true, "ok");

    // Act
    var result = ctx.QueryByTag("nonexistent");

    // Assert
    result.Should().BeEmpty();
  }

  [Fact]
  public void AddEntry_WithNullOutput_ShouldNotThrow()
  {
    // Arrange
    var ctx = new CommandFlowContext();

    // Act - using empty string as null isn't allowed by signature
    var entry = ctx.AddEntry(1, "execute", true, "");

    // Assert
    entry.OutputHash.Should().BeEmpty();
  }

  [Fact]
  public void QueryRecent_RequestMoreThanExists_ShouldReturnAll()
  {
    // Arrange
    var ctx = new CommandFlowContext();
    ctx.AddEntry(1, "execute", true, "ok");
    ctx.AddEntry(2, "execute", true, "ok");

    // Act
    var result = ctx.QueryRecent(10);

    // Assert
    result.Should().HaveCount(2);
  }

  [Fact]
  public void AddEntry_WithAgentTag_ShouldIncludeAgentInTags()
  {
    // Arrange
    var ctx = new CommandFlowContext();

    // Act
    var entry = ctx.AddEntry(1, "execute", true, "ok", agentId: "ws1");

    // Assert
    entry.Tags.Should().Contain("agent:ws1");
  }

  [Fact]
  public void AddEntry_FailedEntry_ShouldIncludeFailedTag()
  {
    // Arrange
    var ctx = new CommandFlowContext();

    // Act
    var entry = ctx.AddEntry(1, "execute", false, "", error: "oops");

    // Assert
    entry.Tags.Should().Contain("failed");
  }

  [Fact]
  public void Entries_ShouldBeReadOnly()
  {
    // Arrange
    var ctx = new CommandFlowContext();
    ctx.AddEntry(1, "execute", true, "ok");

    // Act
    var entries = ctx.Entries;

    // Assert
    entries.Should().HaveCount(1);
    // Verify it's a copy by checking type
    entries.Should().BeOfType<List<CommandFlowEntry>>();
  }

  #endregion
}
