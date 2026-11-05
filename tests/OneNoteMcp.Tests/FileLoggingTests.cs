using Microsoft.Extensions.Logging;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Logging;
using Serilog;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>AD-13: the rolling file log beside stderr, its size cap and the cross-process startup prune.</summary>
public sealed class FileLoggingTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string[] LogFiles() =>
        [.. Directory.GetFiles(_dir.Path, FileLog.FilePattern).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    private string MakeOldLog(string name, int minutesAgo)
    {
        var path = _dir.Combine(name);
        File.WriteAllText(path, "old");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
        return path;
    }

    private static ILoggerFactory FactoryFor(FileLogStart start) =>
        LoggerFactory.Create(b => b.AddSerilog(start.Logger!, dispose: true));

    [Fact]
    public void ResolveDirectory_WithOverride_UsesIt()
    {
        var dir = FileLog.ResolveDirectory(name => name == FileLog.DirectoryOverrideVariable ? _dir.Path : null);

        Assert.Equal(_dir.Path, dir);
    }

    [Fact]
    public void ResolveDirectory_WithoutOverride_IsUnderLocalAppData()
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneNoteMcp", "logs"),
            FileLog.ResolveDirectory(_ => null));
    }

    [Fact]
    public void FileName_HasStartTimeAndProcessId()
    {
        Assert.Equal("onenote-mcp-20261104T093005-4242.log", FileLog.FileName(new DateTime(2026, 11, 4, 9, 30, 5), 4242));
    }

    [Fact]
    public void Start_WritesToTheGivenDirectory()
    {
        var start = FileLog.Start(new FileLogOptions(_dir.Path) { StartedAt = new DateTime(2026, 11, 4, 9, 30, 5), ProcessId = 7 });
        Assert.NotNull(start.Logger);
        Assert.Null(start.FailureReason);

        using (var factory = FactoryFor(start))
        {
            factory.CreateLogger("Test").LogInformation("hello id={Id}", "abc");
        }

        Assert.Equal(_dir.Combine("onenote-mcp-20261104T093005-7.log"), start.FilePath);
        var text = File.ReadAllText(start.FilePath!);
        Assert.Contains("[INF] Test: hello id=abc", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_DropsDebugAndTrace()
    {
        // The MCP SDK logs whole JSON-RPC messages, tool results with XML included, at Debug and Trace.
        var start = FileLog.Start(new FileLogOptions(_dir.Path));

        using (var factory = FactoryFor(start))
        {
            var logger = factory.CreateLogger("Test");
            logger.LogTrace("trace-line");
            logger.LogDebug("debug-line");
            logger.LogInformation("info-line");
        }

        var text = File.ReadAllText(start.FilePath!);
        Assert.Contains("info-line", text, StringComparison.Ordinal);
        Assert.DoesNotContain("trace-line", text, StringComparison.Ordinal);
        Assert.DoesNotContain("debug-line", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolCall_LogsTheCallLineWithoutXml()
    {
        var start = FileLog.Start(new FileLogOptions(_dir.Path));
        string correlationId;

        using (var factory = FactoryFor(start))
        {
            var runner = new ToolRunner(factory.CreateLogger<ToolRunner>());
            var result = await runner.RunAsync(
                "get_hierarchy",
                new ReadUseCase(),
                (ctx, _) => Task.FromResult(ToolSuccess.WithXml(
                    """<one:Notebooks xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote"><one:Notebook name="Private"/></one:Notebooks>""",
                    new ToolMeta(ctx.CorrelationId))),
                TestContext.Current.CancellationToken);
            correlationId = result.StructuredContent!.Value.GetProperty("meta").GetProperty("correlationId").GetString()!;
        }

        var text = File.ReadAllText(start.FilePath!);
        Assert.Contains($"correlationId={correlationId} tool=get_hierarchy result=ok", text, StringComparison.Ordinal);
        Assert.Contains("ms=", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<one:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Private", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_RollsAtTheSizeCapAndKeepsAtMostTheRetainedCount()
    {
        var start = FileLog.Start(new FileLogOptions(_dir.Path) { FileSizeLimitBytes = 1024, RetainedFileCount = 5 });

        using (var factory = FactoryFor(start))
        {
            var logger = factory.CreateLogger("Roll");
            for (var i = 0; i < 400; i++)
            {
                logger.LogInformation("line {Index} padding padding padding padding padding", i);
            }
        }

        var files = LogFiles();
        Assert.True(files.Length > 1, $"expected a roll, found {files.Length} file(s)");
        Assert.True(files.Length <= 5, $"expected at most 5 files, found {files.Length}");
        Assert.All(files, f => Assert.True(new FileInfo(_dir.Combine(f)).Length <= 1024 + 256, $"{f} passed the size cap"));
    }

    [Fact]
    public void Prune_KeepsTheNewestFive()
    {
        for (var i = 0; i < 8; i++)
        {
            MakeOldLog($"onenote-mcp-2026110{i}T000000-1.log", minutesAgo: 100 - i); // higher i is newer
        }

        var unrelated = MakeOldLog("other.log", minutesAgo: 1000);

        var deleted = FileLog.Prune(_dir.Path, keep: 5);

        Assert.Equal(3, deleted);
        Assert.Equal(
            [.. Enumerable.Range(3, 5).Select(i => $"onenote-mcp-2026110{i}T000000-1.log")],
            LogFiles());
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void Prune_SkipsAFileLockedByAnotherProcess()
    {
        for (var i = 0; i < 7; i++)
        {
            MakeOldLog($"onenote-mcp-2026110{i}T000000-1.log", minutesAgo: 100 - i);
        }

        var oldest = _dir.Combine("onenote-mcp-20261100T000000-1.log");
        using (new FileStream(oldest, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var deleted = FileLog.Prune(_dir.Path, keep: 5);

            Assert.Equal(1, deleted);
            Assert.True(File.Exists(oldest));
            Assert.False(File.Exists(_dir.Combine("onenote-mcp-20261101T000000-1.log")));
        }
    }

    [Fact]
    public void Prune_MissingDirectory_DeletesNothing()
    {
        Assert.Equal(0, FileLog.Prune(_dir.Combine("absent"), keep: 5));
    }

    [Fact]
    public void Start_PrunesOtherProcessesFilesLeavingRoomForItsOwn()
    {
        for (var i = 0; i < 6; i++)
        {
            MakeOldLog($"onenote-mcp-2026110{i}T000000-1.log", minutesAgo: 100 - i);
        }

        var start = FileLog.Start(new FileLogOptions(_dir.Path) { ProcessId = 2 });
        using (var factory = FactoryFor(start))
        {
            factory.CreateLogger("Test").LogInformation("started");
        }

        var files = LogFiles();
        Assert.Equal(5, files.Length);
        Assert.Contains(Path.GetFileName(start.FilePath!), files);
    }

    [Fact]
    public void Start_RollingWithOtherProcessesFiles_KeepsAtMostFive()
    {
        for (var i = 0; i < 4; i++)
        {
            MakeOldLog($"onenote-mcp-2026110{i}T000000-1.log", minutesAgo: 100 - i);
        }

        var start = FileLog.Start(new FileLogOptions(_dir.Path) { ProcessId = 2, FileSizeLimitBytes = 1024, RetainedFileCount = 5 });
        using (var factory = FactoryFor(start))
        {
            var logger = factory.CreateLogger("Roll");
            for (var i = 0; i < 400; i++)
            {
                logger.LogInformation("line {Index} padding padding padding padding padding", i);
            }
        }

        var files = LogFiles();
        Assert.True(files.Count(f => f.EndsWith("-2.log", StringComparison.Ordinal) || f.Contains("-2_", StringComparison.Ordinal)) > 1, "expected this process to roll");
        Assert.True(files.Length <= 5, $"expected at most 5 files, found {files.Length}: {string.Join(", ", files)}");
    }

    [Fact]
    public void Start_UnusableDirectory_ReturnsNoLoggerAndDoesNotThrow()
    {
        var notADirectory = _dir.Combine("a-file");
        File.WriteAllText(notADirectory, "x");

        var start = FileLog.Start(new FileLogOptions(notADirectory));

        Assert.Null(start.Logger);
        Assert.Null(start.FilePath);
        Assert.NotNull(start.FailureReason);
    }

    private sealed class ReadUseCase : IUseCase
    {
        public OperationClass Class => OperationClass.Read;
    }
}
