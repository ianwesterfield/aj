using System.Diagnostics;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AJ.Orchestrator.Abstractions.Protos;
using AJ.Orchestrator.Abstractions.Services;
using AJ.Shared.Contracts;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;

namespace AJ.Orchestrator.Domain.Services;

/// <summary>
/// gRPC client for executing tasks on FunnelCloud agents.
/// Delegates to FunnelCloud agents for command execution.
/// </summary>
public class GrpcAgentClient : IGrpcAgentClient
{
  private readonly IAgentDiscovery _discovery;
  private readonly ILogger<GrpcAgentClient> _logger;
  private readonly string? _localAgentGrpcUrl;

  public GrpcAgentClient(
      IAgentDiscovery discovery,
      ILogger<GrpcAgentClient> logger)
  {
    _discovery = discovery;
    _logger = logger;

    // Pre-compute the local agent gRPC URL from the FunnelCloud HTTP URL
    // FunnelCloud HTTP is on 41421, gRPC is on 41235
    var funnelUrl = Environment.GetEnvironmentVariable("FunnelCloud__Url");
    if (!string.IsNullOrEmpty(funnelUrl))
    {
      var uri = new Uri(funnelUrl);
      _localAgentGrpcUrl = new UriBuilder(Uri.UriSchemeHttps, uri.Host, 41235).Uri.ToString();
    }
  }

  public async Task<List<AgentCapabilities>> DiscoverAgentsAsync(CancellationToken ct = default)
  {
    return await _discovery.DiscoverAgentsAsync(ct);
  }

  public async Task<TaskExecutionResult> ExecuteAsync(
      string agentId,
      string command,
      int timeoutSeconds = 30,
      CancellationToken ct = default)
  {
    var stopwatch = Stopwatch.StartNew();

    try
    {
      var agent = _discovery.GetAgent(agentId);
      if (agent == null)
      {
        return new TaskExecutionResult(
            false, null, null, 1, stopwatch.ElapsedMilliseconds,
            $"Agent not found: {agentId}. Use list_agents to see available agents.");
      }

      // Build gRPC address — use agent's IP for remote, or local FunnelCloud URL for self
      var grpcAddress = GetGrpcAddress(agent, agentId);
      if (grpcAddress == null)
      {
        return new TaskExecutionResult(
            false, null, null, 1, stopwatch.ElapsedMilliseconds,
            $"Cannot determine gRPC address for agent {agentId}");
      }

      return await ExecuteViaGrpcAsync(grpcAddress, agentId, command, timeoutSeconds, ct, stopwatch);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to execute command on {AgentId}", agentId);
      return new TaskExecutionResult(
          false, null, ex.Message, 1, stopwatch.ElapsedMilliseconds,
          ex.Message);
    }
  }

  private string? GetGrpcAddress(AgentCapabilities agent, string agentId)
  {
    // For remote agents with known IP, connect directly
    if (!string.IsNullOrEmpty(agent.IpAddress))
      return new UriBuilder(Uri.UriSchemeHttps, agent.IpAddress, agent.GrpcPort).Uri.ToString();

    // For agents without an IP (local FunnelCloud agent), use the configured gRPC URL
    return _localAgentGrpcUrl;
  }

  private async Task<TaskExecutionResult> ExecuteViaGrpcAsync(
      string grpcAddress,
      string agentId,
      string command,
      int timeoutSeconds,
      CancellationToken ct,
      Stopwatch stopwatch)
  {
    _logger.LogInformation("Executing via gRPC at {Address}: {Command}", grpcAddress, command);

    var clientCertPath = Environment.GetEnvironmentVariable("FUNNEL_CLIENT_CERT_PATH");
    var caPath = Environment.GetEnvironmentVariable("FUNNEL_CA_PATH");
    var passwordFile = Environment.GetEnvironmentVariable("FUNNEL_CLIENT_CERT_PASSWORD_FILE");
    var password = string.IsNullOrEmpty(passwordFile)
        ? Environment.GetEnvironmentVariable("FUNNEL_CLIENT_CERT_PASSWORD")
        : File.ReadAllText(passwordFile).TrimEnd('\r', '\n');
    if (string.IsNullOrWhiteSpace(clientCertPath) || string.IsNullOrWhiteSpace(caPath) || string.IsNullOrEmpty(password))
      throw new InvalidOperationException("Configure FUNNEL_CLIENT_CERT_PATH, FUNNEL_CA_PATH, and FUNNEL_CLIENT_CERT_PASSWORD or FUNNEL_CLIENT_CERT_PASSWORD_FILE.");

    using var clientCertificate = X509CertificateLoader.LoadPkcs12FromFile(clientCertPath, password);
    if (!clientCertificate.HasPrivateKey)
      throw new InvalidOperationException("The FunnelCloud client certificate must contain its private key.");
    using var trustedCa = X509CertificateLoader.LoadCertificateFromFile(caPath);
    using var handler = new HttpClientHandler
    {
      ClientCertificateOptions = ClientCertificateOption.Manual,
      ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
          IsTrustedServerCertificate(certificate, chain, errors, trustedCa, agentId)
    };
    handler.ClientCertificates.Add(clientCertificate);
    using var channel = GrpcChannel.ForAddress(grpcAddress, new GrpcChannelOptions { HttpHandler = handler });
    var client = new TaskService.TaskServiceClient(channel);

    var request = new Abstractions.Protos.TaskRequest
    {
      TaskId = Guid.NewGuid().ToString("N"),
      Type = Abstractions.Protos.TaskType.Powershell,
      Command = command,
      TimeoutSeconds = timeoutSeconds
    };

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds + 5));

    var result = await client.ExecuteAsync(request, cancellationToken: cts.Token);

    stopwatch.Stop();

    return new TaskExecutionResult(
        result.Success,
        string.IsNullOrEmpty(result.Stdout) ? null : result.Stdout,
        string.IsNullOrEmpty(result.Stderr) ? null : result.Stderr,
        result.ExitCode,
        result.DurationMs > 0 ? result.DurationMs : stopwatch.ElapsedMilliseconds,
        result.ErrorCode != ErrorCode.ErrorNone && result.ErrorCode != ErrorCode.Unspecified
            ? $"Agent error: {result.ErrorCode}" : null);
  }

  internal static bool IsTrustedServerCertificate(
      X509Certificate2? certificate, X509Chain? presentedChain, SslPolicyErrors errors, X509Certificate2 trustedCa, string agentId)
  {
    if (certificate == null || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0 ||
        certificate.RawData.AsSpan().SequenceEqual(trustedCa.RawData) ||
        !trustedCa.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority) ||
        !certificate.MatchesHostname(agentId, allowWildcards: false, allowCommonName: true) ||
        !certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1")))
      return false;

    using var chain = new X509Chain();
    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.CustomTrustStore.Add(trustedCa);
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
    if (presentedChain != null)
      foreach (var element in presentedChain.ChainElements)
        chain.ChainPolicy.ExtraStore.Add(element.Certificate);
    return chain.Build(certificate);
  }

  public async IAsyncEnumerable<string> ExecuteStreamingAsync(
      string agentId,
      string command,
      [EnumeratorCancellation] CancellationToken ct = default)
  {
    // For all agents, delegate to non-streaming gRPC and return lines
    var result = await ExecuteAsync(agentId, command, 3600, ct);
    if (result.Stdout != null)
    {
      foreach (var line in result.Stdout.Split('\n'))
      {
        yield return line;
      }
    }
    if (result.ErrorMessage != null)
    {
      yield return $"[ERROR] {result.ErrorMessage}";
    }
  }
}
