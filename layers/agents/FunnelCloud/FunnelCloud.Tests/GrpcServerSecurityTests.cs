namespace FunnelCloud.Tests;

using FunnelCloud.Agent.Services;
using FunnelCloud.Agent.Services.Events;
using FunnelCloud.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

public class GrpcServerSecurityTests
{
  [Fact]
  public async Task MissingCertificate_FailsClosed()
  {
    var previousPath = Environment.GetEnvironmentVariable("FUNNEL_CERT_PATH");
    var missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.pfx");
    Environment.SetEnvironmentVariable("FUNNEL_CERT_PATH", missingPath);

    try
    {
      var capabilities = new AgentCapabilities
      {
        AgentId = "security-test",
        Hostname = "localhost",
        Platform = "test",
        Capabilities = [],
        WorkspaceRoots = [],
        CertificateFingerprint = ""
      };
      using var executor = new TaskExecutor(NullLogger<TaskExecutor>.Instance, capabilities.AgentId);
      using var server = new GrpcServerHost(
          NullLogger<GrpcServerHost>.Instance,
          capabilities,
          executor,
          Mock.Of<IAgentEventPublisher>());

      await server.StartAsync(CancellationToken.None);
      await Assert.ThrowsAsync<FileNotFoundException>(
          async () => await server.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    finally
    {
      Environment.SetEnvironmentVariable("FUNNEL_CERT_PATH", previousPath);
    }
  }

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task InvalidTrustConfiguration_FailsClosed(bool wrongCaPin)
  {
    using var trustedCa = CreateCa();
    using var key = RSA.Create(2048);
    var request = new CertificateRequest("CN=security-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));
    using var issued = request.Create(trustedCa, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
    using var certificate = issued.CopyWithPrivateKey(key);
    var directory = Path.Combine(Path.GetTempPath(), $"funnel-startup-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var certPath = Path.Combine(directory, "agent.pfx");
    var caPath = Path.Combine(directory, "ca.crt");
    var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
    File.WriteAllBytes(certPath, certificate.Export(X509ContentType.Pkcs12, password));
    File.WriteAllText(caPath, trustedCa.ExportCertificatePem());

    var settings = new Dictionary<string, string?>
    {
      ["FUNNEL_CERT_PATH"] = certPath,
      ["FUNNEL_CERT_PASSWORD"] = password,
      ["FUNNEL_CERT_PASSWORD_FILE"] = null,
      ["FUNNEL_CA_PATH"] = caPath,
      ["FUNNEL_ALLOWED_CLIENT_SHA256"] = wrongCaPin ? new string('A', 64) : null
    };
    var previousSettings = settings.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
    foreach (var setting in settings)
      Environment.SetEnvironmentVariable(setting.Key, setting.Value);

    try
    {
      var capabilities = new AgentCapabilities
      {
        AgentId = "security-test",
        Hostname = "localhost",
        Platform = "test",
        Capabilities = [],
        WorkspaceRoots = [],
        CertificateFingerprint = ""
      };
      using var executor = new TaskExecutor(NullLogger<TaskExecutor>.Instance, capabilities.AgentId);
      var pin = wrongCaPin ? "SHA256:" + new string('0', 64) : "SHA256:" + trustedCa.GetCertHashString(HashAlgorithmName.SHA256);
      using var server = new GrpcServerHost(NullLogger<GrpcServerHost>.Instance, capabilities, executor,
          Mock.Of<IAgentEventPublisher>(), null, pin);

      await server.StartAsync(CancellationToken.None);
      var exception = await Assert.ThrowsAsync<InvalidOperationException>(
          async () => await server.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));
      Assert.Contains(wrongCaPin ? "pinned" : "FUNNEL_ALLOWED_CLIENT_SHA256", exception.Message);
    }
    finally
    {
      foreach (var setting in previousSettings)
        Environment.SetEnvironmentVariable(setting.Key, setting.Value);
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public void ClientCertificate_RequiresPinnedIdentityAndValidClientChain()
  {
    using var trustedCa = CreateCa();
    using var forgedCa = CreateCa();
    using var allowedClient = CreateLeaf(trustedCa, "1.3.6.1.5.5.7.3.2");
    using var otherClient = CreateLeaf(trustedCa, "1.3.6.1.5.5.7.3.2");
    using var forgedClient = CreateLeaf(forgedCa, "1.3.6.1.5.5.7.3.2");
    using var serverOnly = CreateLeaf(trustedCa, "1.3.6.1.5.5.7.3.1");
    using var expired = CreateLeaf(trustedCa, "1.3.6.1.5.5.7.3.2", expired: true);
    var allowed = GrpcServerHost.ParseClientFingerprints(allowedClient.GetCertHashString(HashAlgorithmName.SHA256));

    Assert.True(GrpcServerHost.IsAuthorizedClientCertificate(allowedClient, null, trustedCa, allowed));
    Assert.False(GrpcServerHost.IsAuthorizedClientCertificate(otherClient, null, trustedCa, allowed));
    Assert.False(GrpcServerHost.IsAuthorizedClientCertificate(forgedClient, null, trustedCa,
        GrpcServerHost.ParseClientFingerprints(forgedClient.GetCertHashString(HashAlgorithmName.SHA256))));
    Assert.False(GrpcServerHost.IsAuthorizedClientCertificate(serverOnly, null, trustedCa,
        GrpcServerHost.ParseClientFingerprints(serverOnly.GetCertHashString(HashAlgorithmName.SHA256))));
    Assert.False(GrpcServerHost.IsAuthorizedClientCertificate(expired, null, trustedCa,
        GrpcServerHost.ParseClientFingerprints(expired.GetCertHashString(HashAlgorithmName.SHA256))));
  }

  [Fact]
  public void ClientFingerprintConfiguration_MustBeValidAndNonempty()
  {
    Assert.Throws<InvalidOperationException>(() => GrpcServerHost.ParseClientFingerprints(null));
    Assert.Throws<InvalidOperationException>(() => GrpcServerHost.ParseClientFingerprints("not-a-fingerprint"));
    Assert.Single(GrpcServerHost.ParseClientFingerprints("SHA256:" + new string('A', 64)));
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

  private static X509Certificate2 CreateLeaf(X509Certificate2 issuer, string usage, bool expired = false)
  {
    using var key = RSA.Create(2048);
    var request = new CertificateRequest("CN=test-client", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(usage) }, true));
    var notBefore = DateTimeOffset.UtcNow.AddDays(-3);
    var notAfter = expired ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(1);
    return request.Create(issuer, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
  }
}