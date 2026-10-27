using System.Security.Cryptography;
using System.Text;

namespace AJ.Orchestrator.Domain.Services;

/// <summary>
/// Entry in the command flow context, tracking a single execution step.
/// Token-efficient structure for LLM context window.
/// </summary>
public record CommandFlowEntry
{
  public int StepIndex { get; init; }
  public string Tool { get; init; } = "";
  public bool Success { get; init; }
  public string OutputPreview { get; init; } = "";
  public string OutputHash { get; init; } = "";
  public string? AgentId { get; init; }
  public string? Command { get; init; }
  public string? Error { get; init; }
  public long? DurationMs { get; init; }
  public List<string> Tags { get; init; } = new();
  public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Context for tracking command execution flow across a task.
/// Provides queryable, token-efficient access to execution history.
/// Ported from Python: services/session_state.py::CommandFlowContext
/// </summary>
public class CommandFlowContext
{
  private readonly List<CommandFlowEntry> _entries = new();
  private readonly object _lock = new();

  private const int MaxPreviewLength = 10000;

  public string? OriginalGoal { get; set; }

  public IReadOnlyList<CommandFlowEntry> Entries
  {
    get
    {
      lock (_lock)
      {
        return _entries.ToList();
      }
    }
  }

  public CommandFlowEntry AddEntry(
      int stepIndex,
      string tool,
      bool success,
      string output,
      string? agentId = null,
      string? command = null,
      string? error = null,
      long? durationMs = null)
  {
    var preview = output.Length > MaxPreviewLength
        ? output[..MaxPreviewLength] + "..."
        : output;

    var hash = string.IsNullOrEmpty(output)
        ? ""
        : ComputeHash(output);

    var tags = new List<string> { tool };
    if (!string.IsNullOrEmpty(agentId))
      tags.Add($"agent:{agentId}");
    if (!success)
      tags.Add("failed");

    var entry = new CommandFlowEntry
    {
      StepIndex = stepIndex,
      Tool = tool,
      Success = success,
      OutputPreview = preview,
      OutputHash = hash,
      AgentId = agentId,
      Command = command,
      Error = error,
      DurationMs = durationMs,
      Tags = tags
    };

    lock (_lock)
    {
      _entries.Add(entry);
    }

    return entry;
  }

  public List<CommandFlowEntry> QueryByAgent(string agentId)
  {
    lock (_lock)
    {
      return _entries.Where(e => e.AgentId == agentId).ToList();
    }
  }

  public List<CommandFlowEntry> QueryByTag(string tag)
  {
    lock (_lock)
    {
      return _entries.Where(e => e.Tags.Contains(tag)).ToList();
    }
  }

  public List<CommandFlowEntry> QueryFailures()
  {
    lock (_lock)
    {
      return _entries.Where(e => !e.Success).ToList();
    }
  }

  public List<CommandFlowEntry> QueryRecent(int count = 5)
  {
    lock (_lock)
    {
      return _entries.TakeLast(count).ToList();
    }
  }

  public List<string> GetAgentsQueried()
  {
    lock (_lock)
    {
      return _entries
          .Where(e => !string.IsNullOrEmpty(e.AgentId))
          .Select(e => e.AgentId!)
          .Distinct()
          .ToList();
    }
  }

  public bool HasExecutedOn(string agentId)
  {
    lock (_lock)
    {
      return _entries.Any(e =>
          e.AgentId == agentId &&
          e.Tool == "execute");
    }
  }

  public string SummarizeForReplan()
  {
    lock (_lock)
    {
      if (_entries.Count == 0)
        return "No commands executed yet.";

      var okCount = _entries.Count(e => e.Success);
      var failCount = _entries.Count(e => !e.Success);

      var sb = new StringBuilder();

      if (!string.IsNullOrEmpty(OriginalGoal))
        sb.AppendLine($"Goal: {OriginalGoal}");

      sb.AppendLine($"Commands: {okCount} ok, {failCount} failed");

      var failures = QueryFailures();
      foreach (var f in failures.Take(3))
      {
        sb.AppendLine($"  - {f.Command}: {f.Error}");
      }

      return sb.ToString();
    }
  }

  public string FormatStepSummary(CommandFlowEntry entry)
  {
    var marker = entry.Success ? "✓" : "✗";
    var sb = new StringBuilder();

    if (entry.Tool == "execute" && !string.IsNullOrEmpty(entry.AgentId))
    {
      sb.Append($"{marker} [{entry.AgentId}] {entry.Command ?? entry.Tool}");
    }
    else
    {
      sb.Append($"{marker} {entry.Tool}");
    }

    return sb.ToString();
  }

  private static string ComputeHash(string input)
  {
    using var sha = SHA256.Create();
    var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
    return Convert.ToHexString(bytes)[..16]; // First 16 chars
  }
}
