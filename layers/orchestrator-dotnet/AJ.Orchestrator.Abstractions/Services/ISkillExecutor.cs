using AJ.Orchestrator.Abstractions.Models.Skills;

namespace AJ.Orchestrator.Abstractions.Services;

/// <summary>
/// Service for deterministically executing YAML-defined skills.
/// This bypasses the LLM for known task patterns.
/// </summary>
public interface ISkillExecutor
{
  /// <summary>
  /// Try to match user input against registered executable skills.
  /// Returns the best match if confidence is high enough, null otherwise.
  /// </summary>
  SkillMatch? TryMatch(string userInput);

  /// <summary>
  /// Execute a matched skill, running all workflow steps.
  /// </summary>
  Task<SkillExecutionResult> ExecuteAsync(SkillMatch match, CancellationToken ct = default);

  /// <summary>
  /// Get all loaded executable skills (for debugging/listing)
  /// </summary>
  IEnumerable<ExecutableSkill> GetAllSkills();

  /// <summary>
  /// Get a skill by its name (for LLM-initiated skill calls)
  /// </summary>
  ExecutableSkill? GetSkillByName(string name);
}

/// <summary>
/// Result of matching user input against skills
/// </summary>
public record SkillMatch
{
  public required ExecutableSkill Skill { get; init; }

  /// <summary>
  /// Parameters extracted from user input
  /// </summary>
  public required Dictionary<string, string> Parameters { get; init; }

  /// <summary>
  /// Confidence score (0-1) of this match
  /// </summary>
  public required double Confidence { get; init; }

  /// <summary>
  /// Which pattern or keywords triggered the match
  /// </summary>
  public string? MatchedBy { get; init; }
}

/// <summary>
/// Result of executing a skill
/// </summary>
public record SkillExecutionResult
{
  public required bool Success { get; init; }

  /// <summary>
  /// Human-readable response message
  /// </summary>
  public required string Message { get; init; }

  /// <summary>
  /// Results of each workflow step
  /// </summary>
  public required List<StepExecutionResult> StepResults { get; init; }

  /// <summary>
  /// Total execution time in milliseconds
  /// </summary>
  public long DurationMs { get; init; }
}

public record StepExecutionResult
{
  public required string StepName { get; init; }
  public required bool Success { get; init; }
  public string? Output { get; init; }
  public string? Error { get; init; }
  public long DurationMs { get; init; }
}
