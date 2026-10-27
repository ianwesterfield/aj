namespace FunnelCloud.Tests;

using FunnelCloud.Agent.Services;
using FunnelCloud.Shared.Contracts;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Tests for TaskExecutor - task execution engine for FunnelCloud agents.
/// </summary>
public class TaskExecutorTests : IDisposable
{
  private readonly Mock<ILogger<TaskExecutor>> _loggerMock;
  private readonly TaskExecutor _executor;
  private readonly string _testDir;
  private readonly List<string> _tempFiles = new();

  public TaskExecutorTests()
  {
    _loggerMock = new Mock<ILogger<TaskExecutor>>();
    _executor = new TaskExecutor(_loggerMock.Object, "test-agent");
    _testDir = Path.Combine(Path.GetTempPath(), $"FunnelCloudTests_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_testDir);
  }

  public void Dispose()
  {
    _executor.Dispose();
    foreach (var file in _tempFiles)
    {
      try { File.Delete(file); } catch { }
    }
    try { Directory.Delete(_testDir, recursive: true); } catch { }
    GC.SuppressFinalize(this);
  }

  private string CreateTempFile(string content)
  {
    var path = Path.Combine(_testDir, $"test_{Guid.NewGuid():N}.txt");
    File.WriteAllText(path, content);
    _tempFiles.Add(path);
    return path;
  }

  #region ReadFile Tests

  [Fact]
  public async Task ExecuteReadFile_ExistingFile_ReturnsContent()
  {
    var expectedContent = "Hello, FunnelCloud!";
    var filePath = CreateTempFile(expectedContent);

    var request = new TaskRequest
    {
      TaskId = "read-1",
      Type = TaskType.ReadFile,
      Command = filePath
    };

    var result = await _executor.ExecuteAsync(request);

    Assert.True(result.Success);
    Assert.Equal(expectedContent, result.Stdout);
    Assert.Equal(0, result.ExitCode);
    Assert.Equal("test-agent", result.AgentId);
  }

  [Fact]
  public async Task ExecuteReadFile_NonExistentFile_ReturnsNotFoundError()
  {
    var request = new TaskRequest
    {
      TaskId = "read-2",
      Type = TaskType.ReadFile,
      Command = Path.Combine(_testDir, "nonexistent.txt")
    };

    var result = await _executor.ExecuteAsync(request);

    Assert.False(result.Success);
    Assert.Equal(TaskErrorCode.NotFound, result.ErrorCode);
    Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task ExecuteReadFile_EmptyFile_ReturnsEmptyString()
  {
    var filePath = CreateTempFile("");

    var request = new TaskRequest
    {
      TaskId = "read-3",
      Type = TaskType.ReadFile,
      Command = filePath
    };

    var result = await _executor.ExecuteAsync(request);

    Assert.True(result.Success);
    Assert.Equal("", result.Stdout);
  }

  [Fact]
  public async Task ExecuteReadFile_LargeFile_ReturnsFullContent()
  {
    var content = string.Join("\n", Enumerable.Range(1, 1000).Select(i => $"Line {i}: {new string('x', 100)}"));
    var filePath = CreateTempFile(content);

    var request = new TaskRequest
    {
      TaskId = "read-4",
      Type = TaskType.ReadFile,
      Command = filePath
    };

    var result = await _executor.ExecuteAsync(request);

    Assert.True(result.Success);
    Assert.Equal(content, result.Stdout);
  }

  #endregion

  #region WriteFile Tests

  [Fact]
  public async Task ExecuteWriteFile_NewFile_CreatesFile()
  {
    var filePath = Path.Combine(_testDir, "newfile.txt");
    var content = "Test content";
    _tempFiles.Add(filePath);

    var request = new TaskRequest
    {
      TaskId = "write-1",
      Type = TaskType.WriteFile,
      Command = filePath,
      Content = content
    };

    var result = await _executor.ExecuteAsync(request);

    Assert.True(result.Success);
    Assert.True(File.Exists(filePath));
    Assert.Equal(content, File.ReadAllText(filePath));
  }

  [Fact]
  public async Task ExecuteWriteFile_ExistingFile_OverwritesContent()
  {
    var filePath = CreateTempFile("original content");
    var newContent = "new content";

    var request = new TaskRequest
    {
      TaskId = "write-2",
      Type = TaskType.WriteFile,
      Command = filePath,
      Content = newContent
    };

    var result = await _executor.ExecuteAsync(request);

    Assert.True(result.Success);
    Assert.Equal(newContent, File.ReadAllText(filePath));
  }

  [Fact]
  public async Task ExecuteWriteFile_NestedDirectory_CreatesDirectories()
  {
    var filePath = Path.Combine(_testDir, "nested", "deep", "file.txt");
    var content = "nested content";
    _tempFiles.Add(filePath);

    var request = new TaskRequest
    {
      TaskId = "write-3",
      Type = TaskType.WriteFile,
      Command = filePath,
      Content = content
    };

    var result = await _executor.ExecuteAsync(request);

    Assert.True(result.Success);
    Assert.True(File.Exists(filePath));
    Assert.Equal(content, File.ReadAllText(filePath));
  }

  [Fact]
  public async Task ExecuteWriteFile_NullContent_WritesEmptyFile()
  {
    var filePath = Path.Combine(_testDir, "empty.txt");
    _tempFiles.Add(filePath);

    var request = new TaskRequest
    {
      TaskId = "write-4",
      Type = TaskType.WriteFile,
      Command = filePath,
      Content = null
    };

    var result = await _executor.ExecuteAsync(request);

    Assert.True(result.Success);
    Assert.True(File.Exists(filePath));
    Assert.Equal("", File.ReadAllText(filePath));
  }

  #endregion

  #region ListDirectory Tests

  [Fact]
  public async Task ExecuteListDirectory_ExistingDirectory_ReturnsEntries()
  {
    // Create some files and subdirectories
    File.WriteAllText(Path.Combine(_testDir, "file1.txt"), "");
    File.WriteAllText(Path.Combine(_testDir, "file2.txt"), "");
    Directory.CreateDirectory(Path.Combine(_testDir, "subdir"));

    var request = new TaskRequest
    {
      TaskId = "list-1",
      Type = TaskType.ListDirectory,
      Command = _testDir
    };

    var result = await _executor.ExecuteAsync(request);

    Assert.True(result.Success);
    Assert.NotNull(result.Stdout);
    Assert.Contains("file1.txt", result.Stdout);
    Assert.Contains("file2.txt", result.Stdout);
    Assert.Contains("subdir/", result.Stdout); // Directories have trailing slash
  }

  [Fact]
  public async Task ExecuteListDirectory_NonExistentDirectory_ReturnsNotFoundError()
  {
    var request = new TaskRequest
    {
      TaskId = "list-2",
      Type = TaskType.ListDirectory,
      Command = Path.Combine(_testDir, "nonexistent")
    };

    var result = await _executor.ExecuteAsync(request);

    Assert.False(result.Success);
    Assert.Equal(TaskErrorCode.NotFound, result.ErrorCode);
  }

  [Fact]
  public async Task ExecuteListDirectory_EmptyDirectory_ReturnsEmptyOutput()
  {
    var emptyDir = Path.Combine(_testDir, "empty");
    Directory.CreateDirectory(emptyDir);

    var request = new TaskRequest
    {
      TaskId = "list-3",
      Type = TaskType.ListDirectory,
      Command = emptyDir
    };

    var result = await _executor.ExecuteAsync(request);

    Assert.True(result.Success);
    Assert.Equal("", result.Stdout);
  }

  [Fact]
  public async Task ExecuteListDirectory_DirectoriesFirst_ThenFiles()
  {
    File.WriteAllText(Path.Combine(_testDir, "aaa.txt"), "");
    Directory.CreateDirectory(Path.Combine(_testDir, "zzz_dir"));
    File.WriteAllText(Path.Combine(_testDir, "bbb.txt"), "");

    var request = new TaskRequest
    {
      TaskId = "list-4",
      Type = TaskType.ListDirectory,
      Command = _testDir
    };

    var result = await _executor.ExecuteAsync(request);

    Assert.True(result.Success);
    var lines = result.Stdout!.Split('\n');
    var dirIndex = Array.FindIndex(lines, l => l.Contains("zzz_dir"));
    var fileIndex = Array.FindIndex(lines, l => l.Contains("aaa.txt"));
    Assert.True(dirIndex < fileIndex, "Directories should come before files");
  }

  #endregion

  #region Shell/PowerShell Tests

  [Fact]
  public async Task ExecuteShell_SimpleCommand_ReturnsOutput()
  {
    var request = new TaskRequest
    {
      TaskId = "shell-1",
      Type = TaskType.Shell,
      Command = OperatingSystem.IsWindows() ? "echo hello" : "echo hello"
    };

    var result = await _executor.ExecuteAsync(request);

    Assert.True(result.Success);
    Assert.Contains("hello", result.Stdout, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(0, result.ExitCode);
  }

  [Fact]
  public async Task ExecutePowerShell_SimpleCommand_ReturnsOutput()
  {
    var request = new TaskRequest
    {
      TaskId = "ps-1",
      Type = TaskType.PowerShell,
      Command = "Write-Output 'Hello from PowerShell'"
    };

    var result = await _executor.ExecuteAsync(request);

    // May fail if PowerShell is not installed, but should not throw
    Assert.NotNull(result);
    if (result.Success)
    {
      Assert.Contains("Hello from PowerShell", result.Stdout!);
    }
  }

  [Fact]
  public async Task ExecuteShell_FailingCommand_ReturnsNonZeroExitCode()
  {
    var request = new TaskRequest
    {
      TaskId = "shell-2",
      Type = TaskType.Shell,
      Command = OperatingSystem.IsWindows() ? "exit 42" : "exit 42"
    };

    var result = await _executor.ExecuteAsync(request);

    Assert.False(result.Success);
    Assert.Equal(42, result.ExitCode);
  }

  [Fact]
  public async Task ExecuteShell_WithWorkingDirectory_UsesCorrectDirectory()
  {
    var request = new TaskRequest
    {
      TaskId = "shell-3",
      Type = TaskType.Shell,
      Command = OperatingSystem.IsWindows() ? "cd" : "pwd",
      WorkingDirectory = _testDir
    };

    var result = await _executor.ExecuteAsync(request);

    Assert.True(result.Success);
    Assert.Contains(_testDir, result.Stdout!, StringComparison.OrdinalIgnoreCase);
  }

  #endregion

  #region Unsupported Task Type Tests

  [Fact]
  public async Task ExecuteUnsupportedType_ReturnsInternalError()
  {
    var request = new TaskRequest
    {
      TaskId = "unsupported-1",
      Type = TaskType.DotNetCode,
      Command = "Console.WriteLine();"
    };

    var result = await _executor.ExecuteAsync(request);

    Assert.False(result.Success);
    Assert.Equal(TaskErrorCode.InternalError, result.ErrorCode);
    Assert.Contains("Unsupported", result.ErrorMessage);
  }

  #endregion

  #region TaskResult Metadata Tests

  [Fact]
  public async Task Execute_AlwaysSetsTaskId()
  {
    var request = new TaskRequest
    {
      TaskId = "metadata-1",
      Type = TaskType.ReadFile,
      Command = CreateTempFile("test")
    };

    var result = await _executor.ExecuteAsync(request);

    Assert.Equal("metadata-1", result.TaskId);
  }

  [Fact]
  public async Task Execute_AlwaysSetsAgentId()
  {
    var request = new TaskRequest
    {
      TaskId = "metadata-2",
      Type = TaskType.ReadFile,
      Command = CreateTempFile("test")
    };

    var result = await _executor.ExecuteAsync(request);

    Assert.Equal("test-agent", result.AgentId);
  }

  [Fact]
  public async Task Execute_SetsDurationMs()
  {
    var request = new TaskRequest
    {
      TaskId = "metadata-3",
      Type = TaskType.ReadFile,
      Command = CreateTempFile("test")
    };

    var result = await _executor.ExecuteAsync(request);

    Assert.True(result.DurationMs >= 0);
  }

  #endregion

  #region Dispose Tests

  [Fact]
  public void Dispose_CanBeCalledMultipleTimes()
  {
    var executor = new TaskExecutor(_loggerMock.Object, "dispose-test");

    var exception = Record.Exception(() =>
    {
      executor.Dispose();
      executor.Dispose();
      executor.Dispose();
    });

    Assert.Null(exception);
  }

  #endregion
}
