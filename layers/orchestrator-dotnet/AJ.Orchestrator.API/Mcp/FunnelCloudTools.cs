using System.ComponentModel;
using AJ.Orchestrator.Abstractions.Services;
using ModelContextProtocol.Server;

namespace AJ.Orchestrator.API.Mcp;

/// <summary>
/// MCP facade over the FunnelCloud agent mesh. Exposes agent discovery and
/// command execution as MCP tools so any MCP client (VS Code, Claude, etc.)
/// can drive the homelab agents. gRPC/mTLS internals stay unchanged.
/// </summary>
[McpServerToolType]
public static class FunnelCloudTools
{
  [McpServerTool(Name = "list_agents")]
  [Description("Discover all currently online FunnelCloud agents with their capabilities and workspace roots.")]
  public static async Task<object> ListAgents(
      IGrpcAgentClient agentClient,
      CancellationToken cancellationToken)
  {
    var agents = await agentClient.DiscoverAgentsAsync(cancellationToken);

    return new
    {
      count = agents.Count,
      agents = agents.Select(a => new
      {
        agent_id = a.AgentId,
        hostname = a.Hostname,
        platform = a.Platform,
        capabilities = a.Capabilities,
        workspace_roots = a.WorkspaceRoots
      })
    };
  }

  [McpServerTool(Name = "execute_command")]
  [Description("Execute a shell command on a specific FunnelCloud agent. Use list_agents first to get a valid agent_id.")]
  public static async Task<object> ExecuteCommand(
      IGrpcAgentClient agentClient,
      [Description("Agent ID from list_agents (e.g. a hostname like 'postfix01').")] string agentId,
      [Description("The shell command to run on the agent.")] string command,
      [Description("Timeout in seconds (default 30).")] int timeoutSeconds = 30,
      CancellationToken cancellationToken = default)
  {
    var result = await agentClient.ExecuteAsync(agentId, command, timeoutSeconds, cancellationToken);

    return new
    {
      success = result.Success,
      exit_code = result.ExitCode,
      stdout = result.Stdout,
      stderr = result.Stderr,
      duration_ms = result.DurationMs,
      error = result.ErrorMessage
    };
  }
}
