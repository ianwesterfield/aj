using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using FunnelCloud.Shared.Ipc;
using FunnelCloud.Tray.ViewModels;

namespace FunnelCloud.Tray.Services;

/// <summary>
/// Connects to the agent's named pipe and streams events to the view model.
/// Automatically reconnects on disconnect with exponential backoff.
/// </summary>
public sealed class AgentEventService : IDisposable
{
  private readonly MainViewModel _viewModel;
  private readonly CancellationTokenSource _cts = new();
  private Task? _connectionTask;

  public AgentEventService(MainViewModel viewModel)
  {
    _viewModel = viewModel;
  }

  public void Start()
  {
    _connectionTask = Task.Run(() => ConnectionLoopAsync(_cts.Token));
  }

  public void Dispose()
  {
    _cts.Cancel();
    _connectionTask?.Wait(TimeSpan.FromSeconds(2));
    _cts.Dispose();
  }

  private async Task ConnectionLoopAsync(CancellationToken ct)
  {
    var backoff = TimeSpan.FromSeconds(1);
    var maxBackoff = TimeSpan.FromSeconds(30);

    while (!ct.IsCancellationRequested)
    {
      try
      {
        await ConnectAndStreamAsync(ct);
        // Reset backoff on successful connection
        backoff = TimeSpan.FromSeconds(1);
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        break;
      }
      catch
      {
        // Connection failed or lost
        UpdateStatus(() => _viewModel.SetReconnecting());
      }

      // Wait before reconnect
      try
      {
        await Task.Delay(backoff, ct);
        backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 1.5, maxBackoff.TotalSeconds));
      }
      catch (OperationCanceledException)
      {
        break;
      }
    }

    UpdateStatus(() => _viewModel.SetDisconnected());
  }

  private async Task ConnectAndStreamAsync(CancellationToken ct)
  {
    using var pipe = new NamedPipeClientStream(
        ".",
        AgentEventSerializer.PipeName,
        PipeDirection.InOut,
        PipeOptions.Asynchronous);

    // Connect with timeout
    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    connectCts.CancelAfter(TimeSpan.FromSeconds(5));

    try
    {
      await pipe.ConnectAsync(connectCts.Token);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
      // Connect timeout, not shutdown
      throw new TimeoutException("Connection timed out");
    }

    UpdateStatus(() => _viewModel.SetConnected());

    // Use UTF8 without BOM - the agent expects plain JSON without BOM prefix
    var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    var reader = new StreamReader(pipe, utf8NoBom);
    var writer = new StreamWriter(pipe, utf8NoBom) { AutoFlush = true };

    // Subscribe with replay
    var request = new SubscribeRequest { Replay = 10, OneShot = false };
    await writer.WriteLineAsync(AgentEventSerializer.SerializeSubscribe(request));

    // Stream events
    while (!ct.IsCancellationRequested)
    {
      var line = await reader.ReadLineAsync(ct);
      if (line == null) break; // Pipe closed

      var evt = AgentEventSerializer.Deserialize(line);
      if (evt != null)
      {
        AddEvent(evt);
      }
    }
  }

  private void UpdateStatus(Action action)
  {
    System.Windows.Application.Current?.Dispatcher.Invoke(action);
  }

  private void AddEvent(AgentEvent evt)
  {
    System.Windows.Application.Current?.Dispatcher.Invoke(() => _viewModel.AddEvent(evt));
  }
}
