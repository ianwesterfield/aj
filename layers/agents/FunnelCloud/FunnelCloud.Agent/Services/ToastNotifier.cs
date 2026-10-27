using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace FunnelCloud.Agent.Services;

/// <summary>
/// Shows a notification to the interactive desktop user when the agent receives a task.
/// Works from Session 0 (LocalSystem service) by using the WTSSendMessage Win32 API,
/// which is specifically designed for services to display messages in user sessions.
/// No-op on non-Windows platforms (e.g., Linux orchestrator sidecar).
/// </summary>
public static class ToastNotifier
{
  // ── Win32 P/Invoke ──────────────────────────────────────────────────
  private const int WTS_CURRENT_SERVER_HANDLE = 0;

  [DllImport("kernel32.dll")]
  private static extern uint WTSGetActiveConsoleSessionId();

  [DllImport("wtsapi32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool WTSSendMessageW(
      IntPtr hServer,
      int sessionId,
      [MarshalAs(UnmanagedType.LPWStr)] string pTitle,
      int titleLength,      // in bytes
      [MarshalAs(UnmanagedType.LPWStr)] string pMessage,
      int messageLength,    // in bytes
      int style,
      int timeout,          // seconds — 0 = wait forever
      out int pResponse,
      [MarshalAs(UnmanagedType.Bool)] bool bWait);

  // MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND
  private const int MB_OK = 0x00000000;
  private const int MB_ICONINFORMATION = 0x00000040;
  private const int MB_SETFOREGROUND = 0x00010000;
  private const int MessageStyle = MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND;

  // Auto-dismiss after this many seconds so they don't pile up
  private const int TimeoutSeconds = 10;

  /// <summary>
  /// Show a message box on the interactive desktop with the task details.
  /// Fire-and-forget — runs on a background thread, never blocks the caller, never throws.
  /// </summary>
  public static void NotifyTaskReceived(string taskId, string taskType, string command, ILogger? logger = null)
  {
    if (!OperatingSystem.IsWindows())
      return;

    // Capture values and run async so we never block the gRPC call
    _ = Task.Run(() =>
    {
      try
      {
        var rawSessionId = WTSGetActiveConsoleSessionId();
        if (rawSessionId == 0xFFFFFFFF)
        {
          logger?.LogDebug("No active console session found — skipping notification");
          return;
        }
        var sessionId = (int)rawSessionId;

        var truncatedCmd = command.Length > 150 ? command[..150] + "..." : command;
        var title = $"FunnelCloud Task — {taskType}";
        var message = $"Task ID: {taskId}\n\n{truncatedCmd}";

        // Byte lengths for the Unicode strings (2 bytes per char)
        var titleBytes = title.Length * 2;
        var messageBytes = message.Length * 2;

        var sent = WTSSendMessageW(
            IntPtr.Zero,          // WTS_CURRENT_SERVER_HANDLE
            sessionId,
            title, titleBytes,
            message, messageBytes,
            MessageStyle,
            TimeoutSeconds,
            out _,
            false);                // bWait=false → don't block the calling thread

        if (!sent)
        {
          var error = Marshal.GetLastWin32Error();
          logger?.LogDebug("WTSSendMessage failed (Win32 error {Error}) — non-critical", error);
        }
      }
      catch (Exception ex)
      {
        // Never let notification failures affect task execution
        logger?.LogDebug(ex, "Toast notification failed (non-critical)");
      }
    });
  }
}
