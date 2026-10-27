namespace AJ.Orchestrator.Domain.Services;

/// <summary>
/// A single item in the task plan.
/// </summary>
public class TaskPlanItem
{
  public required int Index { get; init; }  // 1-based index
  public required string Description { get; set; }  // What this step will do
  public string Status { get; set; } = "pending";  // pending, in_progress, completed, skipped
  public string? ToolHint { get; init; }  // Expected tool (optional)
}

/// <summary>
/// Structured plan generated at task start.
/// 
/// Provides:
/// - Clear visibility to user of what will happen
/// - Script for LLM to follow
/// - Progress tracking
/// </summary>
public class TaskPlan
{
  public List<TaskPlanItem> Items { get; private set; } = [];
  public string OriginalTask { get; set; } = "";
  public string? CreatedAt { get; set; }

  /// <summary>Add a plan item.</summary>
  public void AddItem(string description, string? toolHint = null)
  {
    Items.Add(new TaskPlanItem
    {
      Index = Items.Count + 1,
      Description = description,
      ToolHint = toolHint
    });
  }

  /// <summary>Mark an item as in progress.</summary>
  public void MarkInProgress(int index)
  {
    var item = Items.FirstOrDefault(i => i.Index == index);
    if (item == null) return;

    item.Status = "in_progress";
  }

  /// <summary>Mark an item as completed.</summary>
  public void MarkCompleted(int index)
  {
    var item = Items.FirstOrDefault(i => i.Index == index);
    if (item == null) return;

    item.Status = "completed";
  }

  /// <summary>Mark an item as skipped.</summary>
  public void MarkSkipped(int index, string reason = "")
  {
    var item = Items.FirstOrDefault(i => i.Index == index);
    if (item == null) return;

    item.Status = "skipped";
    if (!string.IsNullOrEmpty(reason))
    {
      item.Description += $" (skipped: {reason})";
    }
  }

  /// <summary>Get the next pending or in-progress item.</summary>
  public TaskPlanItem? GetCurrentItem()
  {
    return Items.FirstOrDefault(i => i.Status is "pending" or "in_progress");
  }

  /// <summary>Return (completed_count, total_count).</summary>
  public (int completed, int total) GetProgress()
  {
    var completed = Items.Count(i => i.Status is "completed" or "skipped");
    return (completed, Items.Count);
  }

  /// <summary>Check if all items are done.</summary>
  public bool IsComplete()
  {
    return Items.All(i => i.Status is "completed" or "skipped");
  }

  /// <summary>Format plan for user display (markdown).</summary>
  public string FormatForDisplay()
  {
    if (Items.Count == 0)
    {
      return "";
    }

    var lines = new List<string> { "📋 **Task Plan:**" };
    foreach (var item in Items)
    {
      var icon = item.Status switch
      {
        "completed" => "✅",
        "in_progress" => "⏳",
        "skipped" => "⏭️",
        _ => "•"
      };
      lines.Add($"{icon} {item.Description}");
    }

    return string.Join("\n", lines);
  }

  /// <summary>Format plan for LLM context injection.</summary>
  public string FormatForPrompt()
  {
    if (Items.Count == 0)
    {
      return "";
    }

    var lines = new List<string> { "📋 TASK PLAN (follow this script):" };
    foreach (var item in Items)
    {
      var statusMarker = item.Status switch
      {
        "completed" => "✓ DONE",
        "in_progress" => "→ NOW",
        "skipped" => "⏭ SKIP",
        _ => "☐ TODO"
      };
      lines.Add($"  {item.Index}. [{statusMarker}] {item.Description}");
    }

    var current = GetCurrentItem();
    if (current != null)
    {
      lines.Add("");
      lines.Add($"⚡ CURRENT TASK: Step {current.Index} - {current.Description}");
      lines.Add("   Complete this step, then move to the next.");
    }
    else
    {
      lines.Add("");
      lines.Add("✅ ALL STEPS COMPLETE - call 'complete' with your answer");
    }

    return string.Join("\n", lines);
  }
}
