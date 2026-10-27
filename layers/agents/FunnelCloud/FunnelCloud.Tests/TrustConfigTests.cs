namespace FunnelCloud.Tests;

using FunnelCloud.Agent;

/// <summary>
/// Tests for TrustConfig constants to ensure configuration integrity.
/// </summary>
public class TrustConfigTests
{
  [Fact]
  public void CaFingerprint_HasValidSha256Format()
  {
    // SHA256 fingerprints should start with "SHA256:" followed by 64 hex chars
    Assert.StartsWith("SHA256:", TrustConfig.CaFingerprint);
    var hexPart = TrustConfig.CaFingerprint.Substring(7);
    Assert.Equal(64, hexPart.Length);
    Assert.Matches("^[A-F0-9]+$", hexPart);
  }

  [Fact]
  public void DiscoveryPort_IsInValidRange()
  {
    Assert.InRange(TrustConfig.DiscoveryPort, 1024, 65535);
    Assert.Equal(41420, TrustConfig.DiscoveryPort);
  }

  [Fact]
  public void GrpcPort_IsInValidRange()
  {
    Assert.InRange(TrustConfig.GrpcPort, 1024, 65535);
    Assert.Equal(41235, TrustConfig.GrpcPort);
  }

  [Fact]
  public void HttpApiPort_IsInValidRange()
  {
    Assert.InRange(TrustConfig.HttpApiPort, 1024, 65535);
    Assert.Equal(41421, TrustConfig.HttpApiPort);
  }

  [Fact]
  public void MulticastGroup_IsValidOrganizationLocalAddress()
  {
    // 239.0.0.0/8 is organization-local scope for multicast
    var address = System.Net.IPAddress.Parse(TrustConfig.MulticastGroup);
    Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, address.AddressFamily);
    var bytes = address.GetAddressBytes();
    Assert.Equal(239, bytes[0]); // Organization-local scope
  }

  [Fact]
  public void DiscoveryMagic_IsNotEmpty()
  {
    Assert.False(string.IsNullOrEmpty(TrustConfig.DiscoveryMagic));
    Assert.Equal("FUNNEL_DISCOVER", TrustConfig.DiscoveryMagic);
  }

  [Fact]
  public void Ports_AreDistinct()
  {
    var ports = new[] { TrustConfig.DiscoveryPort, TrustConfig.GrpcPort, TrustConfig.HttpApiPort };
    Assert.Equal(ports.Length, ports.Distinct().Count());
  }
}
