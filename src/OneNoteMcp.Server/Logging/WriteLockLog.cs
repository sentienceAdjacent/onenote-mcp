using Microsoft.Extensions.Logging;
using OneNoteMcp.Core.Writes;

namespace OneNoteMcp.Server.Logging;

/// <summary>
/// The <c>write lock</c> log line, one per hold of <c>Local\OneNoteMcp.Write</c> (AD-18): the correlationId, the UTC
/// acquire and release times and the wait, IDs and timings only (AD-13). The two-process integration test reads these
/// lines from each server's stderr to check that no two holds overlap.
/// </summary>
public static partial class WriteLockLog
{
    /// <summary>The line's fixed start, which the integration test matches.</summary>
    public const string Prefix = "write lock ";

    public static void Held(ILogger logger, WriteLockHeld held)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(held);
        Log(logger, held.CorrelationId, WriteLockHeld.Format(held.AcquiredUtc), WriteLockHeld.Format(held.ReleasedUtc), held.WaitMs, held.Abandoned);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "write lock correlationId={CorrelationId} acquiredUtc={AcquiredUtc} releasedUtc={ReleasedUtc} waitMs={WaitMs} abandoned={Abandoned}")]
    private static partial void Log(ILogger logger, string correlationId, string acquiredUtc, string releasedUtc, long waitMs, bool abandoned);
}
