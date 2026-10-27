using AJ.Orchestrator.Abstractions.Models.Skills;

namespace AJ.Orchestrator.Abstractions.Services;

/// <summary>
/// Service for discovering and loading skill documents.
/// Skills teach AJ how to perform specific tasks by providing
/// instructions that get injected into the LLM reasoning context.
/// </summary>
public interface ISkillDiscoveryService
{
  /// <summary>
  /// Load all skills from configured skill paths.
  /// Should be called at startup.
  /// </summary>
  Task LoadSkillsAsync(CancellationToken ct = default);

  /// <summary>
  /// Reload skills from disk (for hot-reload scenarios).
  /// </summary>
  Task ReloadSkillsAsync(CancellationToken ct = default);

  /// <summary>
  /// Get all loaded skills.
  /// </summary>
  IReadOnlyCollection<Skill> GetAllSkills();

  /// <summary>
  /// Get a skill by name.
  /// </summary>
  Skill? GetSkill(string name);

  /// <summary>
  /// Find skills relevant to a user's intent.
  /// Uses keyword matching against tags, name, and description.
  /// </summary>
  /// <param name="userIntent">The user's message or intent.</param>
  /// <param name="tags">Optional explicit tags to filter by.</param>
  /// <returns>Matching skills ordered by relevance.</returns>
  IEnumerable<Skill> FindRelevantSkills(string userIntent, string[]? tags = null);

  /// <summary>
  /// Format skills as context for injection into LLM prompt.
  /// </summary>
  /// <param name="skills">Skills to format.</param>
  /// <returns>Formatted markdown string for LLM context.</returns>
  string FormatSkillContext(IEnumerable<Skill> skills);
}
