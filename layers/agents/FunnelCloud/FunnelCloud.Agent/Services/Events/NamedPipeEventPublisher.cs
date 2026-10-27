using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using FunnelCloud.Shared.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FunnelCloud.Agent.Services.Events;

/// <summary>
/// Hosted service that owns a named pipe server and broadcasts
/// <see cref="AgentEvent"/> instances as newline-delimited JSON to every
/// connected subscriber.
///
/// Protocol (newline-delimited JSON, bidirectional):
///   1. Subscriber connects.
///   2. Subscriber sends one <see cref="SubscribeRequest"/> line.
///   3. Server replays up to <c>replay</c> recent events from its in-memory
///      ring buffer.
///   4. If <c>one_shot=false</c>, the server keeps the connection open and
///      streams live events until the client disconnects.
///
/// Cross-platform: Windows named pipe on Windows; Unix domain socket
/// (<c>/tmp/CoreFxPipe_&lt;name&gt;</c>) on Linux/macOS.
/// </summary>
public sealed class NamedPipeEventPublisher : BackgroundService, IAgentEventPublisher
{
  private const int MaxServerInstances = 8;
  private const int RingBufferSize = 50;

  private readonly ILogger<NamedPipeEventPublisher> _logger;
  private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
  private readonly object _ringLock = new();
  private readonly LinkedList<AgentEvent> _ring = new();
  private int _isFirstInstance = 1; // 1 = first instance not yet created

  public NamedPipeEventPublisher(ILogger<NamedPipeEventPublisher> logger)
  {
    _logger = logger;
  }

  public void Publish(AgentEvent evt)
  {
    _logger.LogInformation("Publishing event: kind={Kind}, taskId={TaskId}, command={Command}",
        evt.Kind, evt.TaskId, evt.Command?.Substring(0, Math.Min(50, evt.Command?.Length ?? 0)));

    // 1. Persist to ring buffer so future subscribers can replay it.
    lock (_ringLock)
    {
      _ring.AddLast(evt);
      while (_ring.Count > RingBufferSize)
        _ring.RemoveFirst();
      _logger.LogDebug("Ring buffer size: {Count}", _ring.Count);
    }

    // 2. Broadcast to live subscribers.
    if (_subscribers.IsEmpty)
    {
      _logger.LogDebug("No live subscribers to broadcast to");
      return;
    }

    string line;
    try
    {
      line = AgentEventSerializer.Serialize(evt) + "\n";
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to serialize agent event (kind={Kind})", evt.Kind);
      return;
    }

    foreach (var kvp in _subscribers)
      _ = kvp.Value.WriteAsync(line, _logger);
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    _logger.LogInformation(
        "NamedPipeEventPublisher starting on pipe '{Pipe}' (max {Max} subscribers, ring size {Ring})",
        AgentEventSerializer.PipeName, MaxServerInstances, RingBufferSize);

    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await AcceptOneSubscriberAsync(stoppingToken);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        break;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "NamedPipeEventPublisher accept loop error; retrying in 2s");
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
        catch (OperationCanceledException) { break; }
      }
    }

    foreach (var kvp in _subscribers.ToArray())
      kvp.Value.Dispose();
    _subscribers.Clear();
  }

  private async Task AcceptOneSubscriberAsync(CancellationToken ct)
  {
    NamedPipeServerStream server = CreateServer();
    try
    {
      await server.WaitForConnectionAsync(ct);
    }
    catch
    {
      server.Dispose();
      throw;
    }

    // Handle on a background task so we can loop back and accept the next one.
    _ = Task.Run(() => HandleSubscriberAsync(server, ct), ct);
  }

  private async Task HandleSubscriberAsync(NamedPipeServerStream server, CancellationToken ct)
  {
    var id = Guid.NewGuid();
    var reader = new StreamReader(server, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
    var writer = new StreamWriter(server, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true) { AutoFlush = false };

    SubscribeRequest req;
    try
    {
      using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
      readCts.CancelAfter(TimeSpan.FromSeconds(5));
      var line = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
      if (line is null)
      {
        _logger.LogDebug("Subscriber {Id} disconnected before sending request", id);
        CleanupPipe(server, reader, writer);
        return;
      }

      req = AgentEventSerializer.DeserializeSubscribe(line) ?? new SubscribeRequest();
    }
    catch (OperationCanceledException)
    {
      _logger.LogDebug("Subscriber {Id} did not send subscribe request in time", id);
      CleanupPipe(server, reader, writer);
      return;
    }
    catch (Exception ex)
    {
      _logger.LogDebug(ex, "Failed to read subscribe request from {Id}", id);
      CleanupPipe(server, reader, writer);
      return;
    }

    _logger.LogInformation(
        "Subscriber {Id} connected: replay={Replay}, one_shot={OneShot}",
        id, req.Replay, req.OneShot);

    var replayCount = Math.Clamp(req.Replay, 0, AgentEventSerializer.MaxReplay);
    AgentEvent[] replay;
    lock (_ringLock)
    {
      _logger.LogInformation("Ring buffer has {Count} events, replaying up to {Replay}",
          _ring.Count, replayCount);
      replay = _ring.Count <= replayCount
          ? _ring.ToArray()
          : _ring.Skip(_ring.Count - replayCount).ToArray();
    }

    try
    {
      foreach (var evt in replay)
        await writer.WriteAsync(AgentEventSerializer.Serialize(evt) + "\n").ConfigureAwait(false);
      await writer.FlushAsync().ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
    {
      _logger.LogDebug("Subscriber {Id} disconnected during replay: {Message}", id, ex.Message);
      CleanupPipe(server, reader, writer);
      return;
    }

    if (req.OneShot)
    {
      _logger.LogDebug("Subscriber {Id} one-shot complete; closing", id);
      CleanupPipe(server, reader, writer);
      return;
    }

    var sub = new Subscriber(id, server, reader, writer, OnSubscriberClosed);
    if (!_subscribers.TryAdd(id, sub))
    {
      sub.Dispose();
      return;
    }

    _logger.LogInformation("Subscriber {Id} live; total={Total}", id, _subscribers.Count);
  }

  private static void CleanupPipe(NamedPipeServerStream server, StreamReader reader, StreamWriter writer)
  {
    try { reader.Dispose(); } catch { }
    try { writer.Dispose(); } catch { }
    try { server.Dispose(); } catch { }
  }

  private void OnSubscriberClosed(Guid id)
  {
    if (_subscribers.TryRemove(id, out var sub))
    {
      sub.Dispose();
      _logger.LogInformation("Subscriber {Id} disconnected; total={Total}", id, _subscribers.Count);
    }
  }

  private NamedPipeServerStream CreateServer()
  {
    // On Windows, only the first server instance can set the security descriptor.
    // Subsequent instances must use the basic constructor without security.
    bool isFirst = Interlocked.Exchange(ref _isFirstInstance, 0) == 1;

    if (OperatingSystem.IsWindows())
      return CreateWindowsServer(isFirst);

    return new NamedPipeServerStream(
        AgentEventSerializer.PipeName,
        PipeDirection.InOut,
        MaxServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);
  }

  [SupportedOSPlatform("windows")]
  private NamedPipeServerStream CreateWindowsServer(bool isFirstInstance)
  {
    // Only set security on the first instance - subsequent instances inherit it
    if (!isFirstInstance)
    {
      return new NamedPipeServerStream(
          AgentEventSerializer.PipeName,
          PipeDirection.InOut,
          MaxServerInstances,
          PipeTransmissionMode.Byte,
          PipeOptions.Asynchronous,
          inBufferSize: 4096,
          outBufferSize: 64 * 1024);
    }

    _logger.LogDebug("Creating first pipe instance with security descriptor");
    var security = new PipeSecurity();
    // Owner (whoever creates the pipe) gets full control
    var currentUser = WindowsIdentity.GetCurrent().User;
    if (currentUser != null)
    {
      security.AddAccessRule(new PipeAccessRule(
          currentUser,
          PipeAccessRights.FullControl,
          AccessControlType.Allow));
    }
    // Allow authenticated users (interactive sessions) to connect
    var authUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
    security.AddAccessRule(new PipeAccessRule(
        authUsers,
        PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
        AccessControlType.Allow));
    // LocalSystem (service account) gets full control
    var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    security.AddAccessRule(new PipeAccessRule(
        localSystem,
        PipeAccessRights.FullControl,
        AccessControlType.Allow));

    return NamedPipeServerStreamAcl.Create(
        AgentEventSerializer.PipeName,
        PipeDirection.InOut,
        MaxServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
        inBufferSize: 4096,
        outBufferSize: 64 * 1024,
        pipeSecurity: security);
  }

  private sealed class Subscriber : IDisposable
  {
    private readonly Guid _id;
    private readonly NamedPipeServerStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly Action<Guid> _onClosed;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposed;

    public Subscriber(Guid id, NamedPipeServerStream pipe, StreamReader reader, StreamWriter writer, Action<Guid> onClosed)
    {
      _id = id;
      _pipe = pipe;
      _reader = reader;
      _writer = writer;
      _onClosed = onClosed;
    }

    public async Task WriteAsync(string line, ILogger logger)
    {
      if (Volatile.Read(ref _disposed) != 0) return;

      await _writeLock.WaitAsync().ConfigureAwait(false);
      try
      {
        if (!_pipe.IsConnected) { Close(); return; }

        await _writer.WriteAsync(line).ConfigureAwait(false);
        await _writer.FlushAsync().ConfigureAwait(false);
      }
      catch (Exception ex) when (ex is IOException or ObjectDisposedException)
      {
        logger.LogDebug("Subscriber {Id} write failed; dropping: {Message}", _id, ex.Message);
        Close();
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Unexpected error writing to subscriber {Id}; dropping", _id);
        Close();
      }
      finally
      {
        _writeLock.Release();
      }
    }

    private void Close()
    {
      if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
      _onClosed(_id);
    }

    public void Dispose()
    {
      Interlocked.Exchange(ref _disposed, 1);
      try { _reader.Dispose(); } catch { }
      try { _writer.Dispose(); } catch { }
      try { _pipe.Dispose(); } catch { }
      _writeLock.Dispose();
    }
  }
}
