using AJ.Orchestrator.Abstractions.Models.Agents;
using AJ.Orchestrator.Abstractions.Models.Classification;
using AJ.Orchestrator.Abstractions.Models.Infrastructure;
using AJ.Orchestrator.Abstractions.Models.Session;
using AJ.Orchestrator.Abstractions.Models.Tasks;
using AJ.Orchestrator.Abstractions.Models.Workspace;
using AJ.Orchestrator.Abstractions.Services;
using AJ.Orchestrator.API.Controllers;
using AJ.Shared.Contracts;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace AJ.Orchestrator.Tests.Controllers;

/// <summary>
/// Tests for OrchestratorController - API layer tests.
/// </summary>
public class OrchestratorControllerTests
{
  private readonly Mock<IReasoningEngine> _reasoningMock;
  private readonly Mock<ITaskPlanner> _plannerMock;
  private readonly Mock<ILogger<OrchestratorController>> _loggerMock;
  private readonly OrchestratorController _controller;

  public OrchestratorControllerTests()
  {
    _reasoningMock = new Mock<IReasoningEngine>();
    _plannerMock = new Mock<ITaskPlanner>();
    _loggerMock = new Mock<ILogger<OrchestratorController>>();

    _controller = new OrchestratorController(
        _reasoningMock.Object,
        _plannerMock.Object,
        _loggerMock.Object);

    // Setup default HttpContext
    _controller.ControllerContext = new ControllerContext
    {
      HttpContext = new DefaultHttpContext()
    };
  }

  #region Health Tests

  [Fact]
  public void Health_ReturnsHealthyStatus()
  {
    var result = _controller.Health();

    var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
    var response = okResult.Value.Should().BeOfType<HealthResponse>().Subject;

    response.Status.Should().Be("healthy");
    response.Service.Should().Be("orchestrator-dotnet");
    response.Version.Should().NotBeNullOrEmpty();
  }

  #endregion

  #region Classify Tests

  [Fact]
  public async Task Classify_ReturnsClassificationResult()
  {
    var request = new ClassifyRequest { Text = "deploy the application" };
    var expected = new ClassifyResponse("agentic", 0.95, "Contains deployment action");

    _reasoningMock
        .Setup(r => r.ClassifyIntentAsync(request.Text, request.Context))
        .ReturnsAsync(expected);

    var result = await _controller.Classify(request);

    var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
    var response = okResult.Value.Should().BeOfType<ClassifyResponse>().Subject;

    response.Intent.Should().Be("agentic");
    response.Confidence.Should().Be(0.95);
  }

  [Fact]
  public async Task Classify_CasualIntent_ReturnsLowConfidence()
  {
    var request = new ClassifyRequest { Text = "hello there" };
    var expected = new ClassifyResponse("casual", 0.3, "Greeting detected");

    _reasoningMock
        .Setup(r => r.ClassifyIntentAsync(request.Text, request.Context))
        .ReturnsAsync(expected);

    var result = await _controller.Classify(request);

    var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
    var response = okResult.Value.Should().BeOfType<ClassifyResponse>().Subject;

    response.Intent.Should().Be("casual");
    response.Confidence.Should().BeLessThan(0.5);
  }

  #endregion

  #region SetWorkspace Tests

  [Fact]
  public async Task SetWorkspace_ValidPath_ReturnsSuccess()
  {
    var request = new SetWorkspaceRequest { Cwd = "/home/user/code" };
    var workspace = new WorkspaceContext
    {
      Cwd = request.Cwd,
      WorkspaceRoot = request.Cwd,
      AvailablePaths = new List<string> { "src", "tests" }
    };

    _plannerMock
        .Setup(p => p.SetWorkspaceAsync(request.Cwd, It.IsAny<string>()))
        .ReturnsAsync(workspace);

    var result = await _controller.SetWorkspace(request);

    var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
    var response = okResult.Value.Should().BeOfType<SetWorkspaceResponse>().Subject;

    response.Success.Should().BeTrue();
    response.WorkspacePath.Should().Be(request.Cwd);
  }

  [Fact]
  public async Task SetWorkspace_InvalidPath_ReturnsError()
  {
    var request = new SetWorkspaceRequest { Cwd = "/nonexistent/path" };

    _plannerMock.Setup(p => p.SetWorkspaceAsync(request.Cwd, It.IsAny<string>())).ReturnsAsync((WorkspaceContext)null!);

    var result = await _controller.SetWorkspace(request);

    var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
    var response = okResult.Value.Should().BeOfType<SetWorkspaceResponse>().Subject;

    response.Success.Should().BeFalse();
    response.Error.Should().NotBeNullOrEmpty();
  }

  #endregion

  #region NextStep Tests

  [Fact]
  public async Task NextStep_ReturnsNextStepResponse()
  {
    var request = new NextStepRequest { Task = "list all files", History = new List<StepResult>() };

    var expected = new NextStepResponse(
        "execute",
        new Dictionary<string, object?> { ["command"] = "ls -la" },
        null,
        "Listing files in current directory",
        false);

    _reasoningMock
        .Setup(r => r.GenerateNextStepAsync(
            request.Task,
            It.IsAny<List<StepResult>>(),
            It.IsAny<WorkspaceContext?>(),
            It.IsAny<List<Dictionary<string, object?>>?>()))
        .ReturnsAsync(expected);

    var result = await _controller.NextStep(request);

    var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
    var response = okResult.Value.Should().BeOfType<NextStepResponse>().Subject;

    response.Tool.Should().Be("execute");
    response.Params.Should().ContainKey("command");
  }

  [Fact]
  public async Task NextStep_WithHistory_UsesHistory()
  {
    var history = new List<StepResult>
    {
      new StepResult
      {
        StepId = "step-1",
        Tool = "execute",
        Status = StepStatus.Success,
        Output = "file1.txt\nfile2.txt"
      }
    };

    var request = new NextStepRequest { Task = "count the files", History = history };

    var expected = new NextStepResponse(
        "complete",
        new Dictionary<string, object?> { ["answer"] = "There are 2 files" },
        null,
        "Task complete",
        false);

    _reasoningMock
        .Setup(r => r.GenerateNextStepAsync(
            request.Task,
            It.Is<List<StepResult>>(h => h.Count == 1),
            It.IsAny<WorkspaceContext?>(),
            It.IsAny<List<Dictionary<string, object?>>?>()))
        .ReturnsAsync(expected);

    var result = await _controller.NextStep(request);

    var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
    var response = okResult.Value.Should().BeOfType<NextStepResponse>().Subject;

    response.Tool.Should().Be("complete");
  }

  #endregion

  #region ResetState Tests

  [Fact]
  public void ResetState_ReturnsSuccess()
  {
    var result = _controller.ResetState(null);

    var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
    var response = okResult.Value.Should().BeOfType<ResetStateResponse>().Subject;

    response.Success.Should().BeTrue();
    response.Message.Should().NotBeNullOrEmpty();
  }

  [Fact]
  public void ResetState_WithRequest_ReturnsSuccess()
  {
    var request = new ResetStateRequest { SessionId = "test-session" };

    var result = _controller.ResetState(request);

    var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
    var response = okResult.Value.Should().BeOfType<ResetStateResponse>().Subject;

    response.Success.Should().BeTrue();
  }

  #endregion

  #region GetAgents Tests

  [Fact]
  public async Task GetAgents_ReturnsAgentList()
  {
    var discoveryMock = new Mock<IAgentDiscovery>();
    var agents = new List<AgentCapabilities>
    {
      new AgentCapabilities
      {
        AgentId = "agent-1",
        Hostname = "workstation-1",
        Platform = "windows",
        Capabilities = new[] { "powershell", "dotnet" },
        WorkspaceRoots = new[] { "C:\\Code" },
        CertificateFingerprint = "abc123",
        GrpcPort = 41235,
        IpAddress = "192.168.1.100"
      }
    };

    discoveryMock
        .Setup(d => d.DiscoverAgentsAsync(It.IsAny<CancellationToken>()))
        .ReturnsAsync(agents);

    var result = await _controller.GetAgents(discoveryMock.Object);

    var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
    var response = okResult.Value.Should().BeOfType<AgentsResponse>().Subject;

    response.Agents.Should().HaveCount(1);
    response.Agents[0].AgentId.Should().Be("agent-1");
    response.Agents[0].Hostname.Should().Be("workstation-1");
  }

  [Fact]
  public async Task GetAgents_NoAgents_ReturnsEmptyList()
  {
    var discoveryMock = new Mock<IAgentDiscovery>();
    discoveryMock
        .Setup(d => d.DiscoverAgentsAsync(It.IsAny<CancellationToken>()))
        .ReturnsAsync(new List<AgentCapabilities>());

    var result = await _controller.GetAgents(discoveryMock.Object);

    var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
    var response = okResult.Value.Should().BeOfType<AgentsResponse>().Subject;

    response.Agents.Should().BeEmpty();
  }

  #endregion

  #region User ID Extraction Tests

  [Fact]
  public async Task SetWorkspace_WithUserIdHeader_UsesHeaderValue()
  {
    var request = new SetWorkspaceRequest { Cwd = "/home/user/code" };
    _controller.ControllerContext.HttpContext.Request.Headers["X-User-Id"] = "header-user-123";

    var workspace = new WorkspaceContext { Cwd = request.Cwd, WorkspaceRoot = request.Cwd };
    _plannerMock
        .Setup(p => p.SetWorkspaceAsync(request.Cwd, "header-user-123"))
        .ReturnsAsync(workspace);

    var result = await _controller.SetWorkspace(request);

    _plannerMock.Verify(p => p.SetWorkspaceAsync(request.Cwd, "header-user-123"), Times.Once);
  }

  [Fact]
  public async Task SetWorkspace_WithAuthHeader_UsesAuthenticatedUser()
  {
    var request = new SetWorkspaceRequest { Cwd = "/home/user/code" };
    _controller.ControllerContext.HttpContext.Request.Headers["Authorization"] = "Bearer token123";

    var workspace = new WorkspaceContext { Cwd = request.Cwd, WorkspaceRoot = request.Cwd };
    _plannerMock
        .Setup(p => p.SetWorkspaceAsync(request.Cwd, "authenticated-user"))
        .ReturnsAsync(workspace);

    var result = await _controller.SetWorkspace(request);

    _plannerMock.Verify(p => p.SetWorkspaceAsync(request.Cwd, "authenticated-user"), Times.Once);
  }

  [Fact]
  public async Task SetWorkspace_NoHeaders_UsesDefaultUser()
  {
    var request = new SetWorkspaceRequest { Cwd = "/home/user/code" };

    var workspace = new WorkspaceContext { Cwd = request.Cwd, WorkspaceRoot = request.Cwd };
    _plannerMock
        .Setup(p => p.SetWorkspaceAsync(request.Cwd, "default-user"))
        .ReturnsAsync(workspace);

    var result = await _controller.SetWorkspace(request);

    _plannerMock.Verify(p => p.SetWorkspaceAsync(request.Cwd, "default-user"), Times.Once);
  }

  #endregion
}
