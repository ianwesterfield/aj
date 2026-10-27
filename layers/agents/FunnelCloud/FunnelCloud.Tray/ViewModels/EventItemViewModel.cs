using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FunnelCloud.Shared.Ipc;
using Wpf.Ui.Controls;

namespace FunnelCloud.Tray.ViewModels;

public partial class EventItemViewModel : ObservableObject
{
  public EventItemViewModel(AgentEvent evt)
  {
    TaskId = evt.TaskId ?? "";
    FullCommand = evt.Command ?? "";
    CommandText = Truncate(evt.Command ?? "(no command)", 45);
    Kind = evt.Kind;
    Timestamp = evt.Timestamp;
    DurationSeconds = evt.DurationSeconds;
    ErrorMessage = evt.ErrorMessage;

    // Capture result output
    Stdout = evt.Stdout;
    Stderr = evt.Stderr;
    ExitCode = evt.ExitCode;

    // Status symbol (Fluent UI icons)
    StatusSymbol = evt.Kind switch
    {
      AgentEventKind.TaskReceived => SymbolRegular.Play24,
      AgentEventKind.TaskCompleted => SymbolRegular.Checkmark24,
      AgentEventKind.TaskFailed => SymbolRegular.Dismiss24,
      _ => SymbolRegular.Info24
    };

    // Status color
    StatusBrush = new SolidColorBrush(evt.Kind switch
    {
      AgentEventKind.TaskReceived => System.Windows.Media.Color.FromRgb(0x60, 0xA5, 0xFA), // Blue
      AgentEventKind.TaskCompleted => System.Windows.Media.Color.FromRgb(0x6C, 0xCB, 0x5F), // Green
      AgentEventKind.TaskFailed => System.Windows.Media.Color.FromRgb(0xF8, 0x71, 0x71), // Red
      _ => Colors.Gray
    });
  }

  public string TaskId { get; }
  public string FullCommand { get; }
  public string CommandText { get; }
  public string Kind { get; }
  public DateTimeOffset Timestamp { get; }
  public double? DurationSeconds { get; }
  public string? ErrorMessage { get; }
  public string? Stdout { get; private set; }
  public string? Stderr { get; private set; }
  public int? ExitCode { get; private set; }

  public SymbolRegular StatusSymbol { get; }
  public SolidColorBrush StatusBrush { get; }

  public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss");

  public string DurationText => DurationSeconds.HasValue
      ? $"{DurationSeconds:F1}s"
      : "";

  /// <summary>Combined result text for display (stdout + stderr)</summary>
  public string ResultText
  {
    get
    {
      if (Kind == AgentEventKind.TaskReceived)
        return "(running...)";

      var parts = new List<string>();
      if (!string.IsNullOrWhiteSpace(Stdout))
        parts.Add(Stdout.Trim());
      if (!string.IsNullOrWhiteSpace(Stderr))
        parts.Add($"[stderr]\n{Stderr.Trim()}");
      if (!string.IsNullOrWhiteSpace(ErrorMessage))
        parts.Add($"[error] {ErrorMessage}");

      return parts.Count > 0 ? string.Join("\n\n", parts) : "(no output)";
    }
  }

  /// <summary>Whether there's a result to show</summary>
  public bool HasResult => Kind != AgentEventKind.TaskReceived;

  /// <summary>Whether the result panel is expanded</summary>
  [ObservableProperty]
  private bool _isExpanded;

  /// <summary>Update this event with result data from a completed event</summary>
  public void UpdateWithResult(AgentEvent evt)
  {
    if (evt.Stdout != null) Stdout = evt.Stdout;
    if (evt.Stderr != null) Stderr = evt.Stderr;
    if (evt.ExitCode != null) ExitCode = evt.ExitCode;
    OnPropertyChanged(nameof(ResultText));
    OnPropertyChanged(nameof(HasResult));
  }

  private static string Truncate(string value, int maxLength)
  {
    if (string.IsNullOrEmpty(value)) return value;
    return value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
  }
}
