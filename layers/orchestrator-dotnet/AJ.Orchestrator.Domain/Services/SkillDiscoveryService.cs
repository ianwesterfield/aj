using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using AJ.Orchestrator.Abstractions.Models.Skills;
using AJ.Orchestrator.Abstractions.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AJ.Orchestrator.Domain.Services;

/// <summary>
/// Discovers and loads skill documents from configured paths.
/// 
/// Skills are SKILL.md files that contain instructions for performing
/// specific tasks. They can have YAML frontmatter for metadata.
/// 
/// Skill paths are configured in appsettings.json under Skills:Paths
/// or default to ./skills and ~/.aj/skills
/// 
/// File watching is enabled by default - skills auto-reload on changes.
/// </summary>
public partial class SkillDiscoveryService : ISkillDiscoveryService, IDisposable
{
  private readonly ILogger<SkillDiscoveryService> _logger;
  private readonly List<string> _skillPaths;
  private readonly ConcurrentDictionary<string, Skill> _skills = new();
  private readonly List<FileSystemWatcher> _watchers = new();
  private readonly object _reloadLock = new();
  private CancellationTokenSource? _debounceCts;
  private bool _disposed;

  // Regex to extract YAML frontmatter (content between --- markers)
  [GeneratedRegex(@"^---\s*\n(.*?)\n---\s*\n", RegexOptions.Singleline)]
  private static partial Regex FrontmatterRegex();

  // Keywords that help identify skill relevance
  private static readonly char[] _wordSeparators = [' ', ',', '-', '_', '.', '/', '\\', '"', '\'', '(', ')', '[', ']', '{', '}', ':', ';', '!', '?', '\n', '\r', '\t'];

  public SkillDiscoveryService(
      ILogger<SkillDiscoveryService> logger,
      IConfiguration configuration)
  {
    _logger = logger;

    // Load skill paths from config or use defaults
    var configuredPaths = configuration.GetSection("Skills:Paths").Get<List<string>>();

    if (configuredPaths != null && configuredPaths.Count > 0)
    {
      _skillPaths = configuredPaths;
    }
    else
    {
      _skillPaths =
      [
        Path.Combine(AppContext.BaseDirectory, "skills"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aj", "skills"),
        // Also check workspace-relative skills folder
        "skills"
      ];
    }

    _logger.LogInformation("SkillDiscoveryService initialized with paths: {Paths}",
        string.Join(", ", _skillPaths));
  }

  public async Task LoadSkillsAsync(CancellationToken ct = default)
  {
    _skills.Clear();

    foreach (var basePath in _skillPaths)
    {
      var expandedPath = Environment.ExpandEnvironmentVariables(basePath);

      // Handle relative paths
      if (!Path.IsPathRooted(expandedPath))
      {
        expandedPath = Path.GetFullPath(expandedPath);
      }

      if (!Directory.Exists(expandedPath))
      {
        _logger.LogDebug("Skill path does not exist, skipping: {Path}", expandedPath);
        continue;
      }

      await LoadSkillsFromPathAsync(expandedPath, ct);
    }

    _logger.LogInformation("Loaded {Count} skill(s): {Names}",
        _skills.Count, string.Join(", ", _skills.Keys));

    // Set up file watchers for auto-reload
    SetupFileWatchers();
  }

  /// <summary>
  /// Set up FileSystemWatchers on each skill directory for auto-reload.
  /// </summary>
  private void SetupFileWatchers()
  {
    // Clean up existing watchers
    foreach (var watcher in _watchers)
    {
      watcher.EnableRaisingEvents = false;
      watcher.Dispose();
    }
    _watchers.Clear();

    foreach (var basePath in _skillPaths)
    {
      var expandedPath = Environment.ExpandEnvironmentVariables(basePath);
      if (!Path.IsPathRooted(expandedPath))
        expandedPath = Path.GetFullPath(expandedPath);

      if (!Directory.Exists(expandedPath))
        continue;

      try
      {
        var watcher = new FileSystemWatcher(expandedPath)
        {
          Filter = "*.md",
          IncludeSubdirectories = true,
          NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
        };

        watcher.Changed += OnSkillFileChanged;
        watcher.Created += OnSkillFileChanged;
        watcher.Deleted += OnSkillFileChanged;
        watcher.Renamed += OnSkillFileRenamed;

        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);

        _logger.LogInformation("Watching for skill changes: {Path}", expandedPath);
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Failed to set up file watcher for: {Path}", expandedPath);
      }
    }
  }

  /// <summary>
  /// Handle file change events with debouncing to avoid rapid reloads.
  /// </summary>
  private void OnSkillFileChanged(object sender, FileSystemEventArgs e)
  {
    // Only care about SKILL.md files
    if (!e.Name?.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase) ?? true)
      return;

    _logger.LogInformation("Skill file changed: {Path} ({ChangeType})", e.FullPath, e.ChangeType);
    ScheduleDebouncedReload();
  }

  private void OnSkillFileRenamed(object sender, RenamedEventArgs e)
  {
    var isOldSkill = e.OldName?.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase) ?? false;
    var isNewSkill = e.Name?.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase) ?? false;

    if (!isOldSkill && !isNewSkill)
      return;

    _logger.LogInformation("Skill file renamed: {OldPath} -> {NewPath}", e.OldFullPath, e.FullPath);
    ScheduleDebouncedReload();
  }

  /// <summary>
  /// Schedule a debounced reload - waits 500ms after last change before reloading.
  /// This prevents rapid reloads when editors save multiple times or do atomic writes.
  /// </summary>
  private void ScheduleDebouncedReload()
  {
    lock (_reloadLock)
    {
      // Cancel any pending reload
      _debounceCts?.Cancel();
      _debounceCts?.Dispose();
      _debounceCts = new CancellationTokenSource();

      var token = _debounceCts.Token;

      // Schedule reload after 500ms debounce
      _ = Task.Run(async () =>
      {
        try
        {
          await Task.Delay(500, token);

          if (!token.IsCancellationRequested)
          {
            _logger.LogInformation("Auto-reloading skills due to file changes...");
            await LoadSkillsInternalAsync(CancellationToken.None);
            _logger.LogInformation("Skills auto-reloaded: {Count} skill(s)", _skills.Count);
          }
        }
        catch (OperationCanceledException)
        {
          // Debounce cancelled by newer change - ignore
        }
        catch (Exception ex)
        {
          _logger.LogError(ex, "Failed to auto-reload skills");
        }
      }, token);
    }
  }

  /// <summary>
  /// Internal reload without setting up watchers (to avoid recursion).
  /// </summary>
  private async Task LoadSkillsInternalAsync(CancellationToken ct)
  {
    _skills.Clear();

    foreach (var basePath in _skillPaths)
    {
      var expandedPath = Environment.ExpandEnvironmentVariables(basePath);
      if (!Path.IsPathRooted(expandedPath))
        expandedPath = Path.GetFullPath(expandedPath);

      if (!Directory.Exists(expandedPath))
        continue;

      await LoadSkillsFromPathAsync(expandedPath, ct);
    }
  }

  public Task ReloadSkillsAsync(CancellationToken ct = default) => LoadSkillsAsync(ct);

  public IReadOnlyCollection<Skill> GetAllSkills() => _skills.Values.ToList().AsReadOnly();

  public Skill? GetSkill(string name) =>
      _skills.TryGetValue(name, out var skill) ? skill : null;

  public IEnumerable<Skill> FindRelevantSkills(string userIntent, string[]? tags = null)
  {
    var keywords = ExtractKeywords(userIntent);
    var explicitTags = tags ?? [];

    var scored = _skills.Values
        .Select(skill => new { Skill = skill, Score = ScoreSkill(skill, keywords, explicitTags) })
        .ToList();

    // Require minimum score of 5 to avoid matching on single weak keywords
    // 5 = name match, or 2+ tag matches, or explicit tag
    var matched = scored
        .Where(x => x.Score >= 5)
        .OrderByDescending(x => x.Score)
        .Select(x => x.Skill)
        .ToList();

    return matched;
  }

  public string FormatSkillContext(IEnumerable<Skill> skills)
  {
    var skillList = skills.ToList();

    if (skillList.Count == 0)
      return "";

    var sb = new System.Text.StringBuilder();
    sb.AppendLine("## Available Skills");
    sb.AppendLine();
    sb.AppendLine("REMINDER: Skills below are WORKFLOW GUIDES. They may vary in format (detailed or minimal).");
    sb.AppendLine("- Use skills to identify the TARGET AGENT and COMMAND PATTERNS");
    sb.AppendLine("- ALL example values (domains, users, paths) are PLACEHOLDERS");
    sb.AppendLine("- Extract ACTUAL values from the USER'S REQUEST only");
    sb.AppendLine();

    foreach (var skill in skillList)
    {
      sb.AppendLine($"### Skill: {skill.Name}");
      if (!string.IsNullOrEmpty(skill.Description))
      {
        sb.AppendLine($"*{skill.Description}*");
      }
      if (skill.TargetAgent != null)
      {
        sb.AppendLine($"> Target agent: `{skill.TargetAgent}`");
      }
      sb.AppendLine();
      sb.AppendLine(skill.Instructions);
      sb.AppendLine();
      sb.AppendLine("---");
      sb.AppendLine();
    }

    return sb.ToString();
  }

  private async Task LoadSkillsFromPathAsync(string basePath, CancellationToken ct)
  {
    var skillFiles = Directory.GetFiles(basePath, "SKILL.md", SearchOption.AllDirectories);

    foreach (var file in skillFiles)
    {
      if (ct.IsCancellationRequested) break;

      try
      {
        var skill = await ParseSkillFileAsync(file, ct);
        _skills[skill.Name] = skill;
        _logger.LogDebug("Loaded skill: {Name} from {Path}", skill.Name, file);
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Failed to parse skill file: {Path}", file);
      }
    }
  }

  private async Task<Skill> ParseSkillFileAsync(string filePath, CancellationToken ct)
  {
    var content = await File.ReadAllTextAsync(filePath, ct);
    var directory = Path.GetDirectoryName(filePath) ?? "";
    var directoryName = Path.GetFileName(directory);

    // Try to extract YAML frontmatter
    var metadata = ExtractFrontmatter(content, out var instructions);

    // Use frontmatter name, or directory name, or filename
    var name = metadata?.Name
        ?? directoryName
        ?? Path.GetFileNameWithoutExtension(filePath);

    // Use frontmatter description or extract from first paragraph
    var description = metadata?.Description
        ?? ExtractFirstParagraph(instructions);

    return new Skill
    {
      Name = name,
      Description = description,
      Instructions = instructions.Trim(),
      Tags = metadata?.Tags ?? [],
      TargetAgent = metadata?.TargetAgent,
      SourcePath = filePath
    };
  }

  private static SkillMetadata? ExtractFrontmatter(string content, out string body)
  {
    var match = FrontmatterRegex().Match(content);

    if (!match.Success)
    {
      body = content;
      return null;
    }

    body = content[match.Length..];
    var yamlContent = match.Groups[1].Value;

    // Simple YAML parsing for our limited use case
    // Full YAML library would be better but this avoids extra dependencies
    return ParseSimpleYaml(yamlContent);
  }

  private static SkillMetadata ParseSimpleYaml(string yaml)
  {
    var lines = yaml.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    string? name = null;
    string? description = null;
    string? targetAgent = null;
    var tags = new List<string>();

    foreach (var line in lines)
    {
      var trimmed = line.Trim();
      if (trimmed.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
      {
        name = trimmed[5..].Trim().Trim('"', '\'');
      }
      else if (trimmed.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
      {
        description = trimmed[12..].Trim().Trim('"', '\'');
      }
      else if (trimmed.StartsWith("targetAgent:", StringComparison.OrdinalIgnoreCase))
      {
        targetAgent = trimmed[12..].Trim().Trim('"', '\'');
      }
      else if (trimmed.StartsWith("tags:", StringComparison.OrdinalIgnoreCase))
      {
        var tagsPart = trimmed[5..].Trim();
        // Handle inline array format: tags: [mail, spam, postfix]
        if (tagsPart.StartsWith('[') && tagsPart.EndsWith(']'))
        {
          var inner = tagsPart[1..^1];
          tags.AddRange(inner.Split(',').Select(t => t.Trim().Trim('"', '\'')));
        }
      }
      else if (trimmed.StartsWith("- ") && tags.Count == 0)
      {
        // Handle YAML list format for tags (simplified)
        tags.Add(trimmed[2..].Trim().Trim('"', '\''));
      }
    }

    return new SkillMetadata
    {
      Name = name,
      Description = description,
      Tags = tags.Count > 0 ? tags.ToArray() : null,
      TargetAgent = targetAgent
    };
  }

  private static string ExtractFirstParagraph(string content)
  {
    // Skip any leading headers and get first paragraph
    var lines = content.Split('\n');
    var paragraph = new System.Text.StringBuilder();

    var inParagraph = false;
    foreach (var line in lines)
    {
      var trimmed = line.Trim();

      // Skip headers
      if (trimmed.StartsWith('#'))
        continue;

      // Empty line ends paragraph
      if (string.IsNullOrWhiteSpace(trimmed))
      {
        if (inParagraph)
          break;
        continue;
      }

      inParagraph = true;
      paragraph.Append(trimmed);
      paragraph.Append(' ');
    }

    var result = paragraph.ToString().Trim();
    // Truncate if too long
    if (result.Length > 200)
      result = result[..197] + "...";

    return result;
  }

  private static HashSet<string> ExtractKeywords(string text)
  {
    var words = text.ToLowerInvariant()
        .Split(_wordSeparators, StringSplitOptions.RemoveEmptyEntries)
        .Where(w => w.Length > 2)  // Skip very short words
        .ToHashSet();

    return words;
  }

  private static int ScoreSkill(Skill skill, HashSet<string> keywords, string[] explicitTags)
  {
    var score = 0;

    // Explicit tag matches (highest priority)
    foreach (var tag in explicitTags)
    {
      if (skill.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        score += 10;
    }

    // Skill name match
    if (keywords.Any(k => skill.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))
      score += 5;

    // Tag matches from user keywords
    foreach (var tag in skill.Tags)
    {
      if (keywords.Contains(tag.ToLowerInvariant()))
        score += 3;
    }

    // Description matches
    var descLower = skill.Description.ToLowerInvariant();
    foreach (var keyword in keywords)
    {
      if (descLower.Contains(keyword))
        score += 1;
    }

    // Target agent mentioned
    if (skill.TargetAgent != null &&
        keywords.Contains(skill.TargetAgent.ToLowerInvariant()))
      score += 8;

    return score;
  }

  public void Dispose()
  {
    if (_disposed) return;
    _disposed = true;

    _debounceCts?.Cancel();
    _debounceCts?.Dispose();

    foreach (var watcher in _watchers)
    {
      watcher.EnableRaisingEvents = false;
      watcher.Dispose();
    }
    _watchers.Clear();

    GC.SuppressFinalize(this);
  }
}
