using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FunnelCloud.Shared.Ipc;
using FunnelCloud.Tray.ViewModels;

namespace FunnelCloud.Tray.Services;

/// <summary>
/// HTTP listener that receives events from the orchestrator filter
/// running in Docker/WSL2. Uses raw TcpListener to bind to 0.0.0.0
/// without requiring admin privileges (unlike HttpListener).
/// </summary>
public sealed class HttpEventListener : IDisposable
{
  private const int Port = 6666;
  private readonly MainViewModel _viewModel;
  private readonly TcpListener _listener;
  private readonly CancellationTokenSource _cts = new();
  private Task? _listenerTask;

  public HttpEventListener(MainViewModel viewModel)
  {
    _viewModel = viewModel;
    // Bind to all interfaces so WSL2/Docker can reach us
    _listener = new TcpListener(IPAddress.Any, Port);
  }

  public void Start()
  {
    try
    {
      _listener.Start();
      _listenerTask = Task.Run(() => ListenLoopAsync(_cts.Token));
      Console.WriteLine($"[HttpEventListener] Listening on 0.0.0.0:{Port}");
      
      // Update status to show we're listening for orchestrator events
      System.Windows.Application.Current.Dispatcher.Invoke(() => _viewModel.SetListening());
    }
    catch (Exception ex)
    {
      Console.WriteLine($"[HttpEventListener] Failed to start: {ex.Message}");
    }
  }

  public void Dispose()
  {
    _cts.Cancel();
    _listener.Stop();
    _listenerTask?.Wait(TimeSpan.FromSeconds(2));
    _cts.Dispose();
  }

  private async Task ListenLoopAsync(CancellationToken ct)
  {
    while (!ct.IsCancellationRequested)
    {
      try
      {
        var client = await _listener.AcceptTcpClientAsync(ct);
        _ = Task.Run(() => HandleClientAsync(client, ct), ct);
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        break;
      }
      catch (SocketException) when (ct.IsCancellationRequested)
      {
        break;
      }
      catch (Exception ex)
      {
        Console.WriteLine($"[HttpEventListener] Accept error: {ex.Message}");
      }
    }
  }

  private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
  {
    using (client)
    {
      try
      {
        using var stream = client.GetStream();
        // Use UTF8 without BOM to avoid corrupting HTTP response
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var reader = new StreamReader(stream, utf8NoBom, leaveOpen: true);
        using var writer = new StreamWriter(stream, utf8NoBom, leaveOpen: true) { NewLine = "\r\n", AutoFlush = false };

        // Read request line
        var requestLine = await reader.ReadLineAsync(ct);
        if (string.IsNullOrEmpty(requestLine)) return;

        var parts = requestLine.Split(' ');
        if (parts.Length < 2) return;

        var method = parts[0];
        var path = parts[1];

        // Read headers
        int contentLength = 0;
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(ct)))
        {
          if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
          {
            int.TryParse(line.Substring(15).Trim(), out contentLength);
          }
        }

        // Read body if present
        string body = "";
        if (contentLength > 0)
        {
          var buffer = new char[contentLength];
          await reader.ReadBlockAsync(buffer, 0, contentLength);
          body = new string(buffer);
        }

        // Handle request
        string responseBody = "";
        int statusCode = 200;

        if (method == "POST" && path == "/event")
        {
          try
          {
            var evt = JsonSerializer.Deserialize<AgentEvent>(body, AgentEventSerializer.Options);
            if (evt != null)
            {
              System.Windows.Application.Current?.Dispatcher.Invoke(() =>
              {
                _viewModel.AddEvent(evt);
                _viewModel.SetConnectedViaOrchestrator();
              });
              Console.WriteLine($"[HttpEventListener] Received event: {evt.Kind} - {evt.Command?.Substring(0, Math.Min(50, evt.Command?.Length ?? 0))}");
            }
            responseBody = "OK";
          }
          catch (Exception ex)
          {
            statusCode = 400;
            responseBody = ex.Message;
          }
        }
        else if (method == "GET" && path == "/health")
        {
          responseBody = "OK";
        }
        else
        {
          statusCode = 404;
          responseBody = "Not Found";
        }

        // Write response
        await writer.WriteLineAsync($"HTTP/1.1 {statusCode} {(statusCode == 200 ? "OK" : "Error")}");
        await writer.WriteLineAsync("Content-Type: text/plain");
        await writer.WriteLineAsync($"Content-Length: {Encoding.UTF8.GetByteCount(responseBody)}");
        await writer.WriteLineAsync("Connection: close");
        await writer.WriteLineAsync();
        await writer.WriteAsync(responseBody);
        await writer.FlushAsync();
      }
      catch (Exception ex)
      {
        Console.WriteLine($"[HttpEventListener] Request error: {ex.Message}");
      }
    }
  }
}
