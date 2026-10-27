using AJ.Orchestrator.Abstractions.Models.Agents;
using FluentAssertions;

namespace AJ.Orchestrator.Tests.Models;

/// <summary>
/// Tests for agent-related models.
/// </summary>
public class AgentModelsTests
{
  [Fact]
  public void AgentInfo_ShouldStoreAllProperties()
  {
    var agent = new AgentInfo
    {
      AgentId = "agent-123",
      Hostname = "workstation-1",
      Platform = "windows",
      Capabilities = new List<string> { "powershell", "dotnet", "git" },
      GrpcPort = 50051,
      IpAddress = "192.168.1.100"
    };

    agent.AgentId.Should().Be("agent-123");
    agent.Hostname.Should().Be("workstation-1");
    agent.Platform.Should().Be("windows");
    agent.Capabilities.Should().HaveCount(3);
    agent.Capabilities.Should().Contain("powershell");
    agent.GrpcPort.Should().Be(50051);
    agent.IpAddress.Should().Be("192.168.1.100");
  }

  [Fact]
  public void AgentInfo_DifferentPlatforms_ShouldBeValid()
  {
    var windowsAgent = new AgentInfo
    {
      AgentId = "win-1",
      Hostname = "win-host",
      Platform = "windows",
      Capabilities = new List<string> { "powershell" },
      GrpcPort = 50051,
      IpAddress = "10.0.0.1"
    };

    var linuxAgent = new AgentInfo
    {
      AgentId = "linux-1",
      Hostname = "linux-host",
      Platform = "linux",
      Capabilities = new List<string> { "bash", "python" },
      GrpcPort = 50052,
      IpAddress = "10.0.0.2"
    };

    var macAgent = new AgentInfo
    {
      AgentId = "mac-1",
      Hostname = "mac-host",
      Platform = "macos",
      Capabilities = new List<string> { "zsh", "python", "node" },
      GrpcPort = 50053,
      IpAddress = "10.0.0.3"
    };

    windowsAgent.Platform.Should().Be("windows");
    linuxAgent.Platform.Should().Be("linux");
    macAgent.Platform.Should().Be("macos");
  }

  [Fact]
  public void AgentsResponse_EmptyList_ShouldBeValid()
  {
    var response = new AgentsResponse
    {
      Agents = new List<AgentInfo>()
    };

    response.Agents.Should().BeEmpty();
  }

  [Fact]
  public void AgentsResponse_WithMultipleAgents_ShouldContainAll()
  {
    var agents = new List<AgentInfo>
        {
            new()
            {
                AgentId = "agent-1",
                Hostname = "host-1",
                Platform = "windows",
                Capabilities = new List<string> { "powershell" },
                GrpcPort = 50051,
                IpAddress = "10.0.0.1"
            },
            new()
            {
                AgentId = "agent-2",
                Hostname = "host-2",
                Platform = "linux",
                Capabilities = new List<string> { "bash" },
                GrpcPort = 50052,
                IpAddress = "10.0.0.2"
            }
        };

    var response = new AgentsResponse { Agents = agents };

    response.Agents.Should().HaveCount(2);
    response.Agents.Should().Contain(a => a.AgentId == "agent-1");
    response.Agents.Should().Contain(a => a.AgentId == "agent-2");
  }

  [Fact]
  public void AgentInfo_WithEmptyCapabilities_ShouldBeValid()
  {
    var agent = new AgentInfo
    {
      AgentId = "minimal-agent",
      Hostname = "host",
      Platform = "linux",
      Capabilities = new List<string>(),
      GrpcPort = 50051,
      IpAddress = "127.0.0.1"
    };

    agent.Capabilities.Should().BeEmpty();
  }

  [Fact]
  public void AgentInfo_LocalhostAgent_ShouldHaveLoopbackIp()
  {
    var localAgent = new AgentInfo
    {
      AgentId = "localhost",
      Hostname = "localhost",
      Platform = "windows",
      Capabilities = new List<string> { "powershell", "dotnet" },
      GrpcPort = 50051,
      IpAddress = "127.0.0.1"
    };

    localAgent.IpAddress.Should().Be("127.0.0.1");
    localAgent.AgentId.Should().Be("localhost");
  }
}
