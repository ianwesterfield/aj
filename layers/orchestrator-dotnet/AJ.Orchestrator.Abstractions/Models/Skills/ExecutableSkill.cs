namespace AJ.Orchestrator.Abstractions.Models.Skills;

/// <summary>
/// Represents a YAML-defined skill that can be executed deterministically
/// without LLM involvement. This is the new V2 skill schema.
/// </summary>
public record ExecutableSkill
{
  public required string Name { get; init; }
  public int Version { get; init; } = 1;

  /// <summary>
  /// Human-readable description of what this skill does.
  /// Used by LLM to decide when to use this skill.
  /// </summary>
  public string? Description { get; init; }

  public required SkillTriggers Triggers { get; init; }
  public required Dictionary<string, SkillParameter> Parameters { get; init; }
  public required SkillTarget Target { get; init; }
  public required SkillWorkflow Workflow { get; init; }
  public required SkillResponses Responses { get; init; }

  /// <summary>
  /// File path this skill was loaded from (for debugging)
  /// </summary>
  public string? SourcePath { get; init; }
}

public record SkillTriggers
{
  /// <summary>
  /// Regex patterns to match against user input
  /// </summary>
  public List<string> Patterns { get; init; } = [];

  /// <summary>
  /// Keywords that suggest this skill (used for scoring)
  /// </summary>
  public List<string> Keywords { get; init; } = [];
}

public record SkillParameter
{
  /// <summary>
  /// Regex pattern to extract this parameter from user input.
  /// Should contain one capture group for the value.
  /// </summary>
  public required string Pattern { get; init; }

  /// <summary>
  /// Whether this parameter must be extracted for the skill to execute
  /// </summary>
  public bool Required { get; init; } = true;

  /// <summary>
  /// Human-readable description of what this parameter represents
  /// </summary>
  public string? Description { get; init; }

  /// <summary>
  /// Default value if not extracted and not required
  /// </summary>
  public string? Default { get; init; }
}

public record SkillTarget
{
  /// <summary>
  /// The agent ID to execute commands on
  /// </summary>
  public required string Agent { get; init; }

  /// <summary>
  /// Human-readable description of the target system
  /// </summary>
  public string? Description { get; init; }
}

public record SkillWorkflow
{
  public required List<WorkflowStep> Steps { get; init; }
}

public record WorkflowStep
{
  /// <summary>
  /// Human-readable name for this step
  /// </summary>
  public required string Name { get; init; }

  /// <summary>
  /// Default command template (platform-agnostic or fallback).
  /// Use {{paramName}} for parameter substitution.
  /// </summary>
  public string? Command { get; init; }

  /// <summary>
  /// Per-platform command templates (keys: "linux", "windows", "macos", etc.).
  /// When present, executor selects based on the resolved agent's platform.
  /// Falls back to <see cref="Command"/> when no match.
  /// </summary>
  public Dictionary<string, string>? Commands { get; init; }

  /// <summary>
  /// If true, continue workflow even if this step fails
  /// </summary>
  public bool ContinueOnError { get; init; } = false;

  /// <summary>
  /// Timeout in seconds for this command
  /// </summary>
  public int TimeoutSeconds { get; init; } = 30;
}

public record SkillResponses
{
  /// <summary>
  /// Response when all steps succeed. Use {{paramName}} for substitution.
  /// </summary>
  public required string Success { get; init; }

  /// <summary>
  /// Response when some steps fail. Use {{error}} for error message.
  /// </summary>
  public string? Partial { get; init; }

  /// <summary>
  /// Response when skill fails entirely. Use {{error}} for error message.
  /// </summary>
  public required string Failure { get; init; }
}
