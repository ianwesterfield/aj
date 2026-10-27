namespace AJ.Orchestrator.Tests.Services;

using AJ.Orchestrator.Domain.Services;
using AJ.Orchestrator.Abstractions.Services;
using FunnelCloud.Agent.Services;
using FunnelCloud.Agent.Services.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

public class GrpcAgentClientSecurityTests
{
  [Fact]
  public void ServerCertificate_RequiresMatchingIdentityAndValidServerChain()
  {
    using var trustedCa = CreateCa();
    using var forgedCa = CreateCa();
    using var server = CreateLeaf(trustedCa, "worker-01", "1.3.6.1.5.5.7.3.1");
    using var forged = CreateLeaf(forgedCa, "worker-01", "1.3.6.1.5.5.7.3.1");
    using var clientOnly = CreateLeaf(trustedCa, "worker-01", "1.3.6.1.5.5.7.3.2");
    using var expired = CreateLeaf(trustedCa, "worker-01", "1.3.6.1.5.5.7.3.1", expired: true);
    var platformErrors = SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch;

    Assert.True(GrpcAgentClient.IsTrustedServerCertificate(server, null, platformErrors, trustedCa, "worker-01"));
    Assert.False(GrpcAgentClient.IsTrustedServerCertificate(server, null, platformErrors, trustedCa, "worker-02"));
    Assert.False(GrpcAgentClient.IsTrustedServerCertificate(forged, null, platformErrors, trustedCa, "worker-01"));
    Assert.False(GrpcAgentClient.IsTrustedServerCertificate(clientOnly, null, platformErrors, trustedCa, "worker-01"));
    Assert.False(GrpcAgentClient.IsTrustedServerCertificate(expired, null, platformErrors, trustedCa, "worker-01"));
    Assert.False(GrpcAgentClient.IsTrustedServerCertificate(null, null, SslPolicyErrors.RemoteCertificateNotAvailable, trustedCa, "worker-01"));
  }

  [Fact]
  public async Task MutualTls_AllowsApprovedClientAndRejectsOtherClients()
  {
    using var trustedCa = CreateCa();
    using var serverCertificate = CreateLeaf(trustedCa, "worker-01", "1.3.6.1.5.5.7.3.1", includePrivateKey: true);
    using var allowedCertificate = CreateLeaf(trustedCa, "orchestrator", "1.3.6.1.5.5.7.3.2", includePrivateKey: true);
    using var otherCertificate = CreateLeaf(trustedCa, "other-client", "1.3.6.1.5.5.7.3.2", includePrivateKey: true);
    var directory = Path.Combine(Path.GetTempPath(), $"funnel-tls-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var caPath = Path.Combine(directory, "ca.crt");
    var serverPath = Path.Combine(directory, "server.pfx");
    var clientPath = Path.Combine(directory, "client.pfx");
    var otherPath = Path.Combine(directory, "other.pfx");
    var serverPasswordPath = Path.Combine(directory, "server-password");
    var markerPath = Path.Combine(directory, "unexpected-command-output.txt");
    var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
    File.WriteAllText(caPath, trustedCa.ExportCertificatePem());
    File.WriteAllBytes(serverPath, serverCertificate.Export(X509ContentType.Pkcs12, password));
    File.WriteAllBytes(clientPath, allowedCertificate.Export(X509ContentType.Pkcs12, password));
    File.WriteAllBytes(otherPath, otherCertificate.Export(X509ContentType.Pkcs12, password));
    File.WriteAllText(serverPasswordPath, password);

    var settings = new Dictionary<string, string?>
    {
      ["FUNNEL_CERT_PATH"] = serverPath,
      ["FUNNEL_CERT_PASSWORD"] = null,
      ["FUNNEL_CERT_PASSWORD_FILE"] = serverPasswordPath,
      ["FUNNEL_CA_PATH"] = caPath,
      ["FUNNEL_ALLOWED_CLIENT_SHA256"] = allowedCertificate.GetCertHashString(HashAlgorithmName.SHA256),
      ["FUNNEL_CLIENT_CERT_PATH"] = clientPath,
      ["FUNNEL_CLIENT_CERT_PASSWORD"] = password,
      ["FUNNEL_CLIENT_CERT_PASSWORD_FILE"] = null
    };
    var previousSettings = settings.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
    foreach (var setting in settings)
      Environment.SetEnvironmentVariable(setting.Key, setting.Value);

    var portListener = new TcpListener(IPAddress.Loopback, 0);
    portListener.Start();
    var port = ((IPEndPoint)portListener.LocalEndpoint).Port;
    portListener.Stop();
    var serverCapabilities = new FunnelCloud.Shared.Contracts.AgentCapabilities
    {
      AgentId = "worker-01",
      Hostname = "worker-01",
      Platform = "test",
      Capabilities = [],
      WorkspaceRoots = [],
      CertificateFingerprint = ""
    };
    using var executor = new TaskExecutor(NullLogger<TaskExecutor>.Instance, serverCapabilities.AgentId);
    using var server = new GrpcServerHost(
        NullLogger<GrpcServerHost>.Instance, serverCapabilities, executor,
        Mock.Of<IAgentEventPublisher>(), port, "SHA256:" + trustedCa.GetCertHashString(HashAlgorithmName.SHA256));

    try
    {
      await server.StartAsync(CancellationToken.None);
      using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
      while (true)
      {
        startupTimeout.Token.ThrowIfCancellationRequested();
        if (server.ExecuteTask!.IsCompleted)
          await server.ExecuteTask;
        try
        {
          using var probe = new TcpClient();
          await probe.ConnectAsync(IPAddress.Loopback, port, startupTimeout.Token);
          break;
        }
        catch (SocketException)
        {
          await Task.Delay(50, startupTimeout.Token);
        }
      }

      var discovery = new Mock<IAgentDiscovery>();
      discovery.Setup(instance => instance.GetAgent("worker-01")).Returns(new AJ.Shared.Contracts.AgentCapabilities
      {
        AgentId = "worker-01",
        Hostname = "worker-01",
        Platform = "test",
        Capabilities = [],
        WorkspaceRoots = [],
        CertificateFingerprint = "",
        IpAddress = "127.0.0.1",
        GrpcPort = port
      });
      var client = new GrpcAgentClient(discovery.Object, NullLogger<GrpcAgentClient>.Instance);
      var accepted = await client.ExecuteAsync("worker-01", "Write-Output 'tls-approved'", 10);
      Assert.True(accepted.Success, accepted.ErrorMessage ?? accepted.Stderr);
      Assert.Contains("tls-approved", accepted.Stdout);

      Environment.SetEnvironmentVariable("FUNNEL_CLIENT_CERT_PATH", otherPath);
      var rejected = await client.ExecuteAsync("worker-01", $"Set-Content -LiteralPath '{markerPath}' -Value forbidden", 5);
      Assert.False(rejected.Success);
      Assert.False(File.Exists(markerPath));

      using var anonymousHandler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
      using var anonymousClient = new HttpClient(anonymousHandler) { Timeout = TimeSpan.FromSeconds(5) };
      var anonymousError = await Record.ExceptionAsync(() => anonymousClient.GetAsync($"https://127.0.0.1:{port}/"));
      Assert.NotNull(anonymousError);
    }
    finally
    {
      await server.StopAsync(CancellationToken.None);
      foreach (var setting in previousSettings)
        Environment.SetEnvironmentVariable(setting.Key, setting.Value);
      Directory.Delete(directory, recursive: true);
    }
  }

  private static X509Certificate2 CreateCa()
  {
    using var key = RSA.Create(2048);
    var request = new CertificateRequest("CN=FunnelCloud Test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
    request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
    return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(10));
  }

  private static X509Certificate2 CreateLeaf(X509Certificate2 issuer, string name, string usage, bool expired = false, bool includePrivateKey = false)
  {
    using var key = RSA.Create(2048);
    var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(usage) }, true));
    var notBefore = DateTimeOffset.UtcNow.AddDays(-3);
    var notAfter = expired ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(1);
    var certificate = request.Create(issuer, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
    if (!includePrivateKey)
      return certificate;
    using (certificate)
      return certificate.CopyWithPrivateKey(key);
  }
}