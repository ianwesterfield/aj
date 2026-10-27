using FluentAssertions;
using AJ.Orchestrator.Domain.Services;
using Xunit;

namespace AJ.Orchestrator.Tests.Services;

/// <summary>
/// Tests for TaskPlan - ported from Python test_cov_session_state.py
/// </summary>
public class TaskPlanTests
{
  [Fact]
  public void AddItem_CreatesItemWithIndex()
  {
    var plan = new TaskPlan();
    plan.AddItem("Step 1", toolHint: "execute");

    plan.Items.Should().HaveCount(1);
    plan.Items[0].Index.Should().Be(1);
  }

  [Fact]
  public void MarkInProgress_SetsStatus()
  {
    var plan = new TaskPlan();
    plan.AddItem("Step 1");
    plan.MarkInProgress(1);

    plan.Items[0].Status.Should().Be("in_progress");
  }

  [Fact]
  public void MarkCompleted_SetsStatus()
  {
    var plan = new TaskPlan();
    plan.AddItem("Step 1");
    plan.MarkCompleted(1);

    plan.Items[0].Status.Should().Be("completed");
  }

  [Fact]
  public void MarkSkipped_WithReason_SetsStatusAndAppendsReason()
  {
    var plan = new TaskPlan();
    plan.AddItem("Step 1");
    plan.MarkSkipped(1, "not needed");

    plan.Items[0].Status.Should().Be("skipped");
    plan.Items[0].Description.Should().Contain("not needed");
  }

  [Fact]
  public void MarkSkipped_NoReason_SetsStatusOnly()
  {
    var plan = new TaskPlan();
    plan.AddItem("Step 1");
    plan.MarkSkipped(1);

    plan.Items[0].Status.Should().Be("skipped");
  }

  [Fact]
  public void GetCurrentItem_ReturnsNextPending()
  {
    var plan = new TaskPlan();
    plan.AddItem("A");
    plan.AddItem("B");
    plan.MarkCompleted(1);

    var current = plan.GetCurrentItem();

    current.Should().NotBeNull();
    current!.Description.Should().Be("B");
  }

  [Fact]
  public void GetCurrentItem_AllComplete_ReturnsNull()
  {
    var plan = new TaskPlan();
    plan.AddItem("A");
    plan.MarkCompleted(1);

    plan.GetCurrentItem().Should().BeNull();
  }

  [Fact]
  public void GetProgress_ReturnsTuple()
  {
    var plan = new TaskPlan();
    plan.AddItem("A");
    plan.AddItem("B");
    plan.MarkCompleted(1);

    var (completed, total) = plan.GetProgress();

    completed.Should().Be(1);
    total.Should().Be(2);
  }

  [Fact]
  public void IsComplete_AllPending_ReturnsFalse()
  {
    var plan = new TaskPlan();
    plan.AddItem("A");
    plan.AddItem("B");

    plan.IsComplete().Should().BeFalse();
  }

  [Fact]
  public void IsComplete_AllDone_ReturnsTrue()
  {
    var plan = new TaskPlan();
    plan.AddItem("A");
    plan.AddItem("B");
    plan.MarkCompleted(1);
    plan.MarkSkipped(2);

    plan.IsComplete().Should().BeTrue();
  }

  [Fact]
  public void FormatForDisplay_ShowsIcons()
  {
    var plan = new TaskPlan();
    plan.AddItem("Step A");
    plan.AddItem("Step B");
    plan.MarkCompleted(1);
    plan.MarkInProgress(2);

    var display = plan.FormatForDisplay();

    display.Should().Contain("✅");
    display.Should().Contain("⏳");
  }

  [Fact]
  public void FormatForDisplay_Empty_ReturnsEmptyString()
  {
    var plan = new TaskPlan();

    plan.FormatForDisplay().Should().BeEmpty();
  }

  [Fact]
  public void FormatForDisplay_Skipped_ShowsSkipIcon()
  {
    var plan = new TaskPlan();
    plan.AddItem("Skippable");
    plan.MarkSkipped(1);

    var display = plan.FormatForDisplay();

    display.Should().Contain("⏭");
  }

  [Fact]
  public void FormatForDisplay_UnknownStatus_ShowsBullet()
  {
    var plan = new TaskPlan();
    plan.AddItem("Item");
    plan.Items[0].Status = "unknown_status";

    var display = plan.FormatForDisplay();

    display.Should().Contain("•");
  }

  [Fact]
  public void FormatForPrompt_ShowsTodoAndDone()
  {
    var plan = new TaskPlan();
    plan.AddItem("Step A");
    plan.AddItem("Step B");
    plan.MarkCompleted(1);

    var prompt = plan.FormatForPrompt();

    prompt.Should().Contain("DONE");
    prompt.Should().Contain("TODO");
    prompt.Should().Contain("CURRENT TASK");
  }

  [Fact]
  public void FormatForPrompt_AllDone_ShowsAllComplete()
  {
    var plan = new TaskPlan();
    plan.AddItem("A");
    plan.MarkCompleted(1);

    var prompt = plan.FormatForPrompt();

    prompt.Should().Contain("ALL STEPS COMPLETE");
  }

  [Fact]
  public void FormatForPrompt_Empty_ReturnsEmptyString()
  {
    var plan = new TaskPlan();

    plan.FormatForPrompt().Should().BeEmpty();
  }

  [Fact]
  public void MarkInProgress_NonExistentIndex_ShouldNotThrow()
  {
    var plan = new TaskPlan();
    plan.AddItem("Step 1");

    var action = () => plan.MarkInProgress(999);

    action.Should().NotThrow();
    plan.Items[0].Status.Should().Be("pending");
  }

  [Fact]
  public void MarkCompleted_NonExistentIndex_ShouldNotThrow()
  {
    var plan = new TaskPlan();
    plan.AddItem("Step 1");

    var action = () => plan.MarkCompleted(999);

    action.Should().NotThrow();
    plan.Items[0].Status.Should().Be("pending");
  }

  [Fact]
  public void MarkSkipped_NonExistentIndex_ShouldNotThrow()
  {
    var plan = new TaskPlan();
    plan.AddItem("Step 1");

    var action = () => plan.MarkSkipped(999, "reason");

    action.Should().NotThrow();
    plan.Items[0].Status.Should().Be("pending");
  }

  [Fact]
  public void GetCurrentItem_ReturnsInProgressBeforePending()
  {
    var plan = new TaskPlan();
    plan.AddItem("A");
    plan.AddItem("B");
    plan.MarkInProgress(1);

    var current = plan.GetCurrentItem();

    current!.Description.Should().Be("A");
    current.Status.Should().Be("in_progress");
  }

  [Fact]
  public void IsComplete_EmptyPlan_ReturnsTrue()
  {
    var plan = new TaskPlan();

    plan.IsComplete().Should().BeTrue();
  }

  [Fact]
  public void GetProgress_EmptyPlan_ReturnsZeroZero()
  {
    var plan = new TaskPlan();

    var (completed, total) = plan.GetProgress();

    completed.Should().Be(0);
    total.Should().Be(0);
  }

  [Fact]
  public void AddItem_MultipleItems_IncrementsIndex()
  {
    var plan = new TaskPlan();
    plan.AddItem("Step 1");
    plan.AddItem("Step 2");
    plan.AddItem("Step 3");

    plan.Items[0].Index.Should().Be(1);
    plan.Items[1].Index.Should().Be(2);
    plan.Items[2].Index.Should().Be(3);
  }

  [Fact]
  public void OriginalTask_CanBeSetAndRead()
  {
    var plan = new TaskPlan();
    plan.OriginalTask = "Deploy new version";

    plan.OriginalTask.Should().Be("Deploy new version");
  }

  [Fact]
  public void CreatedAt_CanBeSetAndRead()
  {
    var plan = new TaskPlan();
    var timestamp = "2026-03-25T10:00:00Z";
    plan.CreatedAt = timestamp;

    plan.CreatedAt.Should().Be(timestamp);
  }

  [Fact]
  public void FormatForPrompt_InProgressItem_ShowsNowMarker()
  {
    var plan = new TaskPlan();
    plan.AddItem("Step A");
    plan.MarkInProgress(1);

    var prompt = plan.FormatForPrompt();

    prompt.Should().Contain("NOW");
  }

  [Fact]
  public void FormatForPrompt_SkippedItem_ShowsSkipMarker()
  {
    var plan = new TaskPlan();
    plan.AddItem("Step A");
    plan.MarkSkipped(1);

    var prompt = plan.FormatForPrompt();

    prompt.Should().Contain("SKIP");
  }
}
