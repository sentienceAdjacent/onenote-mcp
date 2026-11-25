using Microsoft.Extensions.Logging;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Logging;

/// <summary>
/// <c>navigate_to_url</c>'s log line (story 4.7; AD-13): the link's kind, the rule that refused it if one did, and the
/// outcome. Never the link, which carries notebook and section names.
/// </summary>
public static partial class NavigateToUrlLog
{
    public static void Called(ILogger logger, NavigateToUrlLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(entry);
        Log(logger, entry.CorrelationId, entry.LinkKind, entry.Rule, entry.Outcome);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "navigate_to_url correlationId={CorrelationId} linkKind={LinkKind} rule={Rule} outcome={Outcome}")]
    private static partial void Log(ILogger logger, string correlationId, string linkKind, string rule, string outcome);
}
