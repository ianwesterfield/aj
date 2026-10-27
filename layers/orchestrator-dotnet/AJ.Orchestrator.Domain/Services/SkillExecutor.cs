using System.Diagnostics;
using System.Text.RegularExpressions;
using AJ.Orchestrator.Abstractions.Models.Skills;
using AJ.Orchestrator.Abstractions.Services;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AJ.Orchestrator.Domain.Services;

/// <summary>
/// Executes YAML-defined skills deterministically, bypassing the LLM.
/// This is the core of the V2 architecture - code handles known patterns.
/// </summary>
public class SkillExecutor : ISkillExecutor
{
  private readonly ILogger<SkillExecutor> _logger;
  private readonly IGrpcAgentClient _agentClient;
  private readonly List<ExecutableSkill> _skills = [];
  private readonly IDeserializer _yamlDeserializer;

  // Minimum confidence required to auto-execute a skill
  private const double MinConfidence = 0.5;

  public SkillExecutor(
      ILogger<SkillExecutor> logger,
      IGrpcAgentClient agentClient)
  {
    _logger = logger;
    _agentClient = agentClient;

    _yamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();
  }

  /// <summary>
  /// Load all YAML skill files from the specified paths
  /// </summary>
  public async Task LoadSkillsAsync(IEnumerable<string> skillPaths, CancellationToken ct = default)
  {
    _skills.Clear();

    foreach (var basePath in skillPaths)
    {
      if (!Directory.Exists(basePath))
      {
        _logger.LogWarning("Skill path does not exist: {Path}", basePath);
        continue;
      }

      var yamlFiles = Directory.GetFiles(basePath, "skill.yaml", SearchOption.AllDirectories);
      _logger.LogInformation("Found {Count} YAML skill files in {Path}", yamlFiles.Length, basePath);

      foreach (var file in yamlFiles)
      {
        if (ct.IsCancellationRequested) break;

        try
        {
          var yaml = await File.ReadAllTextAsync(file, ct);
          var skill = ParseYamlSkill(yaml, file);
          if (skill != null)
          {
            _skills.Add(skill);
            _logger.LogInformation("Loaded executable skill: {Name} from {Path}", skill.Name, file);
          }
        }
        catch (Exception ex)
        {
          _logger.LogError(ex, "Failed to load skill from {File}", file);
        }
      }
    }

    _logger.LogInformation("Loaded {Count} executable skills total", _skills.Count);
  }

  private ExecutableSkill? ParseYamlSkill(string yaml, string sourcePath)
  {
    try
    {
      var raw = _yamlDeserializer.Deserialize<YamlSkillRaw>(yaml);
      if (raw == null || string.IsNullOrEmpty(raw.Name))
        return null;

      return new ExecutableSkill
      {
        Name = raw.Name,
        Version = raw.Version,
        Description = raw.Description,
        Triggers = new SkillTriggers
        {
          Patterns = raw.Triggers?.Patterns ?? [],
          Keywords = raw.Triggers?.Keywords ?? []
        },
        Parameters = raw.Parameters?.ToDictionary(
            kv => kv.Key,
            kv => new SkillParameter
            {
              Pattern = kv.Value.Pattern ?? "",
              Required = kv.Value.Required,
              Description = kv.Value.Description,
              Default = kv.Value.Default
            }) ?? [],
        Target = new SkillTarget
        {
          Agent = raw.Target?.Agent ?? "",
          Description = raw.Target?.Description
        },
        Workflow = new SkillWorkflow
        {
          Steps = raw.Workflow?.Steps?.Select(s => new WorkflowStep
          {
            Name = s.Name ?? "Step",
            Command = s.Command,
            Commands = s.Commands,
            ContinueOnError = s.ContinueOnError,
            TimeoutSeconds = s.TimeoutSeconds > 0 ? s.TimeoutSeconds : 30
          }).ToList() ?? []
        },
        Responses = new SkillResponses
        {
          Success = raw.Responses?.Success ?? "Done.",
          Partial = raw.Responses?.Partial,
          Failure = raw.Responses?.Failure ?? "Failed."
        },
        SourcePath = sourcePath
      };
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to parse YAML skill from {Path}", sourcePath);
      return null;
    }
  }

  /// <summary>
  /// Get a skill by its name (for LLM-initiated skill calls)
  /// </summary>
  public ExecutableSkill? GetSkillByName(string name)
  {
    return _skills.FirstOrDefault(s =>
      s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
      s.Name.Replace("-", "").Equals(name.Replace("-", ""), StringComparison.OrdinalIgnoreCase));
  }

  public SkillMatch? TryMatch(string userInput)
  {
    var input = userInput.ToLowerInvariant();
    SkillMatch? bestMatch = null;
    double bestScore = 0;

    foreach (var skill in _skills)
    {
      var (score, matchedBy) = ScoreSkill(skill, input);

      if (score > bestScore)
      {
        // Try to extract required parameters
        var parameters = ExtractParameters(skill, userInput);
        var missingRequired = skill.Parameters
            .Where(p => p.Value.Required && !parameters.ContainsKey(p.Key))
            .Select(p => p.Key)
            .ToList();

        if (missingRequired.Count > 0)
        {
          _logger.LogDebug("Skill {Name} matched but missing required parameters: {Missing}",
              skill.Name, string.Join(", ", missingRequired));
          continue;
        }

        bestScore = score;
        bestMatch = new SkillMatch
        {
          Skill = skill,
          Parameters = parameters,
          Confidence = score,
          MatchedBy = matchedBy
        };
      }
    }

    if (bestMatch != null && bestMatch.Confidence >= MinConfidence)
    {
      _logger.LogInformation("Matched skill {Name} with confidence {Confidence:P0} via {MatchedBy}",
          bestMatch.Skill.Name, bestMatch.Confidence, bestMatch.MatchedBy);
      return bestMatch;
    }

    return null;
  }

  private (double score, string? matchedBy) ScoreSkill(ExecutableSkill skill, string input)
  {
    // Check regex patterns - highest priority
    foreach (var pattern in skill.Triggers.Patterns)
    {
      try
      {
        if (Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase))
        {
          return (0.9, $"pattern: {pattern}");
        }
      }
      catch (RegexParseException ex)
      {
        _logger.LogWarning("Invalid regex pattern in skill {Name}: {Pattern} - {Error}",
            skill.Name, pattern, ex.Message);
      }
    }

    // Check keywords - lower priority, score based on match count
    var keywordMatches = skill.Triggers.Keywords.Count(kw =>
        input.Contains(kw, StringComparison.OrdinalIgnoreCase));

    if (keywordMatches > 0)
    {
      var ratio = (double)keywordMatches / skill.Triggers.Keywords.Count;
      // Need at least 2 keyword matches for decent confidence
      if (keywordMatches >= 2)
      {
        return (0.5 + (ratio * 0.3), $"keywords: {keywordMatches}/{skill.Triggers.Keywords.Count}");
      }
    }

    return (0, null);
  }

  private Dictionary<string, string> ExtractParameters(ExecutableSkill skill, string userInput)
  {
    var result = new Dictionary<string, string>();

    foreach (var (name, param) in skill.Parameters)
    {
      try
      {
        var match = Regex.Match(userInput, param.Pattern, RegexOptions.IgnoreCase);
        if (match.Success && match.Groups.Count > 1)
        {
          result[name] = match.Groups[1].Value;
          _logger.LogDebug("Extracted {Name}={Value} from input", name, result[name]);
        }
        else if (!param.Required && param.Default != null)
        {
          result[name] = param.Default;
        }
      }
      catch (RegexParseException ex)
      {
        _logger.LogWarning("Invalid parameter pattern for {Name}: {Pattern} - {Error}",
            name, param.Pattern, ex.Message);
      }
    }

    return result;
  }

  public async Task<SkillExecutionResult> ExecuteAsync(SkillMatch match, CancellationToken ct = default)
  {
    var sw = Stopwatch.StartNew();
    var stepResults = new List<StepExecutionResult>();
    var allSuccess = true;
    string? lastError = null;

    // Check if skill has no workflow but params include agent_id + command
    // This handles context-injection skills like funnelcloud-agents where the LLM
    // provides the execution params directly
    var hasWorkflow = match.Skill.Workflow?.Steps?.Count > 0;
    var paramAgentId = match.Parameters.GetValueOrDefault("agent_id");
    var paramCommand = match.Parameters.GetValueOrDefault("command");

    if (!hasWorkflow && !string.IsNullOrEmpty(paramAgentId) && !string.IsNullOrEmpty(paramCommand))
    {
      _logger.LogInformation("Skill {Name} has no workflow - executing directly on {Agent}: {Command}",
          match.Skill.Name, paramAgentId, paramCommand);

      var execResult = await _agentClient.ExecuteAsync(paramAgentId, paramCommand, 3600, ct);
      sw.Stop();

      return new SkillExecutionResult
      {
        Success = execResult.Success,
        Message = execResult.Success
            ? execResult.Stdout ?? "(no output)"
            : execResult.ErrorMessage ?? execResult.Stderr ?? "Command failed",
        StepResults =
        [
          new StepExecutionResult
          {
            StepName = "execute",
            Success = execResult.Success,
            Output = execResult.Stdout,
            Error = execResult.Stderr ?? execResult.ErrorMessage,
            DurationMs = execResult.DurationMs
          }
        ],
        DurationMs = sw.ElapsedMilliseconds
      };
    }

    _logger.LogInformation("Executing skill {Name} on agent {Agent} with params: {@Params}",
        match.Skill.Name, match.Skill.Target.Agent, match.Parameters);

    // First, verify agent is online
    var agents = await _agentClient.DiscoverAgentsAsync(ct);

    // Use target agent from skill, or fall back to agent_id param
    var targetAgentId = !string.IsNullOrEmpty(match.Skill.Target?.Agent)
        ? match.Skill.Target.Agent
        : paramAgentId ?? "";

    var targetAgent = agents.FirstOrDefault(a =>
        a.AgentId.Equals(targetAgentId, StringComparison.OrdinalIgnoreCase));

    if (targetAgent == null)
    {
      return new SkillExecutionResult
      {
        Success = false,
        Message = SubstituteParams(match.Skill.Responses.Failure, match.Parameters,
            $"Agent '{targetAgentId}' is not online"),
        StepResults = [],
        DurationMs = sw.ElapsedMilliseconds
      };
    }

    // Execute each workflow step
    var agentPlatform = NormalizePlatform(targetAgent.Platform);

    foreach (var step in match.Skill.Workflow?.Steps ?? [])
    {
      if (ct.IsCancellationRequested)
        break;

      var template = SelectCommandForPlatform(step, agentPlatform);
      if (string.IsNullOrWhiteSpace(template))
      {
        allSuccess = false;
        lastError = $"No command defined for platform '{agentPlatform}' in step '{step.Name}'";
        _logger.LogWarning("{Error}", lastError);
        stepResults.Add(new StepExecutionResult
        {
          StepName = step.Name,
          Success = false,
          Output = null,
          Error = lastError,
          DurationMs = 0
        });
        if (!step.ContinueOnError) break;
        continue;
      }

      var command = SubstituteParams(template, match.Parameters);
      _logger.LogInformation("Step '{Step}' ({Platform}): {Command}", step.Name, agentPlatform, command);

      var stepSw = Stopwatch.StartNew();
      var skillTargetAgent = match.Skill.Target?.Agent;

      if (skillTargetAgent != null)
      {
        var execResult = await _agentClient.ExecuteAsync(skillTargetAgent, command, step.TimeoutSeconds, ct);

        stepSw.Stop();

        var stepResult = new StepExecutionResult
        {
          StepName = step.Name,
          Success = execResult.Success,
          Output = execResult.Stdout,
          Error = execResult.Stderr ?? execResult.ErrorMessage,
          DurationMs = stepSw.ElapsedMilliseconds
        };

        stepResults.Add(stepResult);

        if (!execResult.Success)
        {
          allSuccess = false;
          lastError = stepResult.Error;

          if (!step.ContinueOnError)
          {
            _logger.LogWarning("Step '{Step}' failed, stopping workflow: {Error}", step.Name, lastError);
            break;
          }

          _logger.LogWarning("Step '{Step}' failed but continuing: {Error}", step.Name, lastError);
        }
        else
        {
          _logger.LogInformation("Step '{Step}' succeeded", step.Name);
        }
      }
    }

    sw.Stop();

    // Aggregate stdout across successful steps for {{output}} substitution
    var aggregatedOutput = string.Join("\n",
      stepResults.Where(s => s.Success && !string.IsNullOrWhiteSpace(s.Output))
                 .Select(s => s.Output!.Trim()));

    var paramsWithOutput = new Dictionary<string, string>(match.Parameters)
    {
      ["output"] = aggregatedOutput
    };

    // Build response message
    string message;
    if (allSuccess)
    {
      message = SubstituteParams(match.Skill.Responses.Success, paramsWithOutput);
    }
    else if (stepResults.Any(s => s.Success) && match.Skill.Responses.Partial != null)
    {
      message = SubstituteParams(match.Skill.Responses.Partial, paramsWithOutput, lastError);
    }
    else
    {
      message = SubstituteParams(match.Skill.Responses.Failure, paramsWithOutput, lastError);
    }

    return new SkillExecutionResult
    {
      Success = allSuccess,
      Message = message,
      StepResults = stepResults,
      DurationMs = sw.ElapsedMilliseconds
    };
  }

  private static string NormalizePlatform(string? platform)
  {
    if (string.IsNullOrWhiteSpace(platform)) return "unknown";
    var p = platform.Trim().ToLowerInvariant();
    if (p.Contains("win")) return "windows";
    if (p.Contains("darwin") || p.Contains("mac")) return "macos";
    if (p.Contains("linux") || p.Contains("ubuntu") || p.Contains("debian") ||
        p.Contains("rhel") || p.Contains("centos") || p.Contains("fedora") ||
        p.Contains("alpine")) return "linux";
    return p;
  }

  private static string? SelectCommandForPlatform(WorkflowStep step, string platform)
  {
    if (step.Commands != null && step.Commands.Count > 0)
    {
      foreach (var kvp in step.Commands)
      {
        if (string.Equals(kvp.Key, platform, StringComparison.OrdinalIgnoreCase))
          return kvp.Value;
      }

      if (step.Commands.TryGetValue("default", out var def))
        return def;
    }

    return step.Command;
  }

  private static string SubstituteParams(string template, Dictionary<string, string> parameters, string? error = null)
  {
    var result = template;

    foreach (var (name, value) in parameters)
    {
      result = result.Replace($"{{{{{name}}}}}", value);
    }

    if (error != null)
    {
      result = result.Replace("{{error}}", error);
    }

    return result;
  }

  public IEnumerable<ExecutableSkill> GetAllSkills() => _skills.AsReadOnly();

  // Internal classes for YAML deserialization
  private class YamlSkillRaw
  {
    public string Name { get; set; } = "";
    public int Version { get; set; } = 1;
    public string? Description { get; set; }
    public YamlTriggers? Triggers { get; set; }
    public Dictionary<string, YamlParameter>? Parameters { get; set; }
    public YamlTarget? Target { get; set; }
    public YamlWorkflow? Workflow { get; set; }
    public YamlResponses? Responses { get; set; }
  }

  private class YamlTriggers
  {
    public List<string> Patterns { get; set; } = [];
    public List<string> Keywords { get; set; } = [];
  }

  private class YamlParameter
  {
    public string Pattern { get; set; } = "";
    public bool Required { get; set; } = true;
    public string? Description { get; set; }
    public string? Default { get; set; }
  }

  private class YamlTarget
  {
    public string Agent { get; set; } = "";
    public string? Description { get; set; }
  }

  private class YamlWorkflow
  {
    public List<YamlStep>? Steps { get; set; }
  }

  private class YamlStep
  {
    public string? Name { get; set; }
    public string? Command { get; set; }
    public Dictionary<string, string>? Commands { get; set; }
    public bool ContinueOnError { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
  }

  private class YamlResponses
  {
    public string Success { get; set; } = "Done.";
    public string? Partial { get; set; }
    public string Failure { get; set; } = "Failed.";
  }
}
