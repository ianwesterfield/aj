using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AJ.Orchestrator.Abstractions.Models.Classification;
using AJ.Orchestrator.Abstractions.Models.Tasks;
using AJ.Orchestrator.Abstractions.Models.Workspace;
using AJ.Orchestrator.Abstractions.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AJ.Orchestrator.Domain.Services;

/// <summary>
/// Reasoning engine that coordinates with the LLM for step generation.
/// Uses IChatClient (Microsoft.Extensions.AI) with server-enforced JSON-schema
/// structured outputs, so responses are valid JSON with a known tool enum.
/// Skill-aware: injects relevant skill instructions into context.
/// </summary>
public class ReasoningEngine : IReasoningEngine
{
  private readonly IChatClient _chatClient;
  private readonly ILogger<ReasoningEngine> _logger;
  private readonly IGrpcAgentClient _agentClient;
  private readonly SessionStateManager _sessionManager;
  private readonly ISkillDiscoveryService _skillDiscovery;
  private readonly ISkillExecutor _skillExecutor;

  private const string BaseSystemPrompt = """
        You are AJ, an infrastructure-aware AI assistant with access to FunnelCloud agents.
        
        RESPONSE FORMAT: Strict JSON, no markdown wrapping.
        {"tool": "name", "params": {...}, "reasoning": "brief why"}

        ═══════════════════════════════════════════════════════════════════════════════
        AVAILABLE TOOLS (only these four exist — do NOT invent others)
        ═══════════════════════════════════════════════════════════════════════════════
        
        • list_agents: {"params": {}}
          Discovers all CURRENTLY online FunnelCloud agents. Always fresh data.
        
        • execute: {"params": {"agent_id": "hostname", "command": "shell command"}}
          Runs a command on a specific agent. REQUIRES agent_id from list_agents.
        
        • think: {"params": {"thought": "planning text"}}
          Plan your approach before acting. Use for complex multi-step tasks.
        
        • complete: {"params": {"answer": "final response"}}
          Emit your final answer. See FINAL ANSWER section below.

        ═══════════════════════════════════════════════════════════════════════════════
        FINAL ANSWER BEHAVIOR
        ═══════════════════════════════════════════════════════════════════════════════
        
        When you emit {"tool": "complete", ...}:
        - The "answer" param IS the user-visible reply
        - Write your complete, well-formatted answer there — natural language only
        - Reason privately as much as you need; the answer must stand alone

        ═══════════════════════════════════════════════════════════════════════════════
        WHEN TO USE TOOLS vs COMPLETE IMMEDIATELY
        ═══════════════════════════════════════════════════════════════════════════════
        
        COMPLETE IMMEDIATELY (no tools) when:
        ✓ User asks about themselves (name, preferences) → use Memory Context
        ✓ User asks a general knowledge question → answer directly
        ✓ User asks "how do I..." or "what is..." → answer from knowledge, don't execute
        ✓ User greets you or makes small talk → respond warmly
        ✓ User asks what you can do → describe capabilities
        ✓ Simple math, facts, explanations → just answer
        
        USE TOOLS when user REQUESTS AN ACTION on real infrastructure:
        ✗ "ping X from Y" / "restart service on Z" → execute
        ✗ "is agent X online?" / "what agents are available?" → list_agents
        ✗ "check/modify infrastructure state" → tools needed

        ═══════════════════════════════════════════════════════════════════════════════
        INFRASTRUCTURE TASK WORKFLOW
        ═══════════════════════════════════════════════════════════════════════════════
        
        1. DISCOVER: Call list_agents to see what's online
        2. ANALYZE: Match agent capabilities to task (postfix01→mail, domain01→AD)
        3. EXECUTE: Run commands with execute + agent_id
        4. COMPLETE: Summarize results immediately after execute returns

        ═══════════════════════════════════════════════════════════════════════════════
        CRITICAL RULES
        ═══════════════════════════════════════════════════════════════════════════════
        
        ACCURACY:
        1. NEVER fabricate command output, IPs, hostnames, or system data.
        2. TRUST YOUR TOOL RESULTS. Data in "Previous steps" is REAL and CURRENT.
           - Never say "I don't have real-time data" when tools just gave you data.
           - Quote/summarize tool output directly in your answer.
        3. If a tool FAILED, report the failure honestly. Don't invent content.
        
        EFFICIENCY:
        4. For infrastructure tasks, ALWAYS list_agents before execute.
        5. COMPLETE AS SOON AS POSSIBLE. Once you have the answer, stop.
        6. STOP AFTER FAILURES. If 2-3 attempts fail, call complete with error explanation.
        7. DO NOT REPEAT failed commands. Report empty results and move on.
        
        OUTPUT:
        8. Answer ONLY what was asked. No follow-up questions unless asked.
        9. complete.answer must be NATURAL LANGUAGE, never JSON or code.
        10. Use Memory Context to answer personal questions about the user.

        ═══════════════════════════════════════════════════════════════════════════════
        SKILL INTERPRETATION
        ═══════════════════════════════════════════════════════════════════════════════
        
        Skills provide workflow guidance and command patterns. Interpret them as:
        - WHICH AGENT handles a task type (targetAgent field or infer from description)
        - COMMAND PATTERNS (syntax/structure of commands)
        - WORKFLOW (sequence of operations)
        
        IMPORTANT: Example values in skills are PLACEHOLDERS.
        Extract ACTUAL values from the USER'S REQUEST — that's the source of truth.
        
        Example: Skill shows "echo 'spammer.com REJECT'" 
                 User says "block jojoscarinsurance.com"
                 → Use "jojoscarinsurance.com", not the example domain

        ═══════════════════════════════════════════════════════════════════════════════
        EXAMPLES
        ═══════════════════════════════════════════════════════════════════════════════
        
        Conversational:
        {"tool": "complete", "params": {"answer": "Your name is Ian."}, "reasoning": "from memory"}
        
        Infrastructure discovery:
        {"tool": "list_agents", "params": {}, "reasoning": "finding available agents"}
        
        After seeing postfix01 in agent list:
        {"tool": "execute", "params": {"agent_id": "postfix01", "command": "..."}, "reasoning": "postfix01 handles mail"}
        
        After successful execute:
        {"tool": "complete", "params": {"answer": "Done. Added to block list and reloaded postfix."}, "reasoning": "task complete"}
        """;

  public ReasoningEngine(
      IChatClient chatClient,
      ILogger<ReasoningEngine> logger,
      IGrpcAgentClient agentClient,
      SessionStateManager sessionManager,
      ISkillDiscoveryService skillDiscovery,
      ISkillExecutor skillExecutor)
  {
    _chatClient = chatClient;
    _logger = logger;
    _agentClient = agentClient;
    _sessionManager = sessionManager;
    _skillDiscovery = skillDiscovery;
    _skillExecutor = skillExecutor;
  }

  // Server-enforced response schema for intent classification.
  private static readonly ChatResponseFormatJson s_classifySchema = ChatResponseFormat.ForJsonSchema(
      JsonSerializer.SerializeToElement(new
      {
        type = "object",
        properties = new Dictionary<string, object>
        {
          ["intent"] = new { type = "string", @enum = new[] { "task", "casual", "save", "recall" } },
          ["confidence"] = new { type = "number" }
        },
        required = new[] { "intent", "confidence" },
        additionalProperties = false
      }),
      schemaName: "intent_classification",
      schemaDescription: "Intent category and confidence for the user's message");

  public async Task<ClassifyResponse> ClassifyIntentAsync(string text, string? context = null)
  {
    // Use the same Ollama LLM for intent classification (no DistilBERT)
    try
    {
      var systemPrompt = @"You are an intent classifier. Classify the user's message into exactly ONE category.

Categories:
- ""task"": User wants to perform an action, run commands, execute code, access websites, modify files, or get work done on infrastructure
- ""casual"": Greeting, small talk, general questions, math problems, explanations, or conversation without requiring system actions
- ""save"": User explicitly asks to remember/store information (""remember that..."", ""save this..."")
- ""recall"": User asks what you remember (""what's my..."", ""do you remember..."")

Examples:
- ""ping google.com from my server"" → task
- ""what is half of 11 3/4?"" → casual
- ""how do I configure nginx?"" → casual (explanation, not action)
- ""restart nginx on web01"" → task (action on infrastructure)
- ""hello!"" → casual
- ""remember my favorite color is blue"" → save
- ""what's my name?"" → recall

Respond with ONLY a JSON object: {""intent"": ""<category>"", ""confidence"": <0.0-1.0>}";

      var userPrompt = string.IsNullOrEmpty(context)
          ? text
          : $"Previous context: {context}\n\nCurrent message: {text}";

      var (responseText, _) = await CallLlmAsync(userPrompt, systemPrompt, s_classifySchema);

      var parsed = JsonSerializer.Deserialize<JsonElement>(responseText);
      var intent = parsed.GetProperty("intent").GetString() ?? "task";
      var confidence = parsed.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0.7;

      _logger.LogInformation("LLM classified intent: {Intent} ({Confidence:P0})", intent, confidence);
      return new ClassifyResponse(intent, confidence, "LLM classification");
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to classify intent via LLM, defaulting to task");
      return new ClassifyResponse("task", 0.5, "Classification error");
    }
  }

  // Server-enforced response schema for memory fact extraction.
  private static readonly ChatResponseFormatJson s_extractFactsSchema = ChatResponseFormat.ForJsonSchema(
      JsonSerializer.SerializeToElement(new
      {
        type = "object",
        properties = new Dictionary<string, object>
        {
          ["worth_remembering"] = new { type = "boolean" },
          ["summary"] = new { type = "string" }
        },
        required = new[] { "worth_remembering", "summary" },
        additionalProperties = false
      }),
      schemaName: "memory_extraction",
      schemaDescription: "Whether the message contains memory-worthy personal info, and a concise summary of it");

  public async Task<ExtractFactsResponse> ExtractFactsAsync(string text)
  {
    if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 10)
    {
      return new ExtractFactsResponse(null, []);
    }

    try
    {
      var systemPrompt = """
          You decide what is worth REMEMBERING about a user from their message.

          Worth remembering:
          - Personal identity (name, location, job)
          - Relationships (spouse, family, pets)
          - Preferences and opinions ("I prefer...", "I like...", "I hate...")
          - Custom terminology ("when I say X, I mean Y")
          - Important context for future conversations

          NOT worth remembering:
          - General questions (e.g., "What is the capital of France?")
          - Casual mentions without preference signals
          - Technical requests without personal info

          If worth remembering, set worth_remembering=true and write a concise
          1-3 sentence summary. Otherwise set worth_remembering=false and summary="".
          """;

      var (responseText, _) = await CallLlmAsync(text, systemPrompt, s_extractFactsSchema);

      var parsed = JsonSerializer.Deserialize<JsonElement>(responseText);
      var worthRemembering = parsed.TryGetProperty("worth_remembering", out var w) && w.GetBoolean();
      var summary = parsed.TryGetProperty("summary", out var s) ? s.GetString()?.Trim() : null;

      if (!worthRemembering || string.IsNullOrWhiteSpace(summary))
      {
        return new ExtractFactsResponse(null, []);
      }

      _logger.LogInformation("Memory extraction: {Summary}", summary);
      return new ExtractFactsResponse(summary, [new ExtractedFact("memory", summary)]);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to extract facts via LLM, skipping memory save");
      return new ExtractFactsResponse(null, []);
    }
  }

  public async Task<NextStepResponse> GenerateNextStepAsync(
      string task,
      List<StepResult> history,
      WorkspaceContext? workspace = null,
      List<Dictionary<string, object?>>? memoryContext = null)
  {
    // Find relevant skills for this task
    var relevantSkills = _skillDiscovery.FindRelevantSkills(task).Take(3).ToList();

    if (relevantSkills.Count > 0)
    {
      _logger.LogInformation("Found {Count} relevant skill(s) for task: {Skills}",
          relevantSkills.Count, string.Join(", ", relevantSkills.Select(s => s.Name)));
    }

    var prompt = BuildPrompt(task, history, workspace, memoryContext);
    var systemPrompt = BuildSystemPromptWithSkills(relevantSkills);

    var (text, thinking) = await CallLlmAsync(prompt, systemPrompt, BuildNextStepSchema());
    var parsed = ParseResponse(text, thinking);

    return new NextStepResponse(
        parsed.Tool,
        parsed.Params,
        parsed.BatchId,
        parsed.Reasoning,
        false,
        parsed.ThinkingContent);
  }

  /// <summary>
  /// Response schema for next-step generation. Each built-in tool is bound to its
  /// exact params shape via anyOf so the grammar can't produce nested/garbage
  /// params (e.g. an object inside agent_id). Dynamically loaded skills get a
  /// free-form params object since their parameters vary.
  /// </summary>
  private ChatResponseFormatJson BuildNextStepSchema()
  {
    var skillNames = _skillExecutor.GetAllSkills()
        .Select(s => s.Name)
        .Where(n => n is not ("list_agents" or "execute" or "think" or "complete"))
        .Distinct()
        .ToArray();

    var variants = new List<object>
    {
      new
      {
        type = "object",
        properties = new Dictionary<string, object>
        {
          ["tool"] = new { type = "string", @enum = new[] { "list_agents" } },
          ["params"] = new { type = "object", additionalProperties = false },
          ["reasoning"] = new { type = "string" }
        },
        required = new[] { "tool", "params" },
        additionalProperties = false
      },
      new
      {
        type = "object",
        properties = new Dictionary<string, object>
        {
          ["tool"] = new { type = "string", @enum = new[] { "execute" } },
          ["params"] = new
          {
            type = "object",
            properties = new Dictionary<string, object>
            {
              ["agent_id"] = new { type = "string" },
              ["command"] = new { type = "string" }
            },
            required = new[] { "agent_id", "command" },
            additionalProperties = false
          },
          ["reasoning"] = new { type = "string" }
        },
        required = new[] { "tool", "params" },
        additionalProperties = false
      },
      new
      {
        type = "object",
        properties = new Dictionary<string, object>
        {
          ["tool"] = new { type = "string", @enum = new[] { "think" } },
          ["params"] = new
          {
            type = "object",
            properties = new Dictionary<string, object> { ["thought"] = new { type = "string" } },
            required = new[] { "thought" },
            additionalProperties = false
          },
          ["reasoning"] = new { type = "string" }
        },
        required = new[] { "tool", "params" },
        additionalProperties = false
      },
      new
      {
        type = "object",
        properties = new Dictionary<string, object>
        {
          ["tool"] = new { type = "string", @enum = new[] { "complete" } },
          ["params"] = new
          {
            type = "object",
            properties = new Dictionary<string, object> { ["answer"] = new { type = "string" } },
            required = new[] { "answer" },
            additionalProperties = false
          },
          ["reasoning"] = new { type = "string" }
        },
        required = new[] { "tool", "params" },
        additionalProperties = false
      }
    };

    if (skillNames.Length > 0)
    {
      variants.Add(new
      {
        type = "object",
        properties = new Dictionary<string, object>
        {
          ["tool"] = new { type = "string", @enum = skillNames },
          ["params"] = new { type = "object", additionalProperties = new { type = "string" } },
          ["reasoning"] = new { type = "string" }
        },
        required = new[] { "tool", "params" },
        additionalProperties = false
      });
    }

    return ChatResponseFormat.ForJsonSchema(
        JsonSerializer.SerializeToElement(new { anyOf = variants }),
        schemaName: "next_step",
        schemaDescription: "The next tool call in the agentic loop");
  }

  public async IAsyncEnumerable<TaskEvent> RunTaskStreamAsync(RunTaskRequest request, [EnumeratorCancellation] CancellationToken ct = default)
  {
    var session = _sessionManager.GetOrCreate(request.UserId ?? "default");

    if (!request.PreserveState)
    {
      session.Reset();
    }

    // ===== LLM-FIRST ARCHITECTURE: Let the LLM decide what tools/skills to use =====
    // No pattern matching - the LLM sees available skills and decides when to use them

    // Find skills once at task start - used for command syntax guidance
    var relevantSkills = _skillDiscovery.FindRelevantSkills(request.Task).Take(3).ToList();

    // Emit status showing which skills are being used for context
    if (relevantSkills.Count > 0)
    {
      var skillNames = string.Join(", ", relevantSkills.Select(s => s.Name));
      yield return new TaskEvent
      {
        EventType = "status",
        StepNum = 0,
        Status = $"📚 Using skills: {skillNames}"
      };
    }

    // Track whether agents have been discovered this task
    var agentsDiscovered = false;

    var stepNum = 0;
    var maxSteps = request.MaxSteps;

    while (stepNum < maxSteps && !ct.IsCancellationRequested)
    {
      stepNum++;

      // Generate next step
      yield return new TaskEvent
      {
        EventType = "status",
        StepNum = stepNum,
        Status = "Reasoning..."
      };

      var nextStep = await GenerateNextStepAsync(
          request.Task,
          session.History.ToList(),
          new WorkspaceContext { Cwd = request.WorkspaceRoot, WorkspaceRoot = request.WorkspaceRoot },
          request.MemoryContext);

      // Debug logging
      _logger.LogInformation("Step {Step}: Parsed tool={Tool}, params={@Params}",
          stepNum, nextStep.Tool, nextStep.Params.Keys);

      // Emit thinking content if available (the LLM's chain-of-thought).
      // For the "complete" step we skip this emission because the thinking content
      // is used AS the final answer below — emitting it here would duplicate it.
      if (!string.IsNullOrWhiteSpace(nextStep.ThinkingContent) && nextStep.Tool != "complete")
      {
        yield return new TaskEvent
        {
          EventType = "thinking",
          StepNum = stepNum,
          Status = "Thinking",
          Content = nextStep.ThinkingContent
        };
      }

      yield return new TaskEvent
      {
        EventType = "step",
        StepNum = stepNum,
        Tool = nextStep.Tool,
        Status = BuildStepStatus(nextStep),
        Result = new Dictionary<string, object?>
        {
          ["tool"] = nextStep.Tool,
          ["params"] = nextStep.Params,
          ["reasoning"] = nextStep.Reasoning
        }
      };

      // Execute the step
      StepResult stepResult;
      if (nextStep.Tool == "complete")
      {
        // The schema-constrained "answer" param is the final answer. Thinking is
        // a separate raw-CoT channel now (llama-server --reasoning-format auto),
        // so it only serves as fallback when the answer param is missing/empty
        // (legacy R1-style serving where the answer lived in the think block).
        var answerParam = nextStep.Params.GetValueOrDefault("answer")?.ToString();
        var thinkingAsAnswer = nextStep.ThinkingContent?.Trim();
        var finalAnswer = !string.IsNullOrWhiteSpace(answerParam)
          ? answerParam
          : thinkingAsAnswer;

        stepResult = new StepResult
        {
          StepId = $"step-{stepNum}",
          Tool = "complete",
          Status = StepStatus.Success,
          Output = finalAnswer
        };

        session.AddResult(stepResult);

        yield return new TaskEvent
        {
          EventType = "complete",
          StepNum = stepNum,
          Tool = "complete",
          Status = "Task completed",
          Result = new Dictionary<string, object?>
          {
            ["answer"] = stepResult.Output
          },
          Done = true
        };

        yield break;
      }
      else if (nextStep.Tool == "list_agents")
      {
        // Built-in tool: call agent discovery directly (no shell execution needed)
        var agents = await _agentClient.DiscoverAgentsAsync(ct);
        var agentList = string.Join("\n", agents.Select(a => $"- {a.AgentId} ({a.Hostname}, {a.Platform})"));
        var output = $"Found {agents.Count} agent(s):\n{agentList}";

        agentsDiscovered = true;

        stepResult = new StepResult
        {
          StepId = $"step-{stepNum}",
          Tool = "list_agents",
          Status = StepStatus.Success,
          Output = output
        };
      }
      else if (nextStep.Tool == "execute")
      {
        // Guard: must discover agents before executing
        if (!agentsDiscovered)
        {
          _logger.LogWarning("execute called before list_agents at step {Step}", stepNum);
          stepResult = new StepResult
          {
            StepId = $"step-{stepNum}",
            Tool = "execute",
            Status = StepStatus.Failed,
            Error = "You must call list_agents first to discover available agents before executing commands."
          };
        }
        else
        {
          var agentId = nextStep.Params.GetValueOrDefault("agent_id")?.ToString();
          var command = nextStep.Params.GetValueOrDefault("command")?.ToString() ?? "";

          // Require agent_id
          if (string.IsNullOrWhiteSpace(agentId))
          {
            _logger.LogWarning("execute called without agent_id at step {Step}", stepNum);
            stepResult = new StepResult
            {
              StepId = $"step-{stepNum}",
              Tool = "execute",
              Status = StepStatus.Failed,
              Error = "Missing agent_id. Specify which agent to run the command on."
            };
          }
          else
          {
            var execResult = await _agentClient.ExecuteAsync(agentId, command, 3600, ct);

            stepResult = new StepResult
            {
              StepId = $"step-{stepNum}",
              Tool = "execute",
              Status = execResult.Success ? StepStatus.Success : StepStatus.Failed,
              Output = execResult.Stdout,
              Error = execResult.Stderr ?? execResult.ErrorMessage,
              ExecutionTime = execResult.DurationMs / 1000.0
            };
          }
        }
      }
      else if (nextStep.Tool == "think")
      {
        stepResult = new StepResult
        {
          StepId = $"step-{stepNum}",
          Tool = "think",
          Status = StepStatus.Success,
          Output = nextStep.Params.GetValueOrDefault("thought")?.ToString()
        };
      }
      else
      {
        // Check if LLM called a skill by name - try to execute it
        var skillByName = _skillExecutor.GetSkillByName(nextStep.Tool);
        if (skillByName != null)
        {
          _logger.LogInformation("LLM called skill by name: {SkillName}", nextStep.Tool);

          // Build a SkillMatch from LLM params
          var llmSkillMatch = new SkillMatch
          {
            Skill = skillByName,
            Parameters = nextStep.Params.ToDictionary(
              kvp => kvp.Key,
              kvp => kvp.Value?.ToString() ?? ""),
            Confidence = 1.0,
            MatchedBy = "LLM tool call"
          };

          var execResult = await _skillExecutor.ExecuteAsync(llmSkillMatch, ct);

          stepResult = new StepResult
          {
            StepId = $"step-{stepNum}",
            Tool = nextStep.Tool,
            Status = execResult.Success ? StepStatus.Success : StepStatus.Failed,
            Output = execResult.Message,
            Error = execResult.Success ? null : execResult.Message
          };
        }
        else
        {
          stepResult = new StepResult
          {
            StepId = $"step-{stepNum}",
            Tool = nextStep.Tool,
            Status = StepStatus.Failed,
            Error = $"Unknown tool: {nextStep.Tool}"
          };
        }
      }

      session.AddResult(stepResult);

      yield return new TaskEvent
      {
        EventType = "result",
        StepNum = stepNum,
        Tool = nextStep.Tool,
        Status = stepResult.Status == StepStatus.Success ? "Success" : "Failed",
        Result = new Dictionary<string, object?>
        {
          ["output_preview"] = stepResult.Output,
          ["error"] = stepResult.Error,
          ["execution_time"] = stepResult.ExecutionTime
        },
        // Include params at event level for filter compatibility
        Params = nextStep.Params
      };
    }

    // Max steps reached
    yield return new TaskEvent
    {
      EventType = "error",
      StepNum = stepNum,
      Status = "Max steps reached",
      Done = true
    };
  }

  private string BuildPrompt(string task, List<StepResult> history, WorkspaceContext? workspace, List<Dictionary<string, object?>>? memoryContext = null)
  {
    var sb = new StringBuilder();
    sb.AppendLine($"Task: {task}");

    // Include memory context if provided (user facts, preferences, etc.)
    if (memoryContext != null && memoryContext.Count > 0)
    {
      sb.AppendLine("\nMemory Context (facts about the user):");
      foreach (var item in memoryContext.Take(5)) // Limit to top 5 most relevant
      {
        if (item.TryGetValue("user_text", out var userText))
        {
          sb.AppendLine($"- {userText}");
        }
        // Also check for facts in various formats
        if (item.TryGetValue("facts", out var facts) && facts is string factsStr && !string.IsNullOrWhiteSpace(factsStr))
        {
          foreach (var line in factsStr.Split('\n').Take(3))
          {
            sb.AppendLine($"  → {line.Trim()}");
          }
        }
      }
      sb.AppendLine("(Use this context to answer personal questions about the user)");
    }

    // Note: Removed workspace display - was confusing the model into thinking "user mentioned workspace"

    if (history.Count > 0)
    {
      sb.AppendLine("\nPrevious steps:");
      foreach (var step in history)
      {
        var marker = step.Status == StepStatus.Success ? "✓" : "✗";
        string detail;
        if (step.Status == StepStatus.Success)
        {
          var output = step.Output ?? "";
          if (output.Length > 10000)
          {
            // Truncate but mark as truncated (not incomplete)
            detail = output[..10000] + "\n[OUTPUT TRUNCATED - data above is valid, use it]";
          }
          else
          {
            detail = output;
          }
        }
        else
        {
          detail = $"ERROR: {step.Error ?? "unknown"}";
        }
        sb.AppendLine($"- {step.Tool}: {marker} {detail}");
      }

      // Nudge completion after successful tool calls that produce data
      var executeSuccesses = history.Where(s => s.Status == StepStatus.Success && s.Tool == "execute").ToList();
      if (executeSuccesses.Any())
      {
        sb.AppendLine("\n✅ EXECUTE SUCCEEDED: You have command output from the steps above.");
        sb.AppendLine("If this answers the user's question, call complete NOW with this data.");
      }

      // Also nudge completion after list_agents if user asked an agent-related question
      var listAgentsSuccesses = history.Where(s => s.Status == StepStatus.Success && s.Tool == "list_agents").ToList();
      if (listAgentsSuccesses.Any() && !executeSuccesses.Any())
      {
        sb.AppendLine("\n✅ AGENT LIST RETRIEVED: You now have the list of available agents.");
        sb.AppendLine("If the user only asked about agents (count, status, list), call complete NOW with this data.");
        sb.AppendLine("Only call execute if you need to RUN A COMMAND on an agent.");
      }

      // LOOP DETECTION: If the same tool was called 2+ times consecutively, force completion
      var recentTools = history.AsEnumerable().Reverse().Take(3).Select(s => s.Tool).ToList();
      if (recentTools.Count >= 2 && recentTools.Distinct().Count() == 1 && recentTools[0] != "complete")
      {
        sb.AppendLine($"\n🛑 LOOP DETECTED: You called '{recentTools[0]}' multiple times in a row.");
        sb.AppendLine("You MUST call complete NOW. Report whatever output you have, even if empty.");
      }

      // EMPTY OUTPUT DETECTION: If the last execute returned empty stdout, do not retry
      var lastExecute = history.LastOrDefault(s => s.Tool == "execute");
      if (lastExecute != null && lastExecute.Status == StepStatus.Success && string.IsNullOrWhiteSpace(lastExecute.Output))
      {
        sb.AppendLine("\n⚠️ The last execute call returned EMPTY output. Do NOT retry. Call complete and report that the command produced no output.");
      }

      // Count recent failures — if last 2+ steps failed, strongly nudge to complete
      var recentFailures = history.AsEnumerable().Reverse().TakeWhile(s => s.Status == StepStatus.Failed).Count();
      if (recentFailures >= 2)
      {
        sb.AppendLine($"\n⚠️ WARNING: The last {recentFailures} attempts have FAILED. Do NOT keep trying.");
        sb.AppendLine("Call complete NOW with a summary of what you tried and why it failed.");
      }

      // If total failures exceed threshold, force completion
      var totalFailures = history.Count(s => s.Status == StepStatus.Failed);
      if (totalFailures >= 3)
      {
        sb.AppendLine($"\n🛑 STOP: {totalFailures} steps have failed. You MUST call complete now.");
        sb.AppendLine("Summarize what worked, what failed, and provide your best answer or explanation.");
      }
    }

    sb.AppendLine("\nGenerate the next step as JSON:");
    return sb.ToString();
  }

  private string BuildSystemPromptWithSkills(IEnumerable<AJ.Orchestrator.Abstractions.Models.Skills.Skill> skills)
  {
    var sb = new System.Text.StringBuilder();
    sb.AppendLine(BaseSystemPrompt);

    // Add executable skills as callable tools
    var executableSkills = _skillExecutor.GetAllSkills().ToList();
    if (executableSkills.Count > 0)
    {
      sb.AppendLine();
      sb.AppendLine("## CALLABLE SKILLS (use as tools)");
      sb.AppendLine("These skills can be called directly by name. Use them when the user's request matches their purpose.");
      sb.AppendLine();

      foreach (var skill in executableSkills)
      {
        var desc = skill.Description ?? skill.Responses?.Success ?? "Execute skill workflow";
        sb.AppendLine($"- **{skill.Name}**: {desc}");
        if (skill.Parameters?.Count > 0)
        {
          var paramDescs = skill.Parameters.Select(p =>
            $"{p.Key}{(p.Value.Required ? " (required)" : "")}: {p.Value.Description ?? "parameter"}");
          sb.AppendLine($"  Parameters: {string.Join("; ", paramDescs)}");
        }
        if (!string.IsNullOrEmpty(skill.Target?.Agent))
        {
          sb.AppendLine($"  Target: {skill.Target.Agent}");
        }
      }

      sb.AppendLine();
      sb.AppendLine("To use a skill, respond: {\"tool\": \"skill-name\", \"params\": {...}}");
      sb.AppendLine("Example: {\"tool\": \"web-fetch\", \"params\": {\"url\": \"https://example.com\"}}");
    }

    // Add SKILL.md workflow guides
    var skillContext = _skillDiscovery.FormatSkillContext(skills);
    if (!string.IsNullOrEmpty(skillContext))
    {
      sb.AppendLine();
      sb.AppendLine(skillContext);
    }

    return sb.ToString();
  }

  /// <summary>
  /// Call the LLM via IChatClient. Returns the response text and any reasoning
  /// (thinking) content. Reasoning arrives either as structured
  /// TextReasoningContent (server-side reasoning parsing, e.g. llama-server
  /// --reasoning-format auto) or embedded &lt;think&gt; blocks in the text (legacy).
  /// </summary>
  private async Task<(string Text, string Thinking)> CallLlmAsync(
      string prompt, string systemPrompt, ChatResponseFormatJson? responseFormat = null)
  {
    var messages = new List<ChatMessage>
    {
      new(ChatRole.System, systemPrompt),
      new(ChatRole.User, prompt),
    };

    var options = new ChatOptions { Temperature = 0.1f };
    if (responseFormat is not null)
      options.ResponseFormat = responseFormat;

    _logger.LogDebug("Calling LLM with prompt:\n{Prompt}", prompt);

    var response = await _chatClient.GetResponseAsync(messages, options);

    var thinking = string.Concat(response.Messages
        .SelectMany(m => m.Contents)
        .OfType<TextReasoningContent>()
        .Select(c => c.Text)).Trim();

    var text = response.Text;

    // Legacy path: models without server-side reasoning parsing embed <think> in text
    if (string.IsNullOrEmpty(thinking))
    {
      thinking = ExtractThinkingContent(text);
      text = StripThinkingBlocks(text);
    }

    _logger.LogInformation("LLM raw response ({Length} chars): {Response}",
        text.Length, text.Length > 500 ? text[..500] + "..." : text);

    return (text, thinking);
  }

  /// <summary>
  /// Parse the LLM response into a step. With server-enforced JSON schema the
  /// text is valid JSON with a known tool name; the fallbacks below only fire
  /// when talking to an endpoint without grammar support.
  /// </summary>
  private ParsedStep ParseResponse(string text, string thinkingContent)
  {
    try
    {
      // Tolerate stray prose around the JSON (non-grammar endpoints only)
      var jsonStart = text.IndexOf('{');
      var jsonEnd = text.LastIndexOf('}');

      if (jsonStart >= 0 && jsonEnd > jsonStart)
      {
        var json = text.Substring(jsonStart, jsonEnd - jsonStart + 1);
        var parsed = JsonSerializer.Deserialize<JsonElement>(json);

        var tool = parsed.TryGetProperty("tool", out var t) ? t.GetString() ?? "unknown" : "unknown";

        var paramsDict = new Dictionary<string, object?>();
        if (parsed.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
        {
          foreach (var prop in p.EnumerateObject())
          {
            paramsDict[prop.Name] = prop.Value.ValueKind switch
            {
              JsonValueKind.String => prop.Value.GetString(),
              JsonValueKind.Number => prop.Value.GetDouble(),
              JsonValueKind.True => true,
              JsonValueKind.False => false,
              _ => prop.Value.ToString()
            };
          }
        }

        var reasoning = parsed.TryGetProperty("reasoning", out var r) ? r.GetString() ?? "" : "";

        return new ParsedStep(tool, paramsDict, null, reasoning, thinkingContent);
      }
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to parse LLM response ({Length} chars): {Response}",
          text.Length, text.Length > 1000 ? text[..1000] + "..." : text);
    }

    // If we have thinking content but no valid JSON, use thinking as the answer
    if (!string.IsNullOrWhiteSpace(thinkingContent))
    {
      _logger.LogInformation("Using thinking content as fallback answer ({Length} chars)", thinkingContent.Length);
      return new ParsedStep("complete", new Dictionary<string, object?> { ["answer"] = thinkingContent }, null, "Thinking fallback", thinkingContent);
    }

    // Last resort: use any meaningful response text as the answer
    var cleanedResponse = text.Trim();
    if (!string.IsNullOrWhiteSpace(cleanedResponse) && cleanedResponse.Length > 10)
    {
      _logger.LogInformation("Using cleaned response as fallback answer ({Length} chars)", cleanedResponse.Length);
      return new ParsedStep("complete", new Dictionary<string, object?> { ["answer"] = cleanedResponse }, null, "Text fallback", thinkingContent);
    }

    return new ParsedStep("complete", new Dictionary<string, object?> { ["answer"] = "Task completed." }, null, "Parse error", thinkingContent);
  }

  /// <summary>
  /// Build a user-visible status string for a step, including key params (agent + command)
  /// so the UI banner shows what is actually running instead of a generic "Executing execute".
  /// </summary>
  private static string BuildStepStatus(NextStepResponse step)
  {
    var tool = step.Tool ?? "";
    var p = step.Params ?? new Dictionary<string, object?>();

    if (tool == "execute" || tool == "remote_execute")
    {
      var agent = p.GetValueOrDefault("agent_id")?.ToString()
                ?? p.GetValueOrDefault("target_agent")?.ToString()
                ?? p.GetValueOrDefault("host")?.ToString();
      var cmd = p.GetValueOrDefault("command")?.ToString()
              ?? p.GetValueOrDefault("cmd")?.ToString()
              ?? "";
      cmd = cmd.Trim();
      if (cmd.Length > 120) cmd = cmd.Substring(0, 117) + "...";
      if (!string.IsNullOrEmpty(agent) && !string.IsNullOrEmpty(cmd))
        return $"Executing on {agent}: {cmd}";
      if (!string.IsNullOrEmpty(cmd))
        return $"Executing: {cmd}";
    }

    if (tool == "list_agents")
      return "Discovering agents";

    if (tool == "think")
      return "Thinking";

    if (tool == "complete")
      return "Finalizing answer";

    return $"Executing {tool}";
  }

  /// <summary>
  /// Extract the content of &lt;think&gt;...&lt;/think&gt; blocks from the model output.
  /// Returns the thinking content for streaming to the UI, or empty string if none.
  /// </summary>
  private static string ExtractThinkingContent(string response)
  {
    // Match <think>content</think> and extract the inner content
    var match = Regex.Match(response, @"<think>([\s\S]*?)</think>", RegexOptions.IgnoreCase);
    if (match.Success)
      return match.Groups[1].Value.Trim();

    // Check for orphaned </think> - content before it might be thinking
    var closeIdx = response.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
    if (closeIdx > 0)
    {
      var beforeClose = response[..closeIdx].Trim();
      // Only return if it looks like thinking (has some substance)
      if (beforeClose.Length > 10 && !beforeClose.StartsWith("{"))
        return beforeClose;
    }

    return string.Empty;
  }

  /// <summary>
  /// Strip R1-Distill &lt;think&gt;...&lt;/think&gt; reasoning blocks from the model output.
  /// The model places its chain-of-thought inside these tags before the actual JSON response.
  /// </summary>
  private static string StripThinkingBlocks(string response)
  {
    // Handle both complete <think>...</think> and unclosed <think>... (model cut off)
    var result = Regex.Replace(response, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase);

    // Handle orphaned </think> without opening tag - take everything after it
    var closeThinkIdx = result.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
    if (closeThinkIdx >= 0)
    {
      result = result[(closeThinkIdx + "</think>".Length)..];
    }

    // If there's an unclosed <think> block (model was cut off mid-thought), strip from <think> to end
    // but only if there's no JSON after it
    var unclosedIdx = result.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
    if (unclosedIdx >= 0)
    {
      // Check if there's JSON after the unclosed think tag
      var afterThink = result[unclosedIdx..];
      var jsonAfter = afterThink.IndexOf('{');
      if (jsonAfter < 0)
      {
        // No JSON after unclosed think — strip it all
        result = result[..unclosedIdx];
      }
    }

    return result.Trim();
  }

  private record ParsedStep(string Tool, Dictionary<string, object?> Params, string? BatchId, string Reasoning, string ThinkingContent);
}
