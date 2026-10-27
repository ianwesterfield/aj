using AJ.Orchestrator.Abstractions.Models.Session;
using FluentAssertions;

namespace AJ.Orchestrator.Tests.Models;

/// <summary>
/// Tests for session-related models.
/// </summary>
public class SessionModelsTests
{
  [Fact]
  public void ResetStateRequest_WithSessionId_ShouldStoreIt()
  {
    var request = new ResetStateRequest { SessionId = "session-123" };

    request.SessionId.Should().Be("session-123");
  }

  [Fact]
  public void ResetStateRequest_WithoutSessionId_ShouldBeNull()
  {
    var request = new ResetStateRequest();

    request.SessionId.Should().BeNull();
  }

  [Fact]
  public void ResetStateResponse_Success_ShouldHaveMessage()
  {
    var response = new ResetStateResponse
    {
      Success = true,
      Message = "Session state cleared"
    };

    response.Success.Should().BeTrue();
    response.Message.Should().Be("Session state cleared");
  }

  [Fact]
  public void ResetStateResponse_Failure_ShouldHaveErrorMessage()
  {
    var response = new ResetStateResponse
    {
      Success = false,
      Message = "Session not found"
    };

    response.Success.Should().BeFalse();
    response.Message.Should().Be("Session not found");
  }

  [Fact]
  public void ResetStateResponse_NoMessage_ShouldBeNull()
  {
    var response = new ResetStateResponse
    {
      Success = true
    };

    response.Success.Should().BeTrue();
    response.Message.Should().BeNull();
  }
}
