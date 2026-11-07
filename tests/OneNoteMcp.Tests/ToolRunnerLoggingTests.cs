using Microsoft.Extensions.Logging;
using OneNoteMcp.Core;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server;
using Xunit;

namespace OneNoteMcp.Tests;

public sealed class ToolRunnerLoggingTests
{
    [Fact]
    public async Task UnexpectedException_IsLoggedWithTheException()
    {
        var logger = new CapturingLogger();
        var boom = new InvalidOperationException("boom");

        await new ToolRunner(logger).RunAsync("t", new ReadUseCase(), (_, _) => throw boom, TestContext.Current.CancellationToken);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && ReferenceEquals(e.Exception, boom));
    }

    [Fact]
    public async Task MappedError_LogsNoException()
    {
        var logger = new CapturingLogger();

        await new ToolRunner(logger).RunAsync(
            "t",
            new ReadUseCase(),
            (_, _) => throw new OneNoteException(ErrorCode.ObjectNotFound, "gone"),
            TestContext.Current.CancellationToken);

        Assert.All(logger.Entries, e => Assert.Null(e.Exception));
    }

    [Fact]
    public async Task SearchCacheUse_IsLoggedAsCountsAndSizesOnly()
    {
        var logger = new CapturingLogger();
        var success = Core.Results.ToolSuccess.WithXml("<secret/>", new Core.Results.ToolMeta("c")) with
        {
            SearchCache = new PageTextCacheUse(Hits: 3, Misses: 2, Evicted: 1, Entries: 4, Bytes: 1234),
        };

        await new ToolRunner(logger).RunAsync("find_pages", new ReadUseCase(), (_, _) => Task.FromResult(success), TestContext.Current.CancellationToken);

        var line = Assert.Single(logger.Entries, e => e.Message.StartsWith("search page-text cache", StringComparison.Ordinal)).Message;
        Assert.Contains("tool=find_pages hits=3 misses=2 evicted=1 entries=4 cacheBytes=1234", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", string.Concat(logger.Entries.Select(e => e.Message)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoSearchCacheUse_LogsNoCacheLine()
    {
        var logger = new CapturingLogger();

        await new ToolRunner(logger).RunAsync(
            "t",
            new ReadUseCase(),
            (_, _) => Task.FromResult(Core.Results.ToolSuccess.WithXml("<x/>", new Core.Results.ToolMeta("c"))),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logger.Entries, e => e.Message.StartsWith("search page-text cache", StringComparison.Ordinal));
    }

    private sealed class ReadUseCase : IUseCase
    {
        public OperationClass Class => OperationClass.Read;
    }

    private sealed class CapturingLogger : ILogger<ToolRunner>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception, formatter(state, exception)));
    }
}
