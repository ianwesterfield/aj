using System.Collections.Concurrent;
using AJ.Orchestrator.Abstractions.Models.Tasks;
using AJ.Orchestrator.Abstractions.Models.Workspace;

namespace AJ.Orchestrator.Domain.Services;

/// <summary>
/// Manages session state for ongoing task executions.
/// Thread-safe per-user session tracking.
/// </summary>
public class SessionStateManager
{
  private readonly ConcurrentDictionary<string, SessionState> _sessions = new();

  public SessionState GetOrCreate(string userId)
  {
    return _sessions.GetOrAdd(userId, _ => new SessionState());
  }

  public void Reset(string userId)
  {
    if (_sessions.TryGetValue(userId, out var session))
    {
      session.Reset();
    }
  }

  public void Remove(string userId)
  {
    _sessions.TryRemove(userId, out _);
  }
}

/// <summary>
/// State for a single user session.
/// </summary>
public class SessionState
{
  private readonly List<StepResult> _history = new();
  private readonly object _lock = new();

  public string? CurrentTask { get; set; }
  public WorkspaceContext? Workspace { get; set; }
  public DateTime StartedAt { get; private set; } = DateTime.UtcNow;

  public IReadOnlyList<StepResult> History
  {
    get
    {
      lock (_lock)
      {
        return _history.ToList();
      }
    }
  }

  public void AddResult(StepResult result)
  {
    lock (_lock)
    {
      _history.Add(result);
    }
  }

  public void Reset()
  {
    lock (_lock)
    {
      _history.Clear();
      CurrentTask = null;
      StartedAt = DateTime.UtcNow;
    }
  }

  public int StepCount
  {
    get
    {
      lock (_lock)
      {
        return _history.Count;
      }
    }
  }
}
