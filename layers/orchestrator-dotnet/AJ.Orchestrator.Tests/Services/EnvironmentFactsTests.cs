using FluentAssertions;
using AJ.Orchestrator.Domain.Services;
using Xunit;

namespace AJ.Orchestrator.Tests.Services;

/// <summary>
/// Tests for EnvironmentFacts - ported from Python test_cov_session_state.py
/// </summary>
public class EnvironmentFactsTests
{
  [Fact]
  public void AddObservation_UniqueOnly()
  {
    var ef = new EnvironmentFacts();
    ef.AddObservation("fact1");
    ef.AddObservation("fact1");  // duplicate

    ef.Observations.Should().HaveCount(1);
  }

  [Fact]
  public void AddObservation_Empty_Ignored()
  {
    var ef = new EnvironmentFacts();
    ef.AddObservation("");

    ef.Observations.Should().BeEmpty();
  }

  [Fact]
  public void AddObservation_Over20_TruncatesToLast20()
  {
    var ef = new EnvironmentFacts();
    for (int i = 0; i < 25; i++)
    {
      ef.AddObservation($"fact{i}");
    }

    ef.Observations.Should().HaveCount(20);
  }

  [Fact]
  public void AddObservation_Null_Ignored()
  {
    var ef = new EnvironmentFacts();
    ef.AddObservation(null!);

    ef.Observations.Should().BeEmpty();
  }

  [Fact]
  public void WorkspaceMetrics_CanBeSetAndRead()
  {
    var ef = new EnvironmentFacts();
    ef.TotalFileCount = 100;
    ef.TotalDirCount = 10;
    ef.TotalSizeBytes = 1024 * 1024;
    ef.TotalSizeHuman = "1 MiB";

    ef.TotalFileCount.Should().Be(100);
    ef.TotalDirCount.Should().Be(10);
    ef.TotalSizeBytes.Should().Be(1024 * 1024);
    ef.TotalSizeHuman.Should().Be("1 MiB");
  }

  [Fact]
  public void ProjectTypes_CanAddAndQuery()
  {
    var ef = new EnvironmentFacts();
    ef.ProjectTypes.Add("python");
    ef.ProjectTypes.Add("docker");

    ef.ProjectTypes.Should().Contain("python");
    ef.ProjectTypes.Should().Contain("docker");
    ef.ProjectTypes.Should().HaveCount(2);
  }

  [Fact]
  public void FrameworksDetected_CanAddAndQuery()
  {
    var ef = new EnvironmentFacts();
    ef.FrameworksDetected.Add("fastapi");
    ef.FrameworksDetected.Add("pytest");

    ef.FrameworksDetected.Should().Contain("fastapi");
    ef.FrameworksDetected.Should().HaveCount(2);
  }

  [Fact]
  public void PackageManagers_CanAddAndQuery()
  {
    var ef = new EnvironmentFacts();
    ef.PackageManagers.Add("pip");
    ef.PackageManagers.Add("npm");

    ef.PackageManagers.Should().Contain("pip");
    ef.PackageManagers.Should().Contain("npm");
  }

  [Fact]
  public void SystemInfo_CanBeSetAndRead()
  {
    var ef = new EnvironmentFacts();
    ef.WorkingDirectory = "/home/user/project";
    ef.PythonVersion = "3.11.0";
    ef.NodeVersion = "20.0.0";
    ef.GitBranch = "main";
    ef.DockerRunning = true;

    ef.WorkingDirectory.Should().Be("/home/user/project");
    ef.PythonVersion.Should().Be("3.11.0");
    ef.NodeVersion.Should().Be("20.0.0");
    ef.GitBranch.Should().Be("main");
    ef.DockerRunning.Should().BeTrue();
  }

  [Fact]
  public void ProjectTypes_SetDeduplicates()
  {
    var ef = new EnvironmentFacts();
    ef.ProjectTypes.Add("python");
    ef.ProjectTypes.Add("python");

    ef.ProjectTypes.Should().HaveCount(1);
  }
}
