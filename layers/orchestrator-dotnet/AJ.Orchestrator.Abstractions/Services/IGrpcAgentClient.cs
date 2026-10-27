using AJ.Shared.Contracts;

namespace AJ.Orchestrator.Abstractions.Services;

/// <summary>
/// Client for executing tasks on FunnelCloud agents via gRPC.
/// </summary>
public interface IGrpcAgentClient
{
    /// <summary>
    /// Discover all available FunnelCloud agents.
    /// </summary>
    Task<List<AgentCapabilities>> DiscoverAgentsAsync(CancellationToken ct = default);

    /// <summary>
    /// Execute a command on a remote agent.
    /// </summary>
    Task<TaskExecutionResult> ExecuteAsync(
        string agentId,
        string command,
        int timeoutSeconds = 30,
        CancellationToken ct = default);

    /// <summary>
    /// Execute a command with streaming output.
    /// </summary>
    IAsyncEnumerable<string> ExecuteStreamingAsync(
        string agentId,
        string command,
        CancellationToken ct = default);
}

/// <summary>
/// Result of executing a task on an agent.
/// </summary>
public record TaskExecutionResult(
    bool Success,
    string? Stdout,
    string? Stderr,
    int ExitCode,
    long DurationMs,
    string? ErrorMessage = null
);
