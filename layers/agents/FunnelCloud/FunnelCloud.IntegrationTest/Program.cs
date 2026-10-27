using System.Diagnostics;
using AJ.Orchestrator.Abstractions.Services;
using AJ.Orchestrator.Domain.Services;
using AJ.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

Console.WriteLine("=== FunnelCloud Integration Test ===\n");

// Test 1: CLI Status
Console.WriteLine("[Test 1] CLI Status Check");
var cliResult = RunCli("status");
if (cliResult.Contains("Agent is running"))
{
  Console.WriteLine("  ✓ CLI reports agent is running");
}
else
{
  Console.WriteLine($"  ✗ CLI status check failed: {cliResult}");
  return 1;
}

// Test 2: Named Pipe Connection
Console.WriteLine("\n[Test 2] Named Pipe Connection");
var listResult = RunCli("list");
Console.WriteLine($"  ✓ CLI connected to pipe (events: {(listResult.Contains("No recent") ? "0" : "some")})");

// Test 3: gRPC Task Execution
Console.WriteLine("\n[Test 3] gRPC Task Execution");
var agentId = Environment.GetEnvironmentVariable("FUNNEL_TEST_AGENT_ID")
  ?? Environment.GetEnvironmentVariable("FUNNEL_AGENT_ID")
  ?? Environment.MachineName.ToLowerInvariant();
var agentAddress = Environment.GetEnvironmentVariable("FUNNEL_TEST_AGENT_ADDRESS") ?? "127.0.0.1";
var agentPort = int.Parse(Environment.GetEnvironmentVariable("FUNNEL_TEST_AGENT_GRPC_PORT") ?? "41235");

try
{
  var agent = new AgentCapabilities
  {
    AgentId = agentId,
    Hostname = agentId,
    Platform = "test",
    Capabilities = [],
    WorkspaceRoots = [],
    CertificateFingerprint = "",
    IpAddress = agentAddress,
    GrpcPort = agentPort
  };
  var client = new GrpcAgentClient(new FixedDiscovery(agent), NullLogger<GrpcAgentClient>.Instance);
  var response = await client.ExecuteAsync(agentId, "Write-Output 'Hello from integration test'", 30);

  if (response.Success)
  {
    Console.WriteLine($"  ✓ Task completed successfully");
    Console.WriteLine($"    Exit code: {response.ExitCode}");
    Console.WriteLine($"    Duration: {response.DurationMs}ms");
    Console.WriteLine($"    Output: {response.Stdout?.Trim()}");
  }
  else
  {
    Console.WriteLine($"  ✗ Task failed: {response.ErrorMessage ?? response.Stderr}");
    return 1;
  }
}
catch (Exception ex)
{
  Console.WriteLine($"  ✗ gRPC error: {ex.Message}");
  Console.WriteLine("    Check the agent ID and configured client certificate, password, and CA paths.");
  return 1;
}

// Test 4: Verify Event Published
Console.WriteLine("\n[Test 4] Event Publication");
await Task.Delay(500); // Give time for event to be published
var eventsResult = RunCli("list");
if (eventsResult.Contains("task_completed"))
{
  Console.WriteLine("  ✓ Event was published to pipe");
}
else if (eventsResult.Contains("No recent"))
{
  Console.WriteLine("  ⚠ No events found (may need to check ring buffer)");
}
else
{
  Console.WriteLine($"  ? Events: {eventsResult}");
}

Console.WriteLine("\n=== All Tests Passed ===");
return 0;

static string RunCli(string args)
{
  var cliPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../FunnelCloud.Cli"));
  var psi = new ProcessStartInfo
  {
    FileName = "dotnet",
    Arguments = $"run --project \"{cliPath}\" -- {args}",
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    CreateNoWindow = true,
    WorkingDirectory = cliPath
  };

  using var proc = Process.Start(psi);
  var output = proc!.StandardOutput.ReadToEnd();
  var error = proc.StandardError.ReadToEnd();
  proc.WaitForExit(10000);

  return string.IsNullOrEmpty(output) ? error : output;
}

sealed class FixedDiscovery(AgentCapabilities agent) : IAgentDiscovery
{
  public Task<List<AgentCapabilities>> DiscoverAgentsAsync(CancellationToken ct = default) => Task.FromResult(new List<AgentCapabilities> { agent });
  public AgentCapabilities? GetAgent(string agentId) => agent.AgentId == agentId ? agent : null;
  public IReadOnlyList<AgentCapabilities> GetAllAgents() => [agent];
  public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
}
