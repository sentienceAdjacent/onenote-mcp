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

    private sealed class ReadUseCase : IUseCase
    {
        public OperationClass Class => OperationClass.Read;
    }

    private sealed class CapturingLogger : ILogger<ToolRunner>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));
    }
}
