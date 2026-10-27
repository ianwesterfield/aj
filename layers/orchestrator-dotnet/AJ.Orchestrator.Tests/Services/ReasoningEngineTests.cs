using AJ.Orchestrator.Abstractions.Models.Classification;
using AJ.Orchestrator.Abstractions.Models.Skills;
using AJ.Orchestrator.Abstractions.Models.Tasks;
using AJ.Orchestrator.Abstractions.Models.Workspace;
using AJ.Orchestrator.Abstractions.Services;
using AJ.Orchestrator.Domain.Services;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json;

namespace AJ.Orchestrator.Tests.Services;

/// <summary>
/// Tests for ReasoningEngine service.
/// </summary>
public class ReasoningEngineTests
{
  private readonly Mock<ILogger<ReasoningEngine>> _loggerMock;
  private readonly Mock<IGrpcAgentClient> _agentClientMock;
  private readonly Mock<ISkillDiscoveryService> _skillDiscoveryMock;
  private readonly Mock<ISkillExecutor> _skillExecutorMock;
  private readonly SessionStateManager _sessionManager;

  public ReasoningEngineTests()
  {
    _loggerMock = new Mock<ILogger<ReasoningEngine>>();
    _agentClientMock = new Mock<IGrpcAgentClient>();
    _skillDiscoveryMock = new Mock<ISkillDiscoveryService>();
    _skillExecutorMock = new Mock<ISkillExecutor>();
    _sessionManager = new SessionStateManager();

    // Default skill discovery mock returns empty results
    _skillDiscoveryMock.Setup(s => s.FindRelevantSkills(It.IsAny<string>(), It.IsAny<string[]?>()))
        .Returns(Enumerable.Empty<Skill>());
    _skillDiscoveryMock.Setup(s => s.FormatSkillContext(It.IsAny<IEnumerable<Skill>>()))
        .Returns(string.Empty);

    // Default skill executor mock returns no match (fall through to LLM)
    _skillExecutorMock.Setup(s => s.TryMatch(It.IsAny<string>())).Returns((SkillMatch?)null);
    _skillExecutorMock.Setup(s => s.GetAllSkills())
        .Returns(Enumerable.Empty<AJ.Orchestrator.Abstractions.Models.Skills.ExecutableSkill>());
  }

  private ReasoningEngine CreateEngine(IChatClient? client = null)
  {
    client ??= CreateFakeChatClient(new { intent = "casual", confidence = 0.5, reason = "default" });

    return new ReasoningEngine(
        client,
        _loggerMock.Object,
        _agentClientMock.Object,
        _sessionManager,
        _skillDiscoveryMock.Object,
        _skillExecutorMock.Object);
  }

  #region ClassifyIntentAsync Tests

  [Fact]
  public async Task ClassifyIntentAsync_TaskIntent_ShouldReturnTask()
  {
    // Arrange
    var response = new { intent = "task", confidence = 0.95, reason = "User wants action" };
    var client = CreateFakeChatClient(response);
    var engine = CreateEngine(client);

    // Act
    var result = await engine.ClassifyIntentAsync("List all files in the directory");

    // Assert
    result.Intent.Should().Be("task");
    result.Confidence.Should().BeGreaterThan(0.9);
  }

  [Fact]
  public async Task ClassifyIntentAsync_CasualIntent_ShouldReturnCasual()
  {
    // Arrange
    var response = new { intent = "casual", confidence = 0.8, reason = "General conversation" };
    var client = CreateFakeChatClient(response);
    var engine = CreateEngine(client);

    // Act
    var result = await engine.ClassifyIntentAsync("Hello, how are you?");

    // Assert
    result.Intent.Should().Be("casual");
  }

  [Fact]
  public async Task ClassifyIntentAsync_ApiFailure_ShouldReturnDefaultTask()
  {
    // Arrange — server returns 500 → client throws
    var client = CreateThrowingChatClient(new HttpRequestException("Internal Server Error"));
    var engine = CreateEngine(client);

    // Act
    var result = await engine.ClassifyIntentAsync("Some text");

    // Assert — engine defaults to "task" on classification errors
    result.Intent.Should().Be("task");
    result.Confidence.Should().Be(0.5);
  }

  [Fact]
  public async Task ClassifyIntentAsync_NetworkError_ShouldReturnDefaultTask()
  {
    // Arrange
    var client = CreateThrowingChatClient(new HttpRequestException("Network error"));
    var engine = CreateEngine(client);

    // Act
    var result = await engine.ClassifyIntentAsync("Some text");

    // Assert — engine defaults to "task" on classification errors
    result.Intent.Should().Be("task");
    result.Reason.Should().Contain("error");
  }

  [Fact]
  public async Task ClassifyIntentAsync_WithContext_ShouldIncludeItInRequest()
  {
    // Arrange
    IList<ChatMessage>? capturedMessages = null;
    var client = new FakeChatClient(messages =>
    {
      capturedMessages = messages.ToList();
      return new ChatResponse(new ChatMessage(ChatRole.Assistant,
          JsonSerializer.Serialize(new { intent = "task", confidence = 0.9 })));
    });
    var engine = CreateEngine(client);

    // Act
    await engine.ClassifyIntentAsync("continue", "Previous conversation context");

    // Assert
    capturedMessages.Should().NotBeNull();
    var userMessage = capturedMessages!.First(m => m.Role == ChatRole.User).Text;
    userMessage.Should().Contain("context");
  }

  #endregion

  #region GenerateNextStepAsync Tests

  [Fact]
  public async Task GenerateNextStepAsync_ShouldReturnNextStep()
  {
    // Arrange
    var ollamaResponse = new
    {
      message = new
      {
        content = """{"tool": "execute", "params": {"command": "ls"}, "reasoning": "listing files"}"""
      }
    };

    var client = CreateFakeChatClient(ollamaResponse);
    var engine = CreateEngine(client);

    var history = new List<StepResult>();
    var workspace = new WorkspaceContext { Cwd = "/test", WorkspaceRoot = "/test" };

    // Act
    var result = await engine.GenerateNextStepAsync("List files", history, workspace);

    // Assert
    result.Should().NotBeNull();
    result.Tool.Should().NotBeNullOrEmpty();
  }

  #endregion

  #region RunTaskStreamAsync Tests

  [Fact]
  public async Task RunTaskStreamAsync_ShouldYieldEvents()
  {
    // Arrange - mock Ollama to return complete tool immediately
    var completeResponse = new
    {
      message = new
      {
        content = """{"tool": "complete", "params": {"answer": "Done"}, "reasoning": "Task complete"}"""
      }
    };

    var client = CreateFakeChatClient(completeResponse);
    var engine = CreateEngine(client);

    var request = new RunTaskRequest { Task = "Simple task", WorkspaceRoot = "/workspace", UserId = "test-user" };

    // Act
    var events = new List<TaskEvent>();
    await foreach (var evt in engine.RunTaskStreamAsync(request))
    {
      events.Add(evt);
    }

    // Assert
    events.Should().NotBeEmpty();
    events.Should().Contain(e => e.EventType == "status");
  }

  [Fact]
  public async Task RunTaskStreamAsync_PreserveStateFalse_ShouldResetSession()
  {
    // Arrange
    var session = _sessionManager.GetOrCreate("test-user");
    session.CurrentTask = "Previous task";
    session.AddResult(new StepResult { StepId = "old-step", Status = StepStatus.Success });

    var completeResponse = new
    {
      message = new
      {
        content = """{"tool": "complete", "params": {"answer": "Done"}, "reasoning": "done"}"""
      }
    };
    var client = CreateFakeChatClient(completeResponse);
    var engine = CreateEngine(client);

    var request = new RunTaskRequest { Task = "New task", WorkspaceRoot = "/workspace", UserId = "test-user", PreserveState = false };

    // Act
    await foreach (var _ in engine.RunTaskStreamAsync(request)) { }

    // Assert - session should have been reset
    var currentSession = _sessionManager.GetOrCreate("test-user");
    currentSession.StepCount.Should().BeGreaterThanOrEqualTo(1); // At least the complete step
  }

  [Fact]
  public async Task RunTaskStreamAsync_CancellationRequested_ShouldStop()
  {
    // Arrange
    var cts = new CancellationTokenSource();
    var stepResponse = new
    {
      message = new
      {
        content = """{"tool": "think", "params": {"thought": "thinking..."}, "reasoning": "planning"}"""
      }
    };
    var client = CreateFakeChatClient(stepResponse);
    var engine = CreateEngine(client);

    var request = new RunTaskRequest { Task = "Long task", WorkspaceRoot = "/workspace" };

    // Act
    var events = new List<TaskEvent>();
    var iteration = 0;
    await foreach (var evt in engine.RunTaskStreamAsync(request, cts.Token))
    {
      events.Add(evt);
      iteration++;
      if (iteration >= 2)
      {
        cts.Cancel();
        break;
      }
    }

    // Assert
    events.Should().HaveCountGreaterThanOrEqualTo(1);
  }

  [Fact]
  public async Task RunTaskStreamAsync_MaxStepsReached_ShouldStop()
  {
    // Arrange - always return think tool to force continuing
    var thinkResponse = new
    {
      message = new
      {
        content = """{"tool": "think", "params": {"thought": "still thinking"}, "reasoning": "need more steps"}"""
      }
    };
    var client = CreateFakeChatClient(thinkResponse);
    var engine = CreateEngine(client);

    var request = new RunTaskRequest { Task = "Task", WorkspaceRoot = "/workspace", MaxSteps = 3 };

    // Act
    var events = new List<TaskEvent>();
    await foreach (var evt in engine.RunTaskStreamAsync(request))
    {
      events.Add(evt);
      if (events.Count > 20) break; // Safety limit
    }

    // Assert - should have stopped after max steps
    // Each step has at least a status event, so we expect limited events
    events.Count.Should().BeLessThanOrEqualTo(10);
  }

  #endregion

  #region Strict Parsing Tests (schema-era: server enforces tool enum + shape)

  [Fact]
  public async Task GenerateNextStepAsync_UnknownTool_PassesThroughForLoopToReject()
  {
    // Schema-constrained servers can't produce unknown tools; if one arrives
    // (non-grammar endpoint), it passes through and the task loop rejects it.
    var ollamaResponse = new { response = """{"tool": "remote_execute", "params": {"command": "test"}, "reasoning": "test"}""" };
    var client = CreateFakeChatClient(ollamaResponse);
    var engine = CreateEngine(client);

    // Act
    var result = await engine.GenerateNextStepAsync("test task", new List<StepResult>(), null);

    // Assert — no silent renaming: strict parse preserves the tool name
    result.Tool.Should().Be("remote_execute");
    result.Params["command"].Should().Be("test");
  }

  [Fact]
  public async Task GenerateNextStepAsync_StripsR1ThinkingBlocks()
  {
    // Arrange — R1-Distill model wraps response in <think>...</think> tags
    var ollamaResponse = new
    {
      response = """
<think>
The user wants to run hostname on ians-r16. I should use the execute tool with agent_id="ians-r16" and command="hostname".
</think>
{"tool": "execute", "params": {"agent_id": "ians-r16", "command": "hostname"}, "reasoning": "run hostname on agent"}
"""
    };
    var client = CreateFakeChatClient(ollamaResponse);
    var engine = CreateEngine(client);

    // Act
    var result = await engine.GenerateNextStepAsync("run hostname on ians-r16", new List<StepResult>(), null);

    // Assert
    result.Tool.Should().Be("execute");
    result.Params.Should().ContainKey("agent_id");
    result.Params["agent_id"].Should().Be("ians-r16");
    result.Params["command"].Should().Be("hostname");
  }

  [Fact]
  public async Task GenerateNextStepAsync_StripsThinkingWithJsonInThought()
  {
    // Arrange — thinking block contains curly braces that would confuse naive IndexOf('{')
    var ollamaResponse = new
    {
      response = """
<think>
Let me think about this. The user asked to list agents. I recall the format is {"tool": "list_agents"}.
I should respond with the proper JSON.
</think>
{"tool": "list_agents", "params": {}, "reasoning": "discover agents"}
"""
    };
    var client = CreateFakeChatClient(ollamaResponse);
    var engine = CreateEngine(client);

    // Act
    var result = await engine.GenerateNextStepAsync("list agents", new List<StepResult>(), null);

    // Assert — should parse the JSON AFTER the think block, not the one inside it
    result.Tool.Should().Be("list_agents");
  }

  [Fact]
  public async Task GenerateNextStepAsync_HandlesMarkdownCodeFence()
  {
    // Arrange — model wraps JSON in markdown code fence
    var ollamaResponse = new
    {
      response = """
Here is the tool call:

```json
{"tool": "execute", "params": {"agent_id": "r730xd", "command": "Get-Process"}, "reasoning": "list processes"}
```
"""
    };
    var client = CreateFakeChatClient(ollamaResponse);
    var engine = CreateEngine(client);

    // Act
    var result = await engine.GenerateNextStepAsync("list processes on r730xd", new List<StepResult>(), null);

    // Assert
    result.Tool.Should().Be("execute");
    result.Params["agent_id"].Should().Be("r730xd");
    result.Params["command"].Should().Be("Get-Process");
  }

  [Fact]
  public async Task GenerateNextStepAsync_HandlesThinkingPlusCodeFence()
  {
    // Arrange — R1 thinking block + markdown code fence (worst case combo)
    var ollamaResponse = new
    {
      response = """
<think>
I need to check the size of directories. The agent is ians-r16. Let me format the execute call properly.
</think>

```json
{"tool": "execute", "params": {"agent_id": "ians-r16", "command": "Get-ChildItem C:\\Code -Directory | Select-Object Name, @{N='SizeGB';E={(Get-ChildItem $_.FullName -Recurse -File | Measure-Object Length -Sum).Sum/1GB}} | Sort-Object SizeGB -Descending | Select-Object -First 5"}, "reasoning": "get directory sizes"}
```
"""
    };
    var client = CreateFakeChatClient(ollamaResponse);
    var engine = CreateEngine(client);

    // Act
    var result = await engine.GenerateNextStepAsync("show largest dirs on ians-r16", new List<StepResult>(), null);

    // Assert
    result.Tool.Should().Be("execute");
    result.Params["agent_id"].Should().Be("ians-r16");
  }

  [Fact]
  public async Task GenerateNextStepAsync_HandlesOrphanedCloseThinkTag()
  {
    // Arrange — model outputs old-format JSON, then </think>, then actual tool JSON
    // This happens when model's thinking gets truncated or malformed
    var ollamaResponse = new
    {
      response = """
{
  "action": "execute",
  "reasoning": "I need to run the command",
  "command": "hostname"
}</think>{
  "tool": "execute",
  "params": {
    "agent_id": "ians-r16",
    "command": "hostname"
  },
  "reasoning": "Running hostname command"
}
"""
    };
    var client = CreateFakeChatClient(ollamaResponse);
    var engine = CreateEngine(client);

    // Act
    var result = await engine.GenerateNextStepAsync("run hostname on ians-r16", new List<StepResult>(), null);

    // Assert — should parse the JSON AFTER the orphaned </think>, not the malformed JSON before it
    result.Tool.Should().Be("execute");
    result.Params["agent_id"].Should().Be("ians-r16");
    result.Params["command"].Should().Be("hostname");
  }

  [Fact]
  public async Task GenerateNextStepAsync_NaturalLanguageOnly_UsesTextAsFallbackAnswer()
  {
    // Arrange — model responds with natural language, no JSON at all
    var ollamaResponse = new
    {
      response = "I'll help you with that. Let me run the hostname command on ians-r16 to check the machine name."
    };
    var client = CreateFakeChatClient(ollamaResponse);
    var engine = CreateEngine(client);

    // Act
    var result = await engine.GenerateNextStepAsync("run hostname", new List<StepResult>(), null);

    // Assert — no JSON falls through to text fallback: complete with the raw text as answer
    result.Tool.Should().Be("complete");
    result.Params.Should().ContainKey("answer");
    result.Params["answer"]!.ToString().Should().Contain("hostname command on ians-r16");
  }

  [Fact]
  public async Task GenerateNextStepAsync_ExtractsThinkingContent()
  {
    // Arrange — R1-Distill model wraps response in <think>...</think> tags
    var ollamaResponse = new
    {
      response = """
<think>
The user wants to calculate 2+2. This is a simple arithmetic operation.
I should respond with the complete tool since this is basic math.
</think>
{"tool": "complete", "params": {"answer": "4"}, "reasoning": "simple math"}
"""
    };
    var client = CreateFakeChatClient(ollamaResponse);
    var engine = CreateEngine(client);

    // Act
    var result = await engine.GenerateNextStepAsync("What is 2+2?", new List<StepResult>(), null);

    // Assert — should extract the thinking content
    result.ThinkingContent.Should().NotBeNullOrEmpty();
    result.ThinkingContent.Should().Contain("simple arithmetic operation");
    result.ThinkingContent.Should().Contain("basic math");
  }

  [Fact]
  public async Task GenerateNextStepAsync_NoThinkingBlock_ReturnsEmptyThinkingContent()
  {
    // Arrange — model responds without thinking block
    var ollamaResponse = new
    {
      response = """{"tool": "list_agents", "params": {}, "reasoning": "discover agents"}"""
    };
    var client = CreateFakeChatClient(ollamaResponse);
    var engine = CreateEngine(client);

    // Act
    var result = await engine.GenerateNextStepAsync("list agents", new List<StepResult>(), null);

    // Assert — no thinking content when no <think> block
    result.ThinkingContent.Should().BeNullOrEmpty();
  }

  [Fact]
  public async Task GenerateNextStepAsync_HandlesMarkdownTextBeforeJson()
  {
    // Arrange — model outputs markdown explanation BEFORE the JSON tool call
    // (non-grammar endpoint); substring extraction tolerates the prefix
    var ollamaResponse = new
    {
      response = """
**Command:** Get-ADPrincipalGroupMembership -Identity $env:USERNAME
**Explanation:** This command lists all AD groups the current user belongs to.
{
  "tool": "execute",
  "params": {"agent_id": "ians-r16", "command": "Get-ADPrincipalGroupMembership -Identity $env:USERNAME | Select-Object Name"},
  "reasoning": "Listing AD group membership"
}
"""
    };
    var client = CreateFakeChatClient(ollamaResponse);
    var engine = CreateEngine(client);

    // Act
    var result = await engine.GenerateNextStepAsync("what ad groups am I in?", new List<StepResult>(), null);

    // Assert — should extract JSON despite markdown prefix
    result.Tool.Should().Be("execute");
    result.Params["command"].Should().Be("Get-ADPrincipalGroupMembership -Identity $env:USERNAME | Select-Object Name");
  }

  #endregion

  #region Helper Methods

  /// <summary>
  /// Minimal IChatClient fake. The responder receives the request messages and
  /// returns the ChatResponse (or throws to simulate transport errors).
  /// </summary>
  private sealed class FakeChatClient(Func<IEnumerable<ChatMessage>, ChatResponse> responder) : IChatClient
  {
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(responder(messages));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
  }

  /// <summary>
  /// Creates a fake IChatClient that returns the given content object as the
  /// assistant message text.
  ///
  /// Call-site conventions (to keep existing tests readable):
  ///   - An anonymous object with a 'response' property: the value of 'response'
  ///     is used verbatim as the assistant message content.
  ///   - An anonymous object with a 'message.content' property: that value is
  ///     used as the assistant message content.
  ///   - Any other shape: the whole object is JSON-serialized and used as the
  ///     assistant message content (e.g. raw `{intent:"task"}` classifier JSON).
  /// </summary>
  private static FakeChatClient CreateFakeChatClient<T>(T content)
  {
    var text = ExtractAssistantContent(content);
    return new FakeChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
  }

  private static FakeChatClient CreateThrowingChatClient(Exception ex)
      => new(_ => throw ex);

  private static string ExtractAssistantContent<T>(T content)
  {
    // Pull 'response' or 'message.content' if present on the anonymous object;
    // otherwise serialize the whole object (used by intent-classification tests
    // that mock the model returning a JSON classification string).
    var type = typeof(T);
    var responseProp = type.GetProperty("response");
    if (responseProp != null)
    {
      var val = responseProp.GetValue(content);
      return val?.ToString() ?? "";
    }

    var messageProp = type.GetProperty("message");
    if (messageProp != null)
    {
      var messageVal = messageProp.GetValue(content);
      if (messageVal != null)
      {
        var contentProp = messageVal.GetType().GetProperty("content");
        if (contentProp != null)
        {
          var inner = contentProp.GetValue(messageVal);
          return inner?.ToString() ?? "";
        }
      }
    }

    return JsonSerializer.Serialize(content);
  }

  #endregion
}
