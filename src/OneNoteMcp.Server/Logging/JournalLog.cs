using Microsoft.Extensions.Logging;
using OneNoteMcp.Core.Journal;

namespace OneNoteMcp.Server.Logging;

/// <summary>
/// The journal's log lines (AD-8, AD-13): a stored entry and each prune, with IDs, counts and sizes only, never a path,
/// a title or any XML. Core reports through callbacks; the Server logs, as <see cref="WriteLockLog"/> does.
/// </summary>
public static partial class JournalLog
{
    public static void Written(ILogger logger, JournalWritten written)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(written);
        LogWritten(logger, written.CorrelationId, written.EntryId, written.Bytes, written.Protected);
    }

    public static void Pruned(ILogger logger, JournalPruneResult result)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(result);
        if (result.SkippedReason is { } reason)
        {
            LogPruneSkipped(logger, reason);
            return;
        }

        LogPruned(logger, result.Kept, result.KeptBytes, result.RemovedByAge, result.RemovedBySize, result.RemovedBytes, result.RemovedTemp, result.Failed);
    }

    /// <summary>A prune that failed as a whole; only the exception's type is logged, since its message can name a path.</summary>
    public static void PruneFailed(ILogger logger, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(exception);
        LogPruneFailed(logger, exception.GetType().Name);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "journal entry written correlationId={CorrelationId} entryId={EntryId} bytes={Bytes} protected={Protected}")]
    private static partial void LogWritten(ILogger logger, string correlationId, Guid entryId, long bytes, bool @protected);

    [LoggerMessage(Level = LogLevel.Warning, Message = "journal prune skipped reason={Reason}")]
    private static partial void LogPruneSkipped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "journal pruned kept={Kept} keptBytes={KeptBytes} removedByAge={RemovedByAge} removedBySize={RemovedBySize} removedBytes={RemovedBytes} removedTemp={RemovedTemp} failed={Failed}")]
    private static partial void LogPruned(ILogger logger, int kept, long keptBytes, int removedByAge, int removedBySize, long removedBytes, int removedTemp, int failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "journal prune failed error={Error}")]
    private static partial void LogPruneFailed(ILogger logger, string error);
}
