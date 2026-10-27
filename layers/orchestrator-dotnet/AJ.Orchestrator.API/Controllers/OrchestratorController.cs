using Microsoft.AspNetCore.Mvc;
using AJ.Orchestrator.Abstractions.Models.Agents;
using AJ.Orchestrator.Abstractions.Models.Classification;
using AJ.Orchestrator.Abstractions.Models.Infrastructure;
using AJ.Orchestrator.Abstractions.Models.Session;
using AJ.Orchestrator.Abstractions.Models.Tasks;
using AJ.Orchestrator.Abstractions.Models.Workspace;
using AJ.Orchestrator.Abstractions.Services;
using AJ.Shared.Contracts;
using System.Text.Json;

namespace AJ.Orchestrator.API.Controllers;

[ApiController]
[Route("api/orchestrate")]
public class OrchestratorController : ControllerBase
{
  private readonly IReasoningEngine _reasoning;
  private readonly ITaskPlanner _planner;
  private readonly ILogger<OrchestratorController> _logger;

  public OrchestratorController(
      IReasoningEngine reasoning,
      ITaskPlanner planner,
      ILogger<OrchestratorController> logger)
  {
    _reasoning = reasoning;
    _planner = planner;
    _logger = logger;
  }

  /// <summary>
  /// Set the active workspace context for the current user.
  /// </summary>
  [HttpPost("set-workspace")]
  public async Task<ActionResult<SetWorkspaceResponse>> SetWorkspace(
      [FromBody] SetWorkspaceRequest request)
  {
    var userId = GetUserId();

    var result = await _planner.SetWorkspaceAsync(request.Cwd, userId);

    return Ok(new SetWorkspaceResponse
    {
      Success = result != null,
      WorkspacePath = result?.Cwd,
      SessionId = userId,
      Error = result == null ? "Failed to set workspace" : null
    });
  }

  /// <summary>
  /// Classify user intent via the LLM.
  /// </summary>
  [HttpPost("classify")]
  public async Task<ActionResult<ClassifyResponse>> Classify(
      [FromBody] ClassifyRequest request)
  {
    var result = await _reasoning.ClassifyIntentAsync(request.Text, request.Context);

    return Ok(result);
  }

  /// <summary>
  /// Extract memory-worthy facts from user text (replaces the pragmatics service).
  /// </summary>
  [HttpPost("extract-facts")]
  public async Task<ActionResult<ExtractFactsResponse>> ExtractFacts(
      [FromBody] ExtractFactsRequest request)
  {
    var result = await _reasoning.ExtractFactsAsync(request.Text);

    return Ok(result);
  }

  /// <summary>
  /// Get the next step in task execution based on current state.
  /// </summary>
  [HttpPost("next-step")]
  public async Task<ActionResult<NextStepResponse>> NextStep(
      [FromBody] NextStepRequest request)
  {
    var result = await _reasoning.GenerateNextStepAsync(
        request.Task,
        request.History ?? new List<StepResult>(),
        request.WorkspaceContext);

    return Ok(result);
  }

  /// <summary>
  /// Run a complete task with streaming output (SSE).
  /// </summary>
  [HttpPost("run-task")]
  public async Task RunTask([FromBody] RunTaskRequest request)
  {
    Response.ContentType = "text/event-stream";
    Response.Headers.Append("Cache-Control", "no-cache");
    Response.Headers.Append("X-Accel-Buffering", "no");

    var ct = HttpContext.RequestAborted;

    try
    {
      await foreach (var evt in _reasoning.RunTaskStreamAsync(request, ct))
      {
        var json = JsonSerializer.Serialize(evt, new JsonSerializerOptions
        {
          PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        await Response.WriteAsync($"data: {json}\n\n", ct);
        await Response.Body.FlushAsync(ct);
      }

      await Response.WriteAsync("data: [DONE]\n\n", ct);
      await Response.Body.FlushAsync(ct);
    }
    catch (OperationCanceledException)
    {
      _logger.LogInformation("Task stream cancelled by client");
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error during task streaming");

      var error = new TaskEvent
      {
        Type = "error",
        Error = ex.Message
      };
      var errorJson = JsonSerializer.Serialize(error, new JsonSerializerOptions
      {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
      });

      await Response.WriteAsync($"data: {errorJson}\n\n", ct);
      await Response.Body.FlushAsync(ct);
    }
  }

  /// <summary>
  /// Reset session state for the current user.
  /// </summary>
  [HttpPost("reset-state")]
  public ActionResult<ResetStateResponse> ResetState([FromBody] ResetStateRequest? request)
  {
    // TODO: Implement session state reset via SessionStateManager
    return Ok(new ResetStateResponse
    {
      Success = true,
      Message = "Session state reset"
    });
  }

  /// <summary>
  /// Health check endpoint.
  /// </summary>
  [HttpGet("health")]
  public ActionResult<HealthResponse> Health()
  {
    return Ok(new HealthResponse
    {
      Status = "healthy",
      Service = "orchestrator-dotnet",
      Version = "2.0.0"
    });
  }

  /// <summary>
  /// Get available agents from the FunnelCloud mesh.
  /// </summary>
  [HttpGet("agents")]
  public async Task<ActionResult<AgentsResponse>> GetAgents(
      [FromServices] IAgentDiscovery discovery)
  {
    var agents = await discovery.DiscoverAgentsAsync();

    return Ok(new AgentsResponse
    {
      Agents = agents.Select(a => new AgentInfo
      {
        AgentId = a.AgentId,
        Hostname = a.Hostname,
        Platform = a.Platform,
        Capabilities = a.Capabilities.ToList(),
        GrpcPort = a.GrpcPort,
        IpAddress = a.IpAddress ?? ""
      }).ToList()
    });
  }

  private string GetUserId()
  {
    // Extract user ID from header, auth token, or use default
    if (Request.Headers.TryGetValue("X-User-Id", out var userId))
      return userId.ToString();

    if (Request.Headers.TryGetValue("Authorization", out var auth))
    {
      // TODO: Decode JWT and extract user ID
      return "authenticated-user";
    }

    return "default-user";
  }
}
