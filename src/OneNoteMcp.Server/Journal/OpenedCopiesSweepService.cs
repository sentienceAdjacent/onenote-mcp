using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.Writes;

namespace OneNoteMcp.Server.Journal;

/// <summary>
/// Story 3.8 (Phill's cleanup answer (b), 2026-11-18): once, when the server starts, in the background, deletes the copies
/// in the opened-copies folder that hold the marker, aren't open in OneNote and are more than 7 days old
/// (<see cref="OpenedCopySweep"/>). A failure is logged, with counts only (AD-13), and never stops the server.
/// </summary>
public sealed partial class OpenedCopiesSweepService : BackgroundService
{
    private readonly IOneNoteGateway gateway;
    private readonly SettingsStore store;
    private readonly TimeProvider clock;
    private readonly ILogger logger;

    public OpenedCopiesSweepService(IOneNoteGateway gateway, SettingsStore store, TimeProvider clock, ILogger<OpenedCopiesSweepService> logger)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        this.gateway = gateway;
        this.store = store;
        this.clock = clock;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Off the startup path: the host goes on serving while the sweep runs.
        await Task.Yield();
        try
        {
            var deleted = await OpenedCopySweep.SweepAsync(gateway, store.OpenedCopiesFolder, clock.GetUtcNow(), stoppingToken).ConfigureAwait(false);
            Swept(logger, deleted);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // A background sweep must never stop the server; the next start sweeps again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SweepFailed(logger, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "opened copies swept deleted={Deleted}")]
    private static partial void Swept(ILogger logger, int deleted);

    [LoggerMessage(Level = LogLevel.Warning, Message = "opened copies sweep failed exception={ExceptionType}")]
    private static partial void SweepFailed(ILogger logger, string exceptionType);
}
