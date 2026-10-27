namespace FunnelCloud.Tests;

using FunnelCloud.Agent.Services;
using FunnelCloud.Shared.Contracts;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Tests for PeerRegistry - thread-safe peer management with TTL-based expiration.
/// </summary>
public class PeerRegistryTests
{
  private readonly Mock<ILogger<PeerRegistry>> _loggerMock;
  private readonly AgentCapabilities _selfCapabilities;
  private PeerRegistry _registry;

  public PeerRegistryTests()
  {
    _loggerMock = new Mock<ILogger<PeerRegistry>>();
    _selfCapabilities = CreateCapabilities("self-agent", "192.168.1.1");
    _registry = new PeerRegistry(_loggerMock.Object, _selfCapabilities);
  }

  private static AgentCapabilities CreateCapabilities(string agentId, string ip, string hostname = "test-host")
  {
    return new AgentCapabilities
    {
      AgentId = agentId,
      Hostname = hostname,
      Platform = "windows",
      Capabilities = new[] { "powershell", "dotnet" },
      WorkspaceRoots = new[] { @"C:\Code" },
      CertificateFingerprint = "SHA256:TEST123",
      IpAddress = ip
    };
  }

  #region AddOrUpdate Tests

  [Fact]
  public void AddOrUpdate_NewPeer_AddsToRegistry()
  {
    var peer = CreateCapabilities("peer-1", "192.168.1.10");

    _registry.AddOrUpdate(peer);

    Assert.Equal(1, _registry.Count);
    Assert.True(_registry.Contains("peer-1"));
  }

  [Fact]
  public void AddOrUpdate_SelfPeer_IgnoresSelf()
  {
    _registry.AddOrUpdate(_selfCapabilities);

    Assert.Equal(0, _registry.Count);
    Assert.False(_registry.Contains("self-agent"));
  }

  [Fact]
  public void AddOrUpdate_ExistingPeer_UpdatesEntry()
  {
    var peer1 = CreateCapabilities("peer-1", "192.168.1.10");
    var peer2 = CreateCapabilities("peer-1", "192.168.1.20"); // Same ID, different IP

    _registry.AddOrUpdate(peer1);
    _registry.AddOrUpdate(peer2);

    Assert.Equal(1, _registry.Count);
    var retrieved = _registry.GetPeer("peer-1");
    Assert.NotNull(retrieved);
    Assert.Equal("192.168.1.20", retrieved.IpAddress);
  }

  [Fact]
  public void AddOrUpdate_MultiplePeers_AllAdded()
  {
    _registry.AddOrUpdate(CreateCapabilities("peer-1", "192.168.1.10"));
    _registry.AddOrUpdate(CreateCapabilities("peer-2", "192.168.1.11"));
    _registry.AddOrUpdate(CreateCapabilities("peer-3", "192.168.1.12"));

    Assert.Equal(3, _registry.Count);
  }

  #endregion

  #region MergeGossip Tests

  [Fact]
  public void MergeGossip_NewPeers_ReturnsAddedCount()
  {
    var peers = new[]
    {
            CreateCapabilities("peer-1", "192.168.1.10"),
            CreateCapabilities("peer-2", "192.168.1.11"),
            CreateCapabilities("peer-3", "192.168.1.12")
        };

    var added = _registry.MergeGossip(peers, "gossip-source");

    Assert.Equal(3, added);
    Assert.Equal(3, _registry.Count);
  }

  [Fact]
  public void MergeGossip_ExistingPeers_ReturnsZero()
  {
    var peer = CreateCapabilities("peer-1", "192.168.1.10");
    _registry.AddOrUpdate(peer);

    var added = _registry.MergeGossip(new[] { peer }, "gossip-source");

    Assert.Equal(0, added);
    Assert.Equal(1, _registry.Count);
  }

  [Fact]
  public void MergeGossip_MixedPeers_ReturnsNewCount()
  {
    _registry.AddOrUpdate(CreateCapabilities("existing-peer", "192.168.1.5"));

    var peers = new[]
    {
            CreateCapabilities("existing-peer", "192.168.1.5"),
            CreateCapabilities("new-peer-1", "192.168.1.10"),
            CreateCapabilities("new-peer-2", "192.168.1.11")
        };

    var added = _registry.MergeGossip(peers, "gossip-source");

    Assert.Equal(2, added);
    Assert.Equal(3, _registry.Count);
  }

  [Fact]
  public void MergeGossip_IncludesSelf_IgnoresSelf()
  {
    var peers = new[]
    {
            _selfCapabilities,
            CreateCapabilities("peer-1", "192.168.1.10")
        };

    var added = _registry.MergeGossip(peers, "gossip-source");

    Assert.Equal(1, added);
    Assert.Equal(1, _registry.Count);
    Assert.False(_registry.Contains("self-agent"));
  }

  #endregion

  #region GetAllPeers Tests

  [Fact]
  public void GetAllPeers_EmptyRegistry_ReturnsEmptyList()
  {
    var peers = _registry.GetAllPeers();

    Assert.Empty(peers);
  }

  [Fact]
  public void GetAllPeers_WithPeers_ReturnsAllNonExpired()
  {
    _registry.AddOrUpdate(CreateCapabilities("peer-1", "192.168.1.10"));
    _registry.AddOrUpdate(CreateCapabilities("peer-2", "192.168.1.11"));

    var peers = _registry.GetAllPeers();

    Assert.Equal(2, peers.Count);
    Assert.Contains(peers, p => p.AgentId == "peer-1");
    Assert.Contains(peers, p => p.AgentId == "peer-2");
  }

  #endregion

  #region GetPeer Tests

  [Fact]
  public void GetPeer_ExistingPeer_ReturnsPeer()
  {
    var expected = CreateCapabilities("peer-1", "192.168.1.10");
    _registry.AddOrUpdate(expected);

    var actual = _registry.GetPeer("peer-1");

    Assert.NotNull(actual);
    Assert.Equal("peer-1", actual.AgentId);
    Assert.Equal("192.168.1.10", actual.IpAddress);
  }

  [Fact]
  public void GetPeer_NonExistentPeer_ReturnsNull()
  {
    var result = _registry.GetPeer("non-existent");

    Assert.Null(result);
  }

  #endregion

  #region Contains Tests

  [Fact]
  public void Contains_ExistingPeer_ReturnsTrue()
  {
    _registry.AddOrUpdate(CreateCapabilities("peer-1", "192.168.1.10"));

    Assert.True(_registry.Contains("peer-1"));
  }

  [Fact]
  public void Contains_NonExistentPeer_ReturnsFalse()
  {
    Assert.False(_registry.Contains("non-existent"));
  }

  #endregion

  #region Remove Tests

  [Fact]
  public void Remove_ExistingPeer_RemovesPeer()
  {
    _registry.AddOrUpdate(CreateCapabilities("peer-1", "192.168.1.10"));
    Assert.True(_registry.Contains("peer-1"));

    _registry.Remove("peer-1");

    Assert.False(_registry.Contains("peer-1"));
    Assert.Equal(0, _registry.Count);
  }

  [Fact]
  public void Remove_NonExistentPeer_DoesNotThrow()
  {
    var exception = Record.Exception(() => _registry.Remove("non-existent"));

    Assert.Null(exception);
  }

  #endregion

  #region Touch Tests

  [Fact]
  public void Touch_ExistingPeer_DoesNotThrow()
  {
    _registry.AddOrUpdate(CreateCapabilities("peer-1", "192.168.1.10"));

    var exception = Record.Exception(() => _registry.Touch("peer-1"));

    Assert.Null(exception);
    Assert.True(_registry.Contains("peer-1"));
  }

  [Fact]
  public void Touch_NonExistentPeer_DoesNotThrow()
  {
    var exception = Record.Exception(() => _registry.Touch("non-existent"));

    Assert.Null(exception);
  }

  #endregion

  #region Count Tests

  [Fact]
  public void Count_EmptyRegistry_ReturnsZero()
  {
    Assert.Equal(0, _registry.Count);
  }

  [Fact]
  public void Count_WithPeers_ReturnsCorrectCount()
  {
    _registry.AddOrUpdate(CreateCapabilities("peer-1", "192.168.1.10"));
    _registry.AddOrUpdate(CreateCapabilities("peer-2", "192.168.1.11"));

    Assert.Equal(2, _registry.Count);
  }

  #endregion

  #region Thread Safety Tests

  [Fact]
  public async Task ConcurrentAddOrUpdate_DoesNotThrow()
  {
    var tasks = Enumerable.Range(0, 100)
        .Select(i => Task.Run(() =>
        {
          _registry.AddOrUpdate(CreateCapabilities($"peer-{i}", $"192.168.1.{i % 255}"));
        }));

    await Task.WhenAll(tasks);

    Assert.Equal(100, _registry.Count);
  }

  [Fact]
  public async Task ConcurrentReadWrite_DoesNotThrow()
  {
    // Pre-populate
    for (int i = 0; i < 50; i++)
    {
      _registry.AddOrUpdate(CreateCapabilities($"peer-{i}", $"192.168.1.{i}"));
    }

    var addTasks = Enumerable.Range(50, 50)
        .Select(i => Task.Run(() =>
        {
          _registry.AddOrUpdate(CreateCapabilities($"peer-{i}", $"192.168.1.{i}"));
        }));

    var readTasks = Enumerable.Range(0, 50)
        .Select(i => Task.Run(() =>
        {
          _ = _registry.GetPeer($"peer-{i}");
          _ = _registry.GetAllPeers();
          _ = _registry.Contains($"peer-{i}");
        }));

    await Task.WhenAll(addTasks.Concat(readTasks));

    Assert.Equal(100, _registry.Count);
  }

  #endregion
}
