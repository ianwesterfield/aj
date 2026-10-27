namespace AJ.Orchestrator.Abstractions.Models.Skills;

/// <summary>
/// Represents a skill document that teaches AJ how to perform specific tasks.
/// Skills are markdown documents with optional YAML frontmatter metadata.
/// </summary>
public record Skill
{
  /// <summary>
  /// Unique identifier for this skill (derived from filename or frontmatter).
  /// </summary>
  public required string Name { get; init; }

  /// <summary>
  /// Brief description of what this skill enables.
  /// </summary>
  public required string Description { get; init; }

  /// <summary>
  /// Full markdown content of the skill instructions.
  /// This is injected into the LLM context when the skill is relevant.
  /// </summary>
  public required string Instructions { get; init; }

  /// <summary>
  /// Tags for skill discovery (e.g., ["mail", "spam", "postfix"]).
  /// </summary>
  public string[] Tags { get; init; } = [];

  /// <summary>
  /// Target agent ID if this skill is specific to a host (e.g., "postfix01").
  /// Null means the skill can be used on any appropriate agent.
  /// </summary>
  public string? TargetAgent { get; init; }

  /// <summary>
  /// Source file path where this skill was loaded from.
  /// </summary>
  public string? SourcePath { get; init; }
}

/// <summary>
/// YAML frontmatter metadata parsed from skill files.
/// </summary>
public record SkillMetadata
{
  public string? Name { get; init; }
  public string? Description { get; init; }
  public string[]? Tags { get; init; }
  public string? TargetAgent { get; init; }
}
