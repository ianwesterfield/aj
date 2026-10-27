using System.Net;
using System.Text.Json;
using FunnelCloud.Agent.Services.Events;
using FunnelCloud.Shared.Contracts;
using FunnelCloud.Shared.Ipc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FunnelCloud.Agent.Services;

/// <summary>
/// Simple HTTP API server for the agent.
/// 
/// Provides endpoints for:
/// - /health - Health check
/// - /discover-peers - Trigger multicast discovery and return all found agents (including cached via gossip)
/// - /peers - Return cached peers only (fast, no network call)
/// 
/// This allows the orchestrator to use this agent as a discovery proxy
/// when multicast doesn't work from its network (e.g., WSL NAT, Docker bridge).
/// </summary>
public class HttpApiHost : BackgroundService
{
  private readonly ILogger<HttpApiHost> _logger;
  private readonly AgentCapabilities _capabilities;
  private readonly PeerDiscoveryService _peerDiscovery;
  private readonly PeerRegistry _peerRegistry;
  private readonly IAgentEventPublisher? _eventPublisher;
  private readonly int _port;
  private IHost? _host;

  public HttpApiHost(
      ILogger<HttpApiHost> logger,
      AgentCapabilities capabilities,
      PeerDiscoveryService peerDiscovery,
      PeerRegistry peerRegistry,
      IAgentEventPublisher? eventPublisher = null,
      int? port = null)
  {
    _logger = logger;
    _capabilities = capabilities;
    _peerDiscovery = peerDiscovery;
    _peerRegistry = peerRegistry;
    _eventPublisher = eventPublisher;
    _port = port ?? TrustConfig.HttpApiPort;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    _logger.LogInformation("Starting HTTP API server on port {Port}", _port);

    try
    {
      var builder = WebApplication.CreateBuilder();

      builder.Logging.ClearProviders();
      builder.Logging.AddConsole();

      builder.WebHost.ConfigureKestrel(options =>
      {
        options.Listen(IPAddress.Any, _port);
      });

      var app = builder.Build();

      // Health check endpoint
      app.MapGet("/health", () => Results.Json(new
      {
        status = "healthy",
        agentId = _capabilities.AgentId,
        hostname = _capabilities.Hostname,
        platform = _capabilities.Platform
      }));

      // Self capabilities endpoint
      app.MapGet("/capabilities", () => Results.Json(_capabilities, new JsonSerializerOptions
      {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
      }));

      // Cached peers endpoint - fast, no network call
      app.MapGet("/peers", () =>
      {
        var peers = _peerRegistry.GetAllPeers();
        // Include ourselves
        var allPeers = new List<AgentCapabilities> { _capabilities };
        allPeers.AddRange(peers);

        return Results.Json(new
        {
          agents = allPeers,
          count = allPeers.Count,
          source = _capabilities.AgentId,
          cached = true
        }, new JsonSerializerOptions
        {
          PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
      });

      // Peer discovery endpoint - this is the key feature!
      // The orchestrator calls this to discover all agents on the network
      app.MapGet("/discover-peers", async (HttpContext ctx) =>
      {
        var timeoutStr = ctx.Request.Query["timeout"].FirstOrDefault();
        var timeout = double.TryParse(timeoutStr, out var t) ? t : 2.0;

        _logger.LogInformation("Peer discovery requested from {RemoteIp} (timeout={Timeout}s)",
                  ctx.Connection.RemoteIpAddress, timeout);

        // Do fresh multicast discovery
        var freshAgents = await _peerDiscovery.DiscoverPeersAsync(timeout);

        // Add fresh to registry
        foreach (var agent in freshAgents)
        {
          _peerRegistry.AddOrUpdate(agent);
        }

        // Get all known peers (fresh + cached from gossip)
        var allPeers = _peerRegistry.GetAllPeers();

        // Include ourselves
        if (!allPeers.Any(p => p.AgentId == _capabilities.AgentId))
        {
          allPeers.Insert(0, _capabilities);
        }

        return Results.Json(new
        {
          agents = allPeers,
          count = allPeers.Count,
          discoveredBy = _capabilities.AgentId,
          freshCount = freshAgents.Count,
          cachedCount = allPeers.Count - freshAgents.Count
        }, new JsonSerializerOptions
        {
          PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
      });

      // Add peer endpoint - for cross-subnet bootstrap
      app.MapPost("/add-peer", async (HttpContext ctx) =>
      {
        var ip = ctx.Request.Query["ip"].FirstOrDefault();
        if (string.IsNullOrEmpty(ip))
        {
          return Results.BadRequest(new { error = "ip parameter required" });
        }

        _logger.LogInformation("Manual peer discovery requested for {IP}", ip);

        try
        {
          // Send UDP discovery to the specific IP
          using var udpClient = new System.Net.Sockets.UdpClient();
          udpClient.Client.ReceiveTimeout = 3000;
          var message = System.Text.Encoding.UTF8.GetBytes("FUNNEL_DISCOVER");
          var endpoint = new IPEndPoint(IPAddress.Parse(ip), TrustConfig.DiscoveryPort);

          await udpClient.SendAsync(message, message.Length, endpoint);

          var receiveTask = Task.Run<(byte[]? response, IPEndPoint? remoteEp)>(() =>
          {
            try
            {
              var remoteEp = new IPEndPoint(IPAddress.Any, 0);
              var response = udpClient.Receive(ref remoteEp);
              return (response, remoteEp);
            }
            catch { return (null, null); }
          });

          if (await Task.WhenAny(receiveTask, Task.Delay(3000)) == receiveTask)
          {
            var (response, remoteEp) = await receiveTask;
            if (response != null)
            {
              var json = System.Text.Encoding.UTF8.GetString(response);
              var peer = JsonSerializer.Deserialize<AgentCapabilities>(json, new JsonSerializerOptions
              {
                PropertyNameCaseInsensitive = true
              });

              if (peer != null)
              {
                // Ensure IP is set from the response endpoint
                if (string.IsNullOrEmpty(peer.IpAddress))
                {
                  peer = peer with { IpAddress = remoteEp!.Address.ToString() };
                }
                _peerRegistry.AddOrUpdate(peer);
                _logger.LogInformation("Added peer {AgentId} at {IP}", peer.AgentId, peer.IpAddress);

                return Results.Json(new
                {
                  success = true,
                  agentId = peer.AgentId,
                  ipAddress = peer.IpAddress,
                  hostname = peer.Hostname
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
              }
            }
          }

          return Results.Json(new { success = false, error = "No response from peer" });
        }
        catch (Exception ex)
        {
          _logger.LogWarning(ex, "Failed to add peer at {IP}", ip);
          return Results.Json(new { success = false, error = ex.Message });
        }
      });

      // Test event endpoint for debugging event publishing
      app.MapPost("/test-event", () =>
      {
        if (_eventPublisher == null)
          return Results.Json(new { success = false, error = "Event publisher not available" });

        var evt = new AgentEvent
        {
          EventId = Guid.NewGuid().ToString(),
          Timestamp = DateTimeOffset.UtcNow,
          Kind = AgentEventKind.TaskCompleted,
          TaskId = "test-" + DateTime.UtcNow.Ticks,
          TaskType = "test",
          Command = "echo hello",
          ExitCode = 0,
          Stdout = "hello\n",
          DurationSeconds = 0.1,
          AgentId = _capabilities.AgentId
        };
        _eventPublisher.Publish(evt);
        _logger.LogInformation("Published test event {EventId}", evt.EventId);
        return Results.Json(new { success = true, eventId = evt.EventId });
      });

      _logger.LogInformation("HTTP API server listening on port {Port}", _port);
      _logger.LogInformation("  GET /health - Health check");
      _logger.LogInformation("  GET /capabilities - This agent's capabilities");
      _logger.LogInformation("  GET /discover-peers - Discover all network agents via multicast");
      _logger.LogInformation("  POST /add-peer?ip=x.x.x.x - Add a peer manually (cross-subnet bootstrap)");
      _logger.LogInformation("  POST /test-event - Publish a test event for debugging");

      _host = app;
      await app.RunAsync(stoppingToken);
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
      _logger.LogInformation("HTTP API server shutting down");
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "HTTP API server failed");
      throw;
    }
  }

  public override async Task StopAsync(CancellationToken cancellationToken)
  {
    if (_host != null)
    {
      await _host.StopAsync(cancellationToken);
    }
    await base.StopAsync(cancellationToken);
  }
}
