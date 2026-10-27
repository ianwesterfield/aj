namespace AJ.Orchestrator.Domain.Services;

/// <summary>
/// A single entry in the conversation ledger.
/// </summary>
public class LedgerEntry
{
  public required string Timestamp { get; init; }
  public required string EntryType { get; init; }  // "request", "action", "result", "extracted"
  public required string Summary { get; init; }
  public Dictionary<string, string>? Details { get; init; }
}

/// <summary>
/// Running ledger of the entire conversation/session.
/// 
/// Tracks user requests, actions taken, important extracted values,
/// and provides a quick-reference summary for the LLM.
/// </summary>
public class ConversationLedger
{
  /// <summary>User requests made this session</summary>
  public List<string> UserRequests { get; private set; } = [];

  /// <summary>Important values extracted from outputs (IPs, URLs, paths, credentials, etc.)</summary>
  public Dictionary<string, string> ExtractedValues { get; private set; } = [];

  /// <summary>Chronological log of all entries</summary>
  public List<LedgerEntry> Entries { get; private set; } = [];

  /// <summary>Running summary (regenerated periodically)</summary>
  public string SessionSummary { get; set; } = "";

  /// <summary>Log a user request.</summary>
  public void AddRequest(string request)
  {
    UserRequests.Add(request);
    Entries.Add(new LedgerEntry
    {
      Timestamp = DateTime.UtcNow.ToString("HH:mm:ss"),
      EntryType = "request",
      Summary = request.Length > 100 ? request[..100] + "..." : request
    });

    // Keep only last 20 requests
    if (UserRequests.Count > 20)
    {
      UserRequests = UserRequests.TakeLast(20).ToList();
    }
  }

  /// <summary>Log an action taken.</summary>
  public void AddAction(string tool, string paramsSummary, string resultSummary)
  {
    Entries.Add(new LedgerEntry
    {
      Timestamp = DateTime.UtcNow.ToString("HH:mm:ss"),
      EntryType = "action",
      Summary = $"{tool}: {paramsSummary}",
      Details = new Dictionary<string, string> { ["result"] = resultSummary }
    });

    // Keep only last 50 entries
    if (Entries.Count > 50)
    {
      Entries = Entries.TakeLast(50).ToList();
    }
  }

  /// <summary>Store an important extracted value for quick reference.</summary>
  public void ExtractValue(string key, string value, string source = "")
  {
    ExtractedValues[key] = value;
    var summary = $"Found {key}: {value}";
    if (!string.IsNullOrEmpty(source))
    {
      summary += $" (from {source})";
    }

    Entries.Add(new LedgerEntry
    {
      Timestamp = DateTime.UtcNow.ToString("HH:mm:ss"),
      EntryType = "extracted",
      Summary = summary
    });
  }

  /// <summary>Format ledger as context for LLM prompt.</summary>
  public string FormatForPrompt()
  {
    var lines = new List<string>();

    // Show extracted values first - most useful for quick reference
    if (ExtractedValues.Count > 0)
    {
      lines.Add("📋 QUICK REFERENCE (extracted from session):");
      foreach (var kvp in ExtractedValues.TakeLast(15))
      {
        lines.Add($"  • {kvp.Key}: {kvp.Value}");
      }
      lines.Add("");
    }

    // Show recent user requests
    if (UserRequests.Count > 0)
    {
      lines.Add("💬 USER REQUESTS THIS SESSION:");
      var index = 1;
      foreach (var req in UserRequests.TakeLast(5))
      {
        var truncated = req.Length > 80 ? req[..80] + "..." : req;
        lines.Add($"  {index}. {truncated}");
        index++;
      }
      lines.Add("");
    }

    // Show recent actions timeline
    var recentActions = Entries
        .Where(e => e.EntryType == "action")
        .TakeLast(10)
        .ToList();

    if (recentActions.Count > 0)
    {
      lines.Add("⏱️ RECENT ACTIONS:");
      foreach (var entry in recentActions)
      {
        var result = entry.Details?.GetValueOrDefault("result", "") ?? "";
        var resultShort = result.Length > 50 ? result[..50] + "..." : result;
        lines.Add($"  [{entry.Timestamp}] {entry.Summary}");
        if (!string.IsNullOrEmpty(resultShort))
        {
          lines.Add($"           → {resultShort}");
        }
      }
      lines.Add("");
    }

    return lines.Count > 0 ? string.Join("\n", lines) : "";
  }
}
