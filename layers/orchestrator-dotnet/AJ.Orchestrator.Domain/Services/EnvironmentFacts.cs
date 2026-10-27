namespace AJ.Orchestrator.Domain.Services;

/// <summary>
/// Facts learned about the environment from command outputs.
/// Extracted by analyzing tool results to understand the workspace.
/// </summary>
public class EnvironmentFacts
{
  // Workspace metrics
  public int? TotalFileCount { get; set; }
  public int? TotalDirCount { get; set; }
  public long? TotalSizeBytes { get; set; }
  public string? TotalSizeHuman { get; set; }

  // Project type detection
  public HashSet<string> ProjectTypes { get; set; } = [];  // e.g., {"python", "docker"}
  public HashSet<string> FrameworksDetected { get; set; } = [];  // e.g., {"fastapi", "pytest"}
  public HashSet<string> PackageManagers { get; set; } = [];  // e.g., {"pip", "npm"}

  // Environment observations
  public List<string> Observations { get; private set; } = [];  // Free-form learnings

  // System info (from shell commands)
  public string? WorkingDirectory { get; set; }
  public string? PythonVersion { get; set; }
  public string? NodeVersion { get; set; }
  public string? GitBranch { get; set; }
  public bool? DockerRunning { get; set; }

  /// <summary>Add a unique observation.</summary>
  public void AddObservation(string fact)
  {
    if (!string.IsNullOrEmpty(fact) && !Observations.Contains(fact))
    {
      Observations.Add(fact);
      // Keep only the last 20 observations
      if (Observations.Count > 20)
      {
        Observations = Observations.TakeLast(20).ToList();
      }
    }
  }
}
