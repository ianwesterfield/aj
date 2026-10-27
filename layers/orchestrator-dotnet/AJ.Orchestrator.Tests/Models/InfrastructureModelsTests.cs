using AJ.Orchestrator.Abstractions.Models.Infrastructure;
using FluentAssertions;

namespace AJ.Orchestrator.Tests.Models;

/// <summary>
/// Tests for infrastructure models.
/// </summary>
public class InfrastructureModelsTests
{
  [Fact]
  public void HealthResponse_Healthy_ShouldHaveCorrectValues()
  {
    var response = new HealthResponse
    {
      Status = "healthy",
      Service = "orchestrator",
      Version = "1.0.0"
    };

    response.Status.Should().Be("healthy");
    response.Service.Should().Be("orchestrator");
    response.Version.Should().Be("1.0.0");
  }

  [Fact]
  public void HealthResponse_Unhealthy_ShouldReflectStatus()
  {
    var response = new HealthResponse
    {
      Status = "unhealthy",
      Service = "orchestrator",
      Version = "1.0.0"
    };

    response.Status.Should().Be("unhealthy");
  }

  [Fact]
  public void HealthResponse_Degraded_ShouldReflectStatus()
  {
    var response = new HealthResponse
    {
      Status = "degraded",
      Service = "orchestrator",
      Version = "1.0.0"
    };

    response.Status.Should().Be("degraded");
  }

  [Theory]
  [InlineData("1.0.0")]
  [InlineData("2.0.0-beta")]
  [InlineData("3.1.4-alpha.1")]
  [InlineData("0.0.1")]
  public void HealthResponse_DifferentVersions_ShouldBeValid(string version)
  {
    var response = new HealthResponse
    {
      Status = "healthy",
      Service = "orchestrator",
      Version = version
    };

    response.Version.Should().Be(version);
  }

  [Theory]
  [InlineData("orchestrator")]
  [InlineData("memory")]
  [InlineData("extractor")]
  [InlineData("pragmatics")]
  public void HealthResponse_DifferentServices_ShouldBeValid(string service)
  {
    var response = new HealthResponse
    {
      Status = "healthy",
      Service = service,
      Version = "1.0.0"
    };

    response.Service.Should().Be(service);
  }
}
