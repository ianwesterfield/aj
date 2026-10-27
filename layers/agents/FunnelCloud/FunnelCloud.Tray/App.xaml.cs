using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using H.NotifyIcon;
using Microsoft.Toolkit.Uwp.Notifications;
using FunnelCloud.Tray.Services;
using FunnelCloud.Tray.ViewModels;

namespace FunnelCloud.Tray;

public partial class App : System.Windows.Application
{
  private TaskbarIcon? _trayIcon;
  private AgentEventService? _eventService;
  private HttpEventListener? _httpListener;
  private EventsWindow? _eventsWindow;

  public static MainViewModel ViewModel { get; private set; } = null!;

  private void Application_Startup(object sender, StartupEventArgs e)
  {
    // Initialize ViewModel
    ViewModel = new MainViewModel();

    // Set up tray icon
    _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
    _trayIcon.DataContext = ViewModel;
    _trayIcon.ForceCreate();

    // Hook up commands that need the window reference
    ViewModel.ShowFlyoutRequested += OnShowFlyoutRequested;
    ViewModel.ExitRequested += OnExitRequested;

    // Start listening to agent events via named pipe (local agent)
    _eventService = new AgentEventService(ViewModel);
    _eventService.Start();

    // Start HTTP listener for events from orchestrator filter (Docker)
    _httpListener = new HttpEventListener(ViewModel);
    _httpListener.Start();

    // Handle toast activation (when user clicks notification)
    ToastNotificationManagerCompat.OnActivated += OnToastActivated;
  }

  private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
  {
    // Bring up the events window when notification is clicked
    Current.Dispatcher.Invoke(OnShowFlyoutRequested);
  }

  private void OnShowFlyoutRequested()
  {
    if (_eventsWindow == null || !_eventsWindow.IsVisible)
    {
      _eventsWindow = new EventsWindow { DataContext = ViewModel };
      PositionWindowAboveTray(_eventsWindow);
      _eventsWindow.Show();

      // Bring to foreground
      var hwnd = new WindowInteropHelper(_eventsWindow).Handle;
      if (hwnd != IntPtr.Zero)
        SetForegroundWindow(hwnd);
    }
    else
    {
      _eventsWindow.Activate();
    }
  }

  private void PositionWindowAboveTray(Window window)
  {
    // Get cursor position (where user clicked the tray icon)
    GetCursorPos(out POINT cursor);

    // Get the working area of the screen containing the cursor
    var screen = Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y));
    var workArea = screen.WorkingArea;

    // Get DPI scale factor
    double dpiScale = GetDpiScale();

    // Convert work area to WPF units
    double workAreaRight = workArea.Right / dpiScale;
    double workAreaBottom = workArea.Bottom / dpiScale;
    double workAreaLeft = workArea.Left / dpiScale;
    double workAreaTop = workArea.Top / dpiScale;

    // Calculate window size
    double windowWidth = window.Width > 0 ? window.Width : 420;
    double windowHeight = window.Height > 0 ? window.Height : 380;

    // Position window: padding from right edge and above taskbar
    double left = workAreaRight - windowWidth - 48;
    double top = workAreaBottom - windowHeight - 12;
    // Clamp to ensure fully on screen
    left = Math.Max(workAreaLeft + 16, left);
    top = Math.Max(workAreaTop + 16, top);

    window.Left = left;
    window.Top = top;
  }

  private double GetDpiScale()
  {
    var source = PresentationSource.FromVisual(Current.MainWindow ?? new Window());
    return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
  }

  private void OnExitRequested()
  {
    _eventService?.Dispose();
    _httpListener?.Dispose();
    _trayIcon?.Dispose();

    // Clean up toast notifications
    ToastNotificationManagerCompat.Uninstall();

    Shutdown();
  }

  public static void ShowToast(string title, string message)
  {
    try
    {
      // Show Windows toast notification (appears in Notification Center)
      // Using customized settings to avoid Windows fallback to MessageBox
      new ToastContentBuilder()
          .AddText(title)
          .AddText(message)
          .Show(toast =>
          {
            toast.Tag = "FunnelCloudEvent";
            toast.Group = "FunnelCloud";
          });
    }
    catch (Exception ex)
    {
      // Log but don't crash - toast registration can fail on some systems
      Console.WriteLine($"[Toast] Failed to show notification: {ex.Message}");
    }
  }

  public static void SetTrayIconColor(string color)
  {
    var app = (App)Current;
    if (app._trayIcon == null) return;

    var iconPath = color switch
    {
      "white" => "/Assets/tray-icon-white.ico",
      "blue" => "/Assets/tray-icon-blue.ico",
      "red" => "/Assets/tray-icon-red.ico",
      "orange" => "/Assets/tray-icon-orange.ico",
      "green" => "/Assets/tray-icon-green.ico",
      _ => "/Assets/tray-icon.ico"
    };

    Current.Dispatcher.Invoke(() =>
    {
      app._trayIcon.IconSource = new System.Windows.Media.Imaging.BitmapImage(
        new Uri($"pack://application:,,,{iconPath}"));
    });
  }

  protected override void OnExit(ExitEventArgs e)
  {
    _eventService?.Dispose();
    _trayIcon?.Dispose();
    base.OnExit(e);
  }

  [DllImport("user32.dll")]
  private static extern bool SetForegroundWindow(IntPtr hWnd);

  [DllImport("user32.dll")]
  private static extern bool GetCursorPos(out POINT lpPoint);

  [StructLayout(LayoutKind.Sequential)]
  private struct POINT
  {
    public int X;
    public int Y;
  }
}
