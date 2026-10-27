using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FunnelCloud.Agent.Services.Events;
using FunnelCloud.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FunnelCloud.Agent.Services;

/// <summary>
/// Hosts the gRPC server with mTLS authentication.
/// 
/// Security model:
/// 1. Server presents certificate signed by FunnelCloud CA
/// 2. Client (orchestrator) must present certificate signed by same CA
/// 3. Both sides validate certificate chain against pinned CA fingerprint
/// </summary>
public class GrpcServerHost : BackgroundService
{
  private readonly ILogger<GrpcServerHost> _logger;
  private readonly AgentCapabilities _capabilities;
  private readonly TaskExecutor _executor;
  private readonly IAgentEventPublisher _events;
  private readonly int _port;
  private readonly string _caFingerprint;
  private IHost? _host;

  public GrpcServerHost(
      ILogger<GrpcServerHost> logger,
      AgentCapabilities capabilities,
      TaskExecutor executor,
      IAgentEventPublisher events,
      int? port = null)
        : this(logger, capabilities, executor, events, port, TrustConfig.CaFingerprint)
  {
  }

  internal GrpcServerHost(
    ILogger<GrpcServerHost> logger,
    AgentCapabilities capabilities,
    TaskExecutor executor,
    IAgentEventPublisher events,
    int? port,
    string caFingerprint)
  {
    _logger = logger;
    _capabilities = capabilities;
    _executor = executor;
    _events = events;
    _port = port ?? TrustConfig.GrpcPort;
    _caFingerprint = caFingerprint;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    _logger.LogInformation("Starting gRPC server on port {Port}", _port);

    try
    {
      // Load agent certificate
      var certPath = Environment.GetEnvironmentVariable("FUNNEL_CERT_PATH");
      var passwordFile = Environment.GetEnvironmentVariable("FUNNEL_CERT_PASSWORD_FILE");
      var certPassword = string.IsNullOrEmpty(passwordFile)
        ? Environment.GetEnvironmentVariable("FUNNEL_CERT_PASSWORD")
        : File.ReadAllText(passwordFile).TrimEnd('\r', '\n');

      if (string.IsNullOrEmpty(certPath))
      {
        // Try default location
        certPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "certs", "agents",
            _capabilities.AgentId ?? "dev-workstation",
            "agent.pfx");

      }

      if (!File.Exists(certPath))
        throw new FileNotFoundException("Agent certificate not found. Set FUNNEL_CERT_PATH.", certPath);

      if (string.IsNullOrEmpty(certPassword))
        throw new InvalidOperationException("Set FUNNEL_CERT_PASSWORD or FUNNEL_CERT_PASSWORD_FILE to load the agent certificate.");

      var certificate = LoadCertificate(certPath, certPassword);
      if (certificate == null)
      {
        throw new InvalidOperationException($"Failed to load agent certificate from {certPath}");
      }

      using var serverCertificate = certificate;
      var caPath = Environment.GetEnvironmentVariable("FUNNEL_CA_PATH")
          ?? Path.Combine(Path.GetDirectoryName(certPath)!, "ca.crt");
      using var trustedCa = X509CertificateLoader.LoadCertificateFromFile(caPath);
      if (!string.Equals(ComputeSha256Fingerprint(trustedCa), _caFingerprint, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("The configured CA does not match the pinned FunnelCloud CA fingerprint.");

      if (!HasValidChain(serverCertificate, null, trustedCa, "1.3.6.1.5.5.7.3.1"))
        throw new InvalidOperationException("The agent certificate is not valid for server authentication under the pinned CA.");

      var allowedClients = ParseClientFingerprints(Environment.GetEnvironmentVariable("FUNNEL_ALLOWED_CLIENT_SHA256"));
      _logger.LogInformation("Loaded certificate: {Subject}", serverCertificate.Subject);

      // Build and run the gRPC host
      _host = CreateHostBuilder(serverCertificate, trustedCa, allowedClients).Build();
      await _host.RunAsync(stoppingToken);
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
      _logger.LogInformation("gRPC server shutting down");
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "gRPC server failed");
      throw;
    }
  }

  private IHostBuilder CreateHostBuilder(X509Certificate2 serverCertificate, X509Certificate2 trustedCa, HashSet<string> allowedClients)
  {
    return Host.CreateDefaultBuilder()
        .ConfigureWebHostDefaults(webBuilder =>
        {
          webBuilder.ConfigureKestrel(options =>
              {
                options.Listen(IPAddress.Any, _port, listenOptions =>
                    {
                      listenOptions.Protocols = HttpProtocols.Http2;
                      listenOptions.UseHttps(httpsOptions =>
                        {
                          httpsOptions.ServerCertificate = serverCertificate;
                          httpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                          httpsOptions.ClientCertificateValidation = (certificate, chain, errors) =>
                            ValidateClientCertificate(certificate, chain, errors, trustedCa, allowedClients);
                        });
                    });
              });

          webBuilder.ConfigureServices(services =>
              {
                services.AddGrpc();
                services.AddSingleton(_capabilities);
                services.AddSingleton(_executor);
                services.AddSingleton<IAgentEventPublisher>(_events);
                services.AddSingleton<TaskServiceImpl>();
              });

          webBuilder.Configure(app =>
              {
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                    {
                      endpoints.MapGrpcService<TaskServiceImpl>();
                    });
              });
        });
  }

  private bool ValidateClientCertificate(
      X509Certificate2 certificate,
      X509Chain? chain,
      System.Net.Security.SslPolicyErrors sslPolicyErrors,
      X509Certificate2 trustedCa,
      HashSet<string> allowedClients)
  {
    var invalidErrors = System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable |
        System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch;
    var accepted = (sslPolicyErrors & invalidErrors) == 0 &&
      IsAuthorizedClientCertificate(certificate, chain, trustedCa, allowedClients);
    if (!accepted)
      _logger.LogWarning("Rejected untrusted or unauthorized client certificate");
    return accepted;
  }

  internal static bool IsAuthorizedClientCertificate(
      X509Certificate2 certificate, X509Chain? presentedChain, X509Certificate2 trustedCa, HashSet<string> allowedClients)
  {
    return allowedClients.Contains(certificate.GetCertHashString(HashAlgorithmName.SHA256)) &&
        HasValidChain(certificate, presentedChain, trustedCa, "1.3.6.1.5.5.7.3.2");
  }

  internal static HashSet<string> ParseClientFingerprints(string? value)
  {
    var fingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var entry in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
      var fingerprint = entry.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase) ? entry[7..] : entry;
      fingerprint = fingerprint.Replace(":", "");
      if (fingerprint.Length != 64 || !fingerprint.All(char.IsAsciiHexDigit))
        throw new InvalidOperationException("FUNNEL_ALLOWED_CLIENT_SHA256 contains an invalid SHA-256 fingerprint.");
      fingerprints.Add(fingerprint);
    }
    if (fingerprints.Count == 0)
      throw new InvalidOperationException("FUNNEL_ALLOWED_CLIENT_SHA256 must list authorized client certificate fingerprints.");
    return fingerprints;
  }

  internal static bool HasValidChain(X509Certificate2 certificate, X509Chain? presentedChain, X509Certificate2 trustedCa, string usage)
  {
    if (certificate.RawData.AsSpan().SequenceEqual(trustedCa.RawData) ||
        !trustedCa.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority) ||
        !certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == usage)))
      return false;

    using var chain = new X509Chain();
    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.CustomTrustStore.Add(trustedCa);
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    chain.ChainPolicy.ApplicationPolicy.Add(new Oid(usage));
    if (presentedChain != null)
      foreach (var element in presentedChain.ChainElements)
        chain.ChainPolicy.ExtraStore.Add(element.Certificate);
    return chain.Build(certificate);
  }

  private static string ComputeSha256Fingerprint(X509Certificate2 certificate)
  {
    using var sha256 = SHA256.Create();
    var certBytes = certificate.Export(X509ContentType.Cert);
    var hashBytes = sha256.ComputeHash(certBytes);
    return "SHA256:" + BitConverter.ToString(hashBytes).Replace("-", "");
  }

  private X509Certificate2? LoadCertificate(string path, string password)
  {
    try
    {
      return new X509Certificate2(
          path,
          password,
          X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to load certificate from {Path}", path);
      return null;
    }
  }

  public override async Task StopAsync(CancellationToken cancellationToken)
  {
    _logger.LogInformation("Stopping gRPC server");

    if (_host != null)
    {
      await _host.StopAsync(cancellationToken);
      _host.Dispose();
    }

    await base.StopAsync(cancellationToken);
  }
}
