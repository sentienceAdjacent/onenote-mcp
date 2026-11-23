using Microsoft.Extensions.Logging;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Logging;

/// <summary>
/// <c>open_notebook</c>'s log line (story 4.5; AD-13): the path's kind, the rule that refused it if one did, and the
/// outcome. Never the path, which carries notebook names.
/// </summary>
public static partial class OpenNotebookLog
{
    public static void Called(ILogger logger, OpenNotebookLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(entry);
        Log(logger, entry.CorrelationId, entry.PathKind, entry.Rule, entry.Outcome);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "open_notebook correlationId={CorrelationId} pathKind={PathKind} rule={Rule} outcome={Outcome}")]
    private static partial void Log(ILogger logger, string correlationId, string pathKind, string rule, string outcome);
}
