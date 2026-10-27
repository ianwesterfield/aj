using AJ.Orchestrator.Abstractions.Models.Classification;
using FluentAssertions;

namespace AJ.Orchestrator.Tests.Models;

/// <summary>
/// Tests for classification models.
/// </summary>
public class ClassificationModelsTests
{
  [Fact]
  public void ClassifyRequest_WithText_ShouldStoreIt()
  {
    var request = new ClassifyRequest { Text = "List all files in the current directory" };

    request.Text.Should().Be("List all files in the current directory");
    request.Context.Should().BeNull();
  }

  [Fact]
  public void ClassifyRequest_WithContext_ShouldStoreIt()
  {
    var request = new ClassifyRequest
    {
      Text = "Explain this",
      Context = "Previous conversation context here"
    };

    request.Text.Should().Be("Explain this");
    request.Context.Should().Be("Previous conversation context here");
  }

  [Fact]
  public void ClassifyResponse_TaskIntent_ShouldHaveHighConfidence()
  {
    var response = new ClassifyResponse(
        "task",
        0.95,
        "User wants to perform an action"
    );

    response.Intent.Should().Be("task");
    response.Confidence.Should().BeGreaterThan(0.9);
    response.Reason.Should().Contain("action");
  }

  [Fact]
  public void ClassifyResponse_CasualIntent_ShouldBeLowerConfidence()
  {
    var response = new ClassifyResponse(
        "casual",
        0.7,
        "General conversation"
    );

    response.Intent.Should().Be("casual");
    response.Confidence.Should().BeLessThanOrEqualTo(0.7);
  }

  [Fact]
  public void ClassifyResponse_WithDefaultReason_ShouldBeEmpty()
  {
    var response = new ClassifyResponse("task", 0.8);

    response.Intent.Should().Be("task");
    response.Confidence.Should().Be(0.8);
    response.Reason.Should().BeEmpty();
  }

  [Theory]
  [InlineData("task", 0.99)]
  [InlineData("casual", 0.5)]
  [InlineData("question", 0.85)]
  [InlineData("command", 0.91)]
  public void ClassifyResponse_DifferentIntents_ShouldBeValid(string intent, double confidence)
  {
    var response = new ClassifyResponse(intent, confidence);

    response.Intent.Should().Be(intent);
    response.Confidence.Should().Be(confidence);
  }

  [Fact]
  public void ClassifyResponse_EdgeCaseConfidence_ShouldHandleBoundaries()
  {
    var zeroConfidence = new ClassifyResponse("unknown", 0.0);
    var fullConfidence = new ClassifyResponse("task", 1.0);

    zeroConfidence.Confidence.Should().Be(0.0);
    fullConfidence.Confidence.Should().Be(1.0);
  }
}
