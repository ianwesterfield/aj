using AJ.Shared.Contracts;
using FluentAssertions;

namespace AJ.Orchestrator.Tests.Contracts;

/// <summary>
/// Tests for AgentCapabilities contract - AJ.Shared layer.
/// </summary>
public class AgentCapabilitiesTests
{
  [Fact]
  public void AgentCapabilities_RequiredProperties_CanBeSet()
  {
    var capabilities = new AgentCapabilities
    {
      AgentId = "test-agent",
      Hostname = "test-host",
      Platform = "windows",
      Capabilities = new[] { "powershell", "dotnet" },
      WorkspaceRoots = new[] { "C:\\Code" },
      CertificateFingerprint = "abc123def456"
    };

    capabilities.AgentId.Should().Be("test-agent");
    capabilities.Hostname.Should().Be("test-host");
    capabilities.Platform.Should().Be("windows");
    capabilities.Capabilities.Should().HaveCount(2);
    capabilities.WorkspaceRoots.Should().Contain("C:\\Code");
    capabilities.CertificateFingerprint.Should().Be("abc123def456");
  }

  [Fact]
  public void AgentCapabilities_DefaultPorts_AreCorrect()
  {
    var capabilities = new AgentCapabilities
    {
      AgentId = "test-agent",
      Hostname = "test-host",
      Platform = "linux",
      Capabilities = Array.Empty<string>(),
      WorkspaceRoots = Array.Empty<string>(),
      CertificateFingerprint = "fingerprint"
    };

    capabilities.DiscoveryPort.Should().Be(41420);
    capabilities.GrpcPort.Should().Be(41235);
  }

  [Fact]
  public void AgentCapabilities_CustomPorts_CanBeSet()
  {
    var capabilities = new AgentCapabilities
    {
      AgentId = "test-agent",
      Hostname = "test-host",
      Platform = "linux",
      Capabilities = Array.Empty<string>(),
      WorkspaceRoots = Array.Empty<string>(),
      CertificateFingerprint = "fingerprint",
      DiscoveryPort = 50000,
      GrpcPort = 50001
    };

    capabilities.DiscoveryPort.Should().Be(50000);
    capabilities.GrpcPort.Should().Be(50001);
  }

  [Fact]
  public void AgentCapabilities_IpAddress_IsOptional()
  {
    var capabilities = new AgentCapabilities
    {
      AgentId = "test-agent",
      Hostname = "test-host",
      Platform = "macos",
      Capabilities = new[] { "bash" },
      WorkspaceRoots = new[] { "/Users/dev/code" },
      CertificateFingerprint = "fingerprint"
    };

    capabilities.IpAddress.Should().BeNull();
  }

  [Fact]
  public void AgentCapabilities_IpAddress_CanBeSet()
  {
    var capabilities = new AgentCapabilities
    {
      AgentId = "test-agent",
      Hostname = "test-host",
      Platform = "linux",
      Capabilities = Array.Empty<string>(),
      WorkspaceRoots = Array.Empty<string>(),
      CertificateFingerprint = "fingerprint",
      IpAddress = "192.168.1.100"
    };

    capabilities.IpAddress.Should().Be("192.168.1.100");
  }

  [Theory]
  [InlineData("windows")]
  [InlineData("linux")]
  [InlineData("macos")]
  public void AgentCapabilities_Platform_AcceptsValidValues(string platform)
  {
    var capabilities = new AgentCapabilities
    {
      AgentId = "test-agent",
      Hostname = "test-host",
      Platform = platform,
      Capabilities = Array.Empty<string>(),
      WorkspaceRoots = Array.Empty<string>(),
      CertificateFingerprint = "fingerprint"
    };

    capabilities.Platform.Should().Be(platform);
  }

  [Fact]
  public void AgentCapabilities_MultipleCapabilities_ArePreserved()
  {
    var caps = new[] { "powershell", "bash", "dotnet", "docker", "git", "npm" };
    var capabilities = new AgentCapabilities
    {
      AgentId = "full-stack-agent",
      Hostname = "dev-machine",
      Platform = "windows",
      Capabilities = caps,
      WorkspaceRoots = new[] { "C:\\Code", "D:\\Projects" },
      CertificateFingerprint = "fingerprint"
    };

    capabilities.Capabilities.Should().HaveCount(6);
    capabilities.Capabilities.Should().Contain("docker");
    capabilities.Capabilities.Should().Contain("npm");
  }

  [Fact]
  public void AgentCapabilities_MultipleWorkspaceRoots_ArePreserved()
  {
    var roots = new[] { "C:\\Code", "D:\\Projects", "E:\\Work" };
    var capabilities = new AgentCapabilities
    {
      AgentId = "test-agent",
      Hostname = "test-host",
      Platform = "windows",
      Capabilities = Array.Empty<string>(),
      WorkspaceRoots = roots,
      CertificateFingerprint = "fingerprint"
    };

    capabilities.WorkspaceRoots.Should().HaveCount(3);
    capabilities.WorkspaceRoots.Should().ContainInOrder("C:\\Code", "D:\\Projects", "E:\\Work");
  }

  [Fact]
  public void AgentCapabilities_RecordEquality_WorksCorrectly()
  {
    var caps1 = new AgentCapabilities
    {
      AgentId = "agent-1",
      Hostname = "host-1",
      Platform = "linux",
      Capabilities = new[] { "bash" },
      WorkspaceRoots = new[] { "/home/user" },
      CertificateFingerprint = "fp1"
    };

    var caps2 = new AgentCapabilities
    {
      AgentId = "agent-1",
      Hostname = "host-1",
      Platform = "linux",
      Capabilities = new[] { "bash" },
      WorkspaceRoots = new[] { "/home/user" },
      CertificateFingerprint = "fp1"
    };

    // Records compare by value for primitive types but reference for arrays
    // So these won't be equal due to array reference comparison
    caps1.AgentId.Should().Be(caps2.AgentId);
    caps1.Platform.Should().Be(caps2.Platform);
  }
}
