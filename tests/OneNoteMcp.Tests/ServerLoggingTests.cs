using Microsoft.Extensions.Logging;
using OneNoteMcp.Server;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// AD-13: stdout carries MCP JSON-RPC only, so the server's logging (<see cref="ServerSetup.AddOneNoteMcpLogging"/>, the
/// same call <c>Program</c> makes) writes nothing to stdout and one stderr line per record, at every level.
/// </summary>
[Collection(ConsoleRedirection.Name)]
public sealed class ServerLoggingTests
{
    private static readonly LogLevel[] EveryLevel =
        [LogLevel.Trace, LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Critical];

    [Fact]
    public void EveryLevel_GoesToStderr_AsOneLinePerRecord_AndNothingReachesStdout()
    {
        var (stdout, stderr) = ConsoleRedirection.Capture(() =>
        {
            // The console provider takes Console.Out and Console.Error when it is built, so build it inside the redirect.
            using var factory = LoggerFactory.Create(b => b.AddOneNoteMcpLogging(fileLogger: null).SetMinimumLevel(LogLevel.Trace));
            var logger = factory.CreateLogger("OneNoteMcp.LoggingTest");
            foreach (var level in EveryLevel)
            {
#pragma warning disable CA1848, CA2254 // A plain call per level is the point of this test.
                logger.Log(level, "record level={Level}" + Environment.NewLine + "second line of the same record", level);
#pragma warning restore CA1848, CA2254
            }

            // Disposing the factory flushes the console provider's queue before the streams are read.
        });

        Assert.Equal(string.Empty, stdout);
        var lines = stderr.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(EveryLevel.Length, lines.Length);
        for (var i = 0; i < EveryLevel.Length; i++)
        {
            Assert.Contains($"record level={EveryLevel[i]}", lines[i], StringComparison.Ordinal);
            Assert.Contains("second line of the same record", lines[i], StringComparison.Ordinal);
        }
    }
}

/// <summary>
/// Tests that swap <see cref="Console.Out"/> and <see cref="Console.Error"/> run one at a time, since the console is
/// process-wide.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleRedirection
{
    public const string Name = "Console redirection";

    public static (string Stdout, string Stderr) Capture(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        return (stdout.ToString(), stderr.ToString());
    }
}
