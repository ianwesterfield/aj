namespace FunnelCloud.Agent;

/// <summary>
/// Trust constants embedded at build time.
/// DO NOT modify manually - regenerate certificates using scripts/New-CACertificate.ps1
/// </summary>
public static class TrustConfig
{
  /// <summary>
  /// SHA256 fingerprint of the FunnelCloud CA certificate.
  /// Used for certificate pinning - agents will only trust orchestrators
  /// presenting certificates signed by this CA.
  /// </summary>
  public const string CaFingerprint = "SHA256:1584BAFABC7E459886BCB1705AA8788442DB8A7D996F51D11D957D7E372B230F";

  /// <summary>
  /// UDP port for agent discovery.
  /// </summary>
  public const int DiscoveryPort = 41420;

  /// <summary>
  /// Multicast group address for discovery.
  /// 239.255.77.77 is in the organization-local scope (239.0.0.0/8).
  /// Can be routed across VLANs with proper IGMP snooping/PIM configuration.
  /// </summary>
  public const string MulticastGroup = "239.255.77.77";

  /// <summary>
  /// gRPC port for task execution (mTLS).
  /// </summary>
  public const int GrpcPort = 41235;

  /// <summary>
  /// HTTP API port for health checks and peer discovery proxy.
  /// </summary>
  public const int HttpApiPort = 41421;

  /// <summary>
  /// Magic string for discovery protocol identification.
  /// </summary>
  public const string DiscoveryMagic = "FUNNEL_DISCOVER";
}
