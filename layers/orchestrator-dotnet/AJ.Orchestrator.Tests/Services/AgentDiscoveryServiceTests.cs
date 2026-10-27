using AJ.Orchestrator.Domain.Services;
using AJ.Shared.Contracts;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text.Json;

namespace AJ.Orchestrator.Tests.Services;

/// <summary>
/// Tests for AgentDiscoveryService.
/// </summary>
public class AgentDiscoveryServiceTests
{
  private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
  private readonly Mock<ILogger<AgentDiscoveryService>> _loggerMock;

  public AgentDiscoveryServiceTests()
  {
    _httpClientFactoryMock = new Mock<IHttpClientFactory>();
    _loggerMock = new Mock<ILogger<AgentDiscoveryService>>();
  }

  private AgentDiscoveryService CreateService(HttpClient? client = null, string? gossipSeedHost = null)
  {
    if (client != null)
    {
      _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>()))
          .Returns(client);
    }

    var configValues = new Dictionary<string, string?>();
    if (gossipSeedHost != null)
    {
      configValues["FunnelCloud:GossipSeedHost"] = gossipSeedHost;
    }
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(configValues)
        .Build();

    return new AgentDiscoveryService(_httpClientFactoryMock.Object, configuration, _loggerMock.Object);
  }

  #region Constructor Tests

  [Fact]
  public void Constructor_ShouldStartWithNoAgents()
  {
    // Arrange & Act
    var service = CreateService();

    // Assert
    var agents = service.GetAllAgents();
    agents.Should().BeEmpty();
  }

  #endregion

  #region GetAgent Tests

  [Fact]
  public void GetAgent_NonExistentAgent_ShouldReturnNull()
  {
    // Arrange
    var service = CreateService();

    // Act
    var agent = service.GetAgent("non-existent-agent");

    // Assert
    agent.Should().BeNull();
  }

  #endregion

  #region GetAllAgents Tests

  [Fact]
  public void GetAllAgents_InitialState_ShouldReturnEmpty()
  {
    // Arrange
    var service = CreateService();

    // Act
    var agents = service.GetAllAgents();

    // Assert
    agents.Should().BeEmpty();
  }

  #endregion

  #region DiscoverAgentsAsync Tests

  [Fact]
  public async Task DiscoverAgentsAsync_SuccessfulResponse_ShouldAddPeers()
  {
    // Arrange
    var response = new
    {
      agents = new[]
      {
        new
        {
          agentId = "peer-1",
          hostname = "peer-host-1",
          platform = "linux",
          capabilities = new[] { "bash", "python" },
          workspaceRoots = new[] { "/home/user" },
          certificateFingerprint = "abc123",
          ipAddress = "192.168.1.10"
        },
        new
        {
          agentId = "peer-2",
          hostname = "peer-host-2",
          platform = "windows",
          capabilities = new[] { "powershell" },
          workspaceRoots = new[] { "C:\\Users" },
          certificateFingerprint = "def456",
          ipAddress = "192.168.1.11"
        }
      },
      count = 2,
      discoveredBy = "local",
      freshCount = 2,
      cachedCount = 0
    };

    var httpClient = CreateMockHttpClient(HttpStatusCode.OK, response);
    var service = CreateService(httpClient);

    // Act
    var result = await service.DiscoverAgentsAsync();

    // Assert
    result.Should().HaveCount(2);
    result.Should().Contain(a => a.AgentId == "peer-1");
    result.Should().Contain(a => a.AgentId == "peer-2");
  }

  [Fact]
  public async Task DiscoverAgentsAsync_FailedResponse_ShouldReturnCachedAgents()
  {
    // Arrange
    var httpClient = CreateMockHttpClient<object>(HttpStatusCode.InternalServerError, null);
    var service = CreateService(httpClient);

    // Act
    var result = await service.DiscoverAgentsAsync();

    // Assert
    result.Should().BeEmpty();
  }

  [Fact]
  public async Task DiscoverAgentsAsync_Timeout_ShouldReturnCachedAgents()
  {
    // Arrange
    var handlerMock = new Mock<HttpMessageHandler>();
    handlerMock.Protected()
        .Setup<Task<HttpResponseMessage>>("SendAsync",
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>())
        .ThrowsAsync(new TaskCanceledException("Request timed out"));

    var httpClient = new HttpClient(handlerMock.Object);
    var service = CreateService(httpClient);

    // Act
    var result = await service.DiscoverAgentsAsync();

    // Assert
    result.Should().BeEmpty();
  }

  [Fact]
  public async Task DiscoverAgentsAsync_AddsPeers_ShouldUpdateLocalCache()
  {
    // Arrange
    var response = new
    {
      agents = new[]
      {
        new
        {
          agentId = "peer-1",
          hostname = "peer-hostname",
          platform = "linux",
          capabilities = new[] { "bash", "python" },
          workspaceRoots = Array.Empty<string>(),
          certificateFingerprint = "cert123",
          ipAddress = "192.168.1.10"
        }
      },
      count = 1,
      discoveredBy = "local",
      freshCount = 1,
      cachedCount = 0
    };

    var httpClient = CreateMockHttpClient(HttpStatusCode.OK, response);
    var service = CreateService(httpClient);

    // Pre-check: no agents initially
    var beforeDiscovery = service.GetAllAgents();
    beforeDiscovery.Should().BeEmpty();

    // Act
    await service.DiscoverAgentsAsync();
    var afterDiscovery = service.GetAgent("peer-1");

    // Assert
    afterDiscovery.Should().NotBeNull();
    afterDiscovery!.Hostname.Should().Be("peer-hostname");
    afterDiscovery.Capabilities.Should().HaveCount(2);
  }

  [Fact]
  public async Task DiscoverAgentsAsync_WithGossipSeed_ShouldQuerySeedHost()
  {
    // Arrange
    var response = new
    {
      agents = new[]
      {
        new
        {
          agentId = "seed-peer",
          hostname = "seed-host",
          platform = "windows",
          capabilities = new[] { "powershell" },
          workspaceRoots = Array.Empty<string>(),
          certificateFingerprint = "cert",
          ipAddress = "192.168.10.100"
        }
      },
      count = 1,
      discoveredBy = "seed",
      freshCount = 1,
      cachedCount = 0
    };

    var httpClient = CreateMockHttpClient(HttpStatusCode.OK, response);
    var service = CreateService(httpClient, gossipSeedHost: "192.168.10.166");

    // Act
    var result = await service.DiscoverAgentsAsync();

    // Assert — seed peer should be discovered
    result.Should().Contain(a => a.AgentId == "seed-peer");
  }

  #endregion

  #region RefreshAsync Tests

  [Fact]
  public async Task RefreshAsync_ShouldTriggerDiscovery()
  {
    // Arrange
    var response = new
    {
      agents = new[]
      {
        new
        {
          agentId = "new-peer",
          hostname = "new-host",
          platform = "linux",
          capabilities = Array.Empty<string>(),
          workspaceRoots = Array.Empty<string>(),
          certificateFingerprint = "cert",
          ipAddress = "10.0.0.1"
        }
      },
      count = 1,
      discoveredBy = "local",
      freshCount = 1,
      cachedCount = 0
    };

    var httpClient = CreateMockHttpClient(HttpStatusCode.OK, response);
    var service = CreateService(httpClient);

    // Act
    await service.RefreshAsync();

    // Assert
    var agents = service.GetAllAgents();
    agents.Should().HaveCount(1);
    agents.Should().Contain(a => a.AgentId == "new-peer");
  }

  [Fact]
  public async Task RefreshAsync_WithCancellationToken_ShouldRespectIt()
  {
    // Arrange
    var cts = new CancellationTokenSource();
    cts.Cancel();

    var response = new { agents = Array.Empty<object>(), count = 0, discoveredBy = "local", freshCount = 0, cachedCount = 0 };
    var httpClient = CreateMockHttpClient(HttpStatusCode.OK, response);
    var service = CreateService(httpClient);

    // Act & Assert - should not throw even if cancelled
    await service.RefreshAsync(cts.Token);
  }

  #endregion

  #region Helper Methods

  private static HttpClient CreateMockHttpClient<T>(HttpStatusCode statusCode, T? content)
  {
    var json = content != null ? JsonSerializer.Serialize(content) : "";
    var handlerMock = new Mock<HttpMessageHandler>();
    handlerMock.Protected()
        .Setup<Task<HttpResponseMessage>>("SendAsync",
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>())
        .ReturnsAsync(() => new HttpResponseMessage
        {
          StatusCode = statusCode,
          Content = content != null
                ? new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                : null
        });

    return new HttpClient(handlerMock.Object) { BaseAddress = new Uri("http://fake-agent:41421/") };
  }

  #endregion
}
