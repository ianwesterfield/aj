using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FunnelCloud.Shared.Ipc;

namespace FunnelCloud.Tray.ViewModels;

public partial class MainViewModel : ObservableObject
{
  private const int MaxEvents = 15;

  [ObservableProperty]
  private string _statusText = "Connecting...";

  [ObservableProperty]
  private SolidColorBrush _statusColor = new(Colors.Orange);

  [ObservableProperty]
  private int _completedCount;

  [ObservableProperty]
  private int _failedCount;

  public ObservableCollection<EventItemViewModel> Events { get; } = [];

  public event Action? ShowFlyoutRequested;
  public event Action? ExitRequested;

  public void SetConnected()
  {
    StatusText = "Connected";
    StatusColor = new SolidColorBrush(Colors.LimeGreen);
    App.SetTrayIconColor("green");
  }

  public void SetConnectedViaOrchestrator()
  {
    // Only update if not already connected to a local agent
    if (StatusText != "Connected")
    {
      StatusText = "Via Orchestrator";
      StatusColor = new SolidColorBrush(Colors.DeepSkyBlue);
      App.SetTrayIconColor("blue");
    }
  }

  public void SetDisconnected()
  {
    StatusText = "Disconnected";
    StatusColor = new SolidColorBrush(Colors.Gray);
    App.SetTrayIconColor("red");
  }

  public void SetReconnecting()
  {
    // Don't override Listening or Via Orchestrator - HTTP listener is primary
    if (StatusText != "Listening" && StatusText != "Via Orchestrator" && StatusText != "Connected")
    {
      StatusText = "Reconnecting...";
      StatusColor = new SolidColorBrush(Colors.Orange);
      App.SetTrayIconColor("orange");
    }
  }

  public void SetListening()
  {
    // Only update if not already connected to local agent
    if (StatusText != "Connected")
    {
      StatusText = "Listening";
      StatusColor = new SolidColorBrush(Colors.DeepSkyBlue);
      App.SetTrayIconColor("blue");
    }
  }

  public void AddEvent(AgentEvent evt)
  {
    var item = new EventItemViewModel(evt);

    // Insert at top
    Events.Insert(0, item);

    // Trim to max
    while (Events.Count > MaxEvents)
      Events.RemoveAt(Events.Count - 1);

    // Update counters
    if (evt.Kind == AgentEventKind.TaskCompleted)
      CompletedCount++;
    else if (evt.Kind == AgentEventKind.TaskFailed)
      FailedCount++;

    // Show toast for completed/failed tasks (not received)
    if (evt.Kind == AgentEventKind.TaskCompleted)
    {
      var duration = evt.DurationSeconds.HasValue ? $" ({evt.DurationSeconds:F1}s)" : "";
      App.ShowToast("Task Completed", $"{Truncate(evt.Command ?? "Task", 50)}{duration}");
    }
    else if (evt.Kind == AgentEventKind.TaskFailed)
    {
      App.ShowToast("Task Failed", evt.ErrorMessage ?? evt.Command ?? "Unknown error");
    }
  }

  [RelayCommand]
  private void ShowFlyout() => ShowFlyoutRequested?.Invoke();

  [RelayCommand]
  private void Exit() => ExitRequested?.Invoke();

  private static string Truncate(string value, int maxLength)
  {
    if (string.IsNullOrEmpty(value)) return value;
    return value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
  }
}
