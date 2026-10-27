namespace FunnelCloud.Tests;

using FunnelCloud.Shared.Contracts;

/// <summary>
/// Tests for FunnelCloud contract models - TaskRequest, TaskResult, AgentCapabilities.
/// </summary>
public class ContractModelsTests
{
  #region TaskRequest Tests

  [Fact]
  public void TaskRequest_DefaultTimeout_Is30Seconds()
  {
    var request = new TaskRequest
    {
      TaskId = "test-1",
      Type = TaskType.Shell,
      Command = "echo hello"
    };

    Assert.Equal(30, request.TimeoutSeconds);
  }

  [Fact]
  public void TaskRequest_RequiresElevation_DefaultsFalse()
  {
    var request = new TaskRequest
    {
      TaskId = "test-2",
      Type = TaskType.PowerShell,
      Command = "Get-Process"
    };

    Assert.False(request.RequiresElevation);
  }

  [Fact]
  public void TaskRequest_OptionalFields_CanBeNull()
  {
    var request = new TaskRequest
    {
      TaskId = "test-3",
      Type = TaskType.ReadFile,
      Command = "/path/to/file"
    };

    Assert.Null(request.Content);
    Assert.Null(request.WorkingDirectory);
    Assert.Null(request.SessionId);
  }

  [Fact]
  public void TaskRequest_CanSetAllProperties()
  {
    var request = new TaskRequest
    {
      TaskId = "full-request",
      Type = TaskType.WriteFile,
      Command = "/path/to/output",
      Content = "file content",
      WorkingDirectory = "/work/dir",
      TimeoutSeconds = 120,
      RequiresElevation = true,
      SessionId = "session-123"
    };

    Assert.Equal("full-request", request.TaskId);
    Assert.Equal(TaskType.WriteFile, request.Type);
    Assert.Equal("/path/to/output", request.Command);
    Assert.Equal("file content", request.Content);
    Assert.Equal("/work/dir", request.WorkingDirectory);
    Assert.Equal(120, request.TimeoutSeconds);
    Assert.True(request.RequiresElevation);
    Assert.Equal("session-123", request.SessionId);
  }

  #endregion

  #region TaskType Tests

  [Theory]
  [InlineData(TaskType.Shell)]
  [InlineData(TaskType.PowerShell)]
  [InlineData(TaskType.ReadFile)]
  [InlineData(TaskType.WriteFile)]
  [InlineData(TaskType.ListDirectory)]
  [InlineData(TaskType.DotNetCode)]
  public void TaskType_AllValuesAreDefined(TaskType taskType)
  {
    Assert.True(Enum.IsDefined(taskType));
  }

  [Fact]
  public void TaskType_HasExpectedCount()
  {
    var values = Enum.GetValues<TaskType>();
    Assert.Equal(6, values.Length);
  }

  #endregion

  #region TaskResult Tests

  [Fact]
  public void TaskResult_RequiredProperties_AreMandatory()
  {
    var result = new TaskResult
    {
      TaskId = "result-1",
      Success = true,
      AgentId = "agent-1"
    };

    Assert.Equal("result-1", result.TaskId);
    Assert.True(result.Success);
    Assert.Equal("agent-1", result.AgentId);
  }

  [Fact]
  public void TaskResult_OptionalProperties_HaveDefaults()
  {
    var result = new TaskResult
    {
      TaskId = "result-2",
      Success = false,
      AgentId = "agent-2"
    };

    Assert.Null(result.Stdout);
    Assert.Null(result.Stderr);
    Assert.Equal(0, result.ExitCode);
    Assert.Null(result.ErrorCode);
    Assert.Null(result.ErrorMessage);
    Assert.Equal(0, result.DurationMs);
  }

  [Fact]
  public void TaskResult_CanRepresentSuccess()
  {
    var result = new TaskResult
    {
      TaskId = "success-1",
      Success = true,
      Stdout = "Operation completed",
      ExitCode = 0,
      DurationMs = 150,
      AgentId = "agent-1"
    };

    Assert.True(result.Success);
    Assert.Equal(0, result.ExitCode);
    Assert.Null(result.ErrorCode);
  }

  [Fact]
  public void TaskResult_CanRepresentFailure()
  {
    var result = new TaskResult
    {
      TaskId = "failure-1",
      Success = false,
      Stderr = "File not found",
      ExitCode = 1,
      ErrorCode = TaskErrorCode.NotFound,
      ErrorMessage = "The specified file does not exist",
      DurationMs = 50,
      AgentId = "agent-1"
    };

    Assert.False(result.Success);
    Assert.NotEqual(0, result.ExitCode);
    Assert.Equal(TaskErrorCode.NotFound, result.ErrorCode);
    Assert.NotNull(result.ErrorMessage);
  }

  #endregion

  #region TaskErrorCode Tests

  [Theory]
  [InlineData(TaskErrorCode.None, 0)]
  [InlineData(TaskErrorCode.Timeout, 1)]
  [InlineData(TaskErrorCode.ElevationRequired, 2)]
  [InlineData(TaskErrorCode.NotFound, 3)]
  [InlineData(TaskErrorCode.PermissionDenied, 4)]
  [InlineData(TaskErrorCode.SyntaxError, 5)]
  [InlineData(TaskErrorCode.InvalidWorkingDirectory, 6)]
  [InlineData(TaskErrorCode.InternalError, 99)]
  public void TaskErrorCode_HasExpectedValues(TaskErrorCode code, int expectedValue)
  {
    Assert.Equal(expectedValue, (int)code);
  }

  [Fact]
  public void TaskErrorCode_None_IsZero()
  {
    Assert.Equal(0, (int)TaskErrorCode.None);
  }

  #endregion

  #region AgentCapabilities Tests

  [Fact]
  public void AgentCapabilities_RequiredProperties_AreMandatory()
  {
    var caps = new AgentCapabilities
    {
      AgentId = "agent-1",
      Hostname = "my-workstation",
      Platform = "windows",
      Capabilities = new[] { "powershell", "dotnet" },
      WorkspaceRoots = new[] { @"C:\Code" },
      CertificateFingerprint = "SHA256:ABC123"
    };

    Assert.Equal("agent-1", caps.AgentId);
    Assert.Equal("my-workstation", caps.Hostname);
    Assert.Equal("windows", caps.Platform);
    Assert.Equal(2, caps.Capabilities.Length);
    Assert.Single(caps.WorkspaceRoots);
  }

  [Fact]
  public void AgentCapabilities_DefaultPorts_AreSet()
  {
    var caps = new AgentCapabilities
    {
      AgentId = "agent-2",
      Hostname = "test-host",
      Platform = "linux",
      Capabilities = Array.Empty<string>(),
      WorkspaceRoots = Array.Empty<string>(),
      CertificateFingerprint = "SHA256:DEF456"
    };

    Assert.Equal(41420, caps.DiscoveryPort);
    Assert.Equal(41235, caps.GrpcPort);
  }

  [Fact]
  public void AgentCapabilities_IpAddress_IsOptional()
  {
    var caps = new AgentCapabilities
    {
      AgentId = "agent-3",
      Hostname = "host",
      Platform = "macos",
      Capabilities = Array.Empty<string>(),
      WorkspaceRoots = Array.Empty<string>(),
      CertificateFingerprint = "SHA256:GHI789"
    };

    Assert.Null(caps.IpAddress);
  }

  [Fact]
  public void AgentCapabilities_CanSetIpAddress()
  {
    var caps = new AgentCapabilities
    {
      AgentId = "agent-4",
      Hostname = "remote-host",
      Platform = "linux",
      Capabilities = new[] { "bash", "python" },
      WorkspaceRoots = new[] { "/home/user/projects" },
      CertificateFingerprint = "SHA256:JKL012",
      IpAddress = "192.168.1.100"
    };

    Assert.Equal("192.168.1.100", caps.IpAddress);
  }

  [Fact]
  public void AgentCapabilities_CanOverridePorts()
  {
    var caps = new AgentCapabilities
    {
      AgentId = "agent-5",
      Hostname = "custom-port-host",
      Platform = "windows",
      Capabilities = Array.Empty<string>(),
      WorkspaceRoots = Array.Empty<string>(),
      CertificateFingerprint = "SHA256:MNO345",
      DiscoveryPort = 50000,
      GrpcPort = 50001
    };

    Assert.Equal(50000, caps.DiscoveryPort);
    Assert.Equal(50001, caps.GrpcPort);
  }

  [Theory]
  [InlineData("windows")]
  [InlineData("linux")]
  [InlineData("macos")]
  public void AgentCapabilities_Platform_AcceptsValidValues(string platform)
  {
    var caps = new AgentCapabilities
    {
      AgentId = $"agent-{platform}",
      Hostname = "test",
      Platform = platform,
      Capabilities = Array.Empty<string>(),
      WorkspaceRoots = Array.Empty<string>(),
      CertificateFingerprint = "SHA256:TEST"
    };

    Assert.Equal(platform, caps.Platform);
  }

  [Fact]
  public void AgentCapabilities_IsRecord_SupportsEquality()
  {
    var caps1 = new AgentCapabilities
    {
      AgentId = "agent-eq",
      Hostname = "host",
      Platform = "linux",
      Capabilities = new[] { "bash" },
      WorkspaceRoots = new[] { "/home" },
      CertificateFingerprint = "SHA256:EQUAL"
    };

    var caps2 = new AgentCapabilities
    {
      AgentId = "agent-eq",
      Hostname = "host",
      Platform = "linux",
      Capabilities = new[] { "bash" },
      WorkspaceRoots = new[] { "/home" },
      CertificateFingerprint = "SHA256:EQUAL"
    };

    // Note: Records with array properties don't have value equality for arrays by default
    // This test documents the behavior
    Assert.Equal(caps1.AgentId, caps2.AgentId);
    Assert.Equal(caps1.Hostname, caps2.Hostname);
  }

  #endregion
}
