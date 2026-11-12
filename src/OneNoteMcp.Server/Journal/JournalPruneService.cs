using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Server.Logging;

namespace OneNoteMcp.Server.Journal;

/// <summary>
/// Each server process prunes the journal at startup, in the background, and then every 24 hours on
/// <see cref="TimeProvider"/> (AD-8). A prune that fails as a whole is logged and never stops the server; the next one
/// runs on schedule.
/// </summary>
public sealed class JournalPruneService : BackgroundService
{
    /// <summary>The time between prunes (AD-8: daily).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly JournalPruner pruner;
    private readonly TimeProvider clock;
    private readonly ILogger logger;

    public JournalPruneService(JournalPruner pruner, TimeProvider clock, ILogger<JournalPruneService> logger)
    {
        ArgumentNullException.ThrowIfNull(pruner);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        this.pruner = pruner;
        this.clock = clock;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Off the startup path: the host goes on serving while the first prune runs.
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            PruneOnce();
            try
            {
                await Task.Delay(Interval, clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>One prune, logged. Never throws.</summary>
    internal void PruneOnce()
    {
        try
        {
            JournalLog.Pruned(logger, pruner.Prune());
        }
#pragma warning disable CA1031 // A background prune must never stop the server; the next one runs on schedule.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            JournalLog.PruneFailed(logger, ex);
        }
    }
}
