using System.CommandLine;
using System.IO.Pipes;
using System.Text;
using FunnelCloud.Shared.Ipc;
using Spectre.Console;

namespace FunnelCloud.Cli;

/// <summary>
/// FunnelCloud CLI - Subscribe to agent events via named pipe.
/// 
/// Usage:
///   funnel list [--count N]     Show last N completed commands (default 10)
///   funnel tail [--count N]     Stream live events (replay last N first)
///   funnel show &lt;task-id&gt;       Show details for a specific task
///   funnel status               Show agent connection status
/// </summary>
public static class Program
{
  public static async Task<int> Main(string[] args)
  {
    var rootCommand = new RootCommand("FunnelCloud Agent CLI - view task execution events");

    // list command
    var listCountOption = new Option<int>("--count", () => 10, "Number of recent events to show");
    listCountOption.AddAlias("-n");
    var listCommand = new Command("list", "Show recent task events") { listCountOption };
    listCommand.SetHandler(async (count) => await ListEventsAsync(count), listCountOption);

    // tail command
    var tailCountOption = new Option<int>("--count", () => 5, "Number of events to replay before streaming");
    tailCountOption.AddAlias("-n");
    var tailCommand = new Command("tail", "Stream live events") { tailCountOption };
    tailCommand.SetHandler(async (count) => await TailEventsAsync(count), tailCountOption);

    // show command
    var taskIdArg = new Argument<string>("task-id", "The task ID to show details for");
    var showCommand = new Command("show", "Show details for a specific task") { taskIdArg };
    showCommand.SetHandler(async (taskId) => await ShowTaskAsync(taskId), taskIdArg);

    // status command
    var statusCommand = new Command("status", "Check agent connection status");
    statusCommand.SetHandler(async () => await StatusAsync());

    rootCommand.AddCommand(listCommand);
    rootCommand.AddCommand(tailCommand);
    rootCommand.AddCommand(showCommand);
    rootCommand.AddCommand(statusCommand);

    return await rootCommand.InvokeAsync(args);
  }

  private static async Task ListEventsAsync(int count)
  {
    try
    {
      using var pipe = CreatePipeClient();
      await ConnectWithTimeoutAsync(pipe, TimeSpan.FromSeconds(2));

      // Use UTF8 without BOM - the agent expects plain JSON
      var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
      var reader = new StreamReader(pipe, utf8NoBom);
      var writer = new StreamWriter(pipe, utf8NoBom) { AutoFlush = true };

      // Send subscribe request with one_shot=true
      var request = new SubscribeRequest { Replay = count, OneShot = true };
      await writer.WriteLineAsync(AgentEventSerializer.SerializeSubscribe(request));

      // Read and display events
      var events = new List<AgentEvent>();
      string? line;
      while ((line = await reader.ReadLineAsync()) != null)
      {
        var evt = AgentEventSerializer.Deserialize(line);
        if (evt != null) events.Add(evt);
      }

      if (events.Count == 0)
      {
        AnsiConsole.MarkupLine("[yellow]No recent events found.[/]");
        return;
      }

      RenderEventTable(events);
    }
    catch (TimeoutException)
    {
      AnsiConsole.MarkupLine("[red]Could not connect to agent. Is FunnelCloud Agent running?[/]");
    }
    catch (Exception ex)
    {
      AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
    }
  }

  private static async Task TailEventsAsync(int replayCount)
  {
    try
    {
      using var pipe = CreatePipeClient();
      await ConnectWithTimeoutAsync(pipe, TimeSpan.FromSeconds(2));

      // Use UTF8 without BOM - the agent expects plain JSON
      var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
      var reader = new StreamReader(pipe, utf8NoBom);
      var writer = new StreamWriter(pipe, utf8NoBom) { AutoFlush = true };

      // Send subscribe request with one_shot=false for streaming
      var request = new SubscribeRequest { Replay = replayCount, OneShot = false };
      await writer.WriteLineAsync(AgentEventSerializer.SerializeSubscribe(request));

      AnsiConsole.MarkupLine("[dim]Streaming events (Ctrl+C to stop)...[/]");
      AnsiConsole.WriteLine();

      using var cts = new CancellationTokenSource();
      Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

      try
      {
        while (!cts.Token.IsCancellationRequested)
        {
          var line = await reader.ReadLineAsync(cts.Token);
          if (line == null) break;

          var evt = AgentEventSerializer.Deserialize(line);
          if (evt != null) RenderSingleEvent(evt);
        }
      }
      catch (OperationCanceledException) { }

      AnsiConsole.WriteLine();
      AnsiConsole.MarkupLine("[dim]Stopped.[/]");
    }
    catch (TimeoutException)
    {
      AnsiConsole.MarkupLine("[red]Could not connect to agent. Is FunnelCloud Agent running?[/]");
    }
    catch (Exception ex)
    {
      AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
    }
  }

  private static async Task ShowTaskAsync(string taskId)
  {
    try
    {
      using var pipe = CreatePipeClient();
      await ConnectWithTimeoutAsync(pipe, TimeSpan.FromSeconds(2));

      // Use UTF8 without BOM - the agent expects plain JSON
      var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
      var reader = new StreamReader(pipe, utf8NoBom);
      var writer = new StreamWriter(pipe, utf8NoBom) { AutoFlush = true };

      // Request replay of recent events to find the task
      var request = new SubscribeRequest { Replay = AgentEventSerializer.MaxReplay, OneShot = true };
      await writer.WriteLineAsync(AgentEventSerializer.SerializeSubscribe(request));

      var taskEvents = new List<AgentEvent>();
      string? line;
      while ((line = await reader.ReadLineAsync()) != null)
      {
        var evt = AgentEventSerializer.Deserialize(line);
        if (evt?.TaskId?.Equals(taskId, StringComparison.OrdinalIgnoreCase) == true)
          taskEvents.Add(evt);
      }

      if (taskEvents.Count == 0)
      {
        AnsiConsole.MarkupLine($"[yellow]No events found for task '{taskId}'.[/]");
        return;
      }

      RenderTaskDetails(taskEvents);
    }
    catch (TimeoutException)
    {
      AnsiConsole.MarkupLine("[red]Could not connect to agent. Is FunnelCloud Agent running?[/]");
    }
    catch (Exception ex)
    {
      AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
    }
  }

  private static async Task StatusAsync()
  {
    try
    {
      using var pipe = CreatePipeClient();
      await ConnectWithTimeoutAsync(pipe, TimeSpan.FromSeconds(2));

      AnsiConsole.MarkupLine("[green]✓[/] Agent is [green]running[/]");
      AnsiConsole.MarkupLine($"[dim]Pipe: {AgentEventSerializer.PipeName}[/]");
    }
    catch (TimeoutException)
    {
      AnsiConsole.MarkupLine("[red]✗[/] Agent is [red]not responding[/]");
      AnsiConsole.MarkupLine($"[dim]Pipe: {AgentEventSerializer.PipeName}[/]");
    }
    catch (Exception ex)
    {
      AnsiConsole.MarkupLine($"[red]✗[/] Error: {ex.Message}");
    }
  }

  private static NamedPipeClientStream CreatePipeClient()
  {
    return new NamedPipeClientStream(
        ".",
        AgentEventSerializer.PipeName,
        PipeDirection.InOut,
        PipeOptions.Asynchronous);
  }

  private static async Task ConnectWithTimeoutAsync(NamedPipeClientStream pipe, TimeSpan timeout)
  {
    using var cts = new CancellationTokenSource(timeout);
    try
    {
      await pipe.ConnectAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
      throw new TimeoutException("Connection timed out");
    }
  }

  private static void RenderEventTable(List<AgentEvent> events)
  {
    var table = new Table();
    table.Border(TableBorder.Rounded);
    table.AddColumn(new TableColumn("Time").Centered());
    table.AddColumn(new TableColumn("Status").Centered());
    table.AddColumn(new TableColumn("Task ID"));
    table.AddColumn(new TableColumn("Command"));
    table.AddColumn(new TableColumn("Duration").RightAligned());

    foreach (var evt in events.OrderByDescending(e => e.Timestamp))
    {
      var time = evt.Timestamp.ToLocalTime().ToString("HH:mm:ss");
      var status = evt.Kind switch
      {
        AgentEventKind.TaskReceived => "[blue]⏳ received[/]",
        AgentEventKind.TaskCompleted => "[green]✓ completed[/]",
        AgentEventKind.TaskFailed => "[red]✗ failed[/]",
        AgentEventKind.AgentStatus => "[dim]status[/]",
        _ => evt.Kind
      };
      var taskId = Truncate(evt.TaskId ?? "-", 12);
      var command = Truncate(evt.Command ?? "-", 40);
      var duration = evt.DurationSeconds.HasValue
          ? $"{evt.DurationSeconds:F1}s"
          : "-";

      table.AddRow(time, status, taskId, Markup.Escape(command), duration);
    }

    AnsiConsole.Write(table);
  }

  private static void RenderSingleEvent(AgentEvent evt)
  {
    var time = evt.Timestamp.ToLocalTime().ToString("HH:mm:ss");
    var (icon, color) = evt.Kind switch
    {
      AgentEventKind.TaskReceived => ("⏳", "blue"),
      AgentEventKind.TaskCompleted => ("✓", "green"),
      AgentEventKind.TaskFailed => ("✗", "red"),
      _ => ("•", "dim")
    };

    var command = Truncate(evt.Command ?? "", 60);
    var duration = evt.DurationSeconds.HasValue ? $" ({evt.DurationSeconds:F1}s)" : "";

    AnsiConsole.MarkupLine($"[dim]{time}[/] [{color}]{icon}[/] {Markup.Escape(command)}{duration}");

    // Show error details for failed tasks
    if (evt.Kind == AgentEventKind.TaskFailed && !string.IsNullOrEmpty(evt.ErrorMessage))
    {
      AnsiConsole.MarkupLine($"       [red]{Markup.Escape(evt.ErrorMessage)}[/]");
    }
  }

  private static void RenderTaskDetails(List<AgentEvent> events)
  {
    var latest = events.OrderByDescending(e => e.Timestamp).First();

    var panel = new Panel(new Rows(
        new Markup($"[bold]Task ID:[/] {latest.TaskId}"),
        new Markup($"[bold]Type:[/] {latest.TaskType}"),
        new Markup($"[bold]Agent:[/] {latest.AgentId}"),
        new Markup($"[bold]Command:[/] {Markup.Escape(latest.Command ?? "-")}"),
        new Text(""),
        new Markup($"[bold]Status:[/] {FormatKind(latest.Kind)}"),
        latest.ExitCode.HasValue
            ? new Markup($"[bold]Exit Code:[/] {latest.ExitCode}")
            : new Text(""),
        latest.DurationSeconds.HasValue
            ? new Markup($"[bold]Duration:[/] {latest.DurationSeconds:F2}s")
            : new Text("")
    ));
    panel.Header = new PanelHeader("Task Details");
    panel.Border = BoxBorder.Rounded;
    AnsiConsole.Write(panel);

    // Show stdout if present
    if (!string.IsNullOrWhiteSpace(latest.Stdout))
    {
      AnsiConsole.WriteLine();
      AnsiConsole.Write(new Panel(new Text(latest.Stdout))
      {
        Header = new PanelHeader("[green]stdout[/]"),
        Border = BoxBorder.Rounded
      });
    }

    // Show stderr if present
    if (!string.IsNullOrWhiteSpace(latest.Stderr))
    {
      AnsiConsole.WriteLine();
      AnsiConsole.Write(new Panel(new Text(latest.Stderr))
      {
        Header = new PanelHeader("[red]stderr[/]"),
        Border = BoxBorder.Rounded
      });
    }

    // Show event timeline
    if (events.Count > 1)
    {
      AnsiConsole.WriteLine();
      AnsiConsole.MarkupLine("[bold]Timeline:[/]");
      foreach (var evt in events.OrderBy(e => e.Timestamp))
      {
        var time = evt.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
        AnsiConsole.MarkupLine($"  [dim]{time}[/] {FormatKind(evt.Kind)}");
      }
    }
  }

  private static string FormatKind(string kind) => kind switch
  {
    AgentEventKind.TaskReceived => "[blue]received[/]",
    AgentEventKind.TaskCompleted => "[green]completed[/]",
    AgentEventKind.TaskFailed => "[red]failed[/]",
    AgentEventKind.AgentStatus => "[dim]status[/]",
    _ => kind
  };

  private static string Truncate(string value, int maxLength)
  {
    if (string.IsNullOrEmpty(value)) return value;
    return value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
  }
}
