using Microsoft.Extensions.Logging;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Logging;

/// <summary>
/// <c>quick_filing</c>'s log line (story 4.9; AD-13): the outcome, the chosen ID, timings and the lengths of the title and
/// description. Never their text, which can name the user's notebooks.
/// </summary>
public static partial class QuickFilingLog
{
    public static void Called(ILogger logger, QuickFilingLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(entry);
        Log(logger, entry.CorrelationId, entry.Outcome, entry.ChosenId ?? "-", entry.PressedButton ?? "-", entry.RunMs ?? -1, entry.WaitMs ?? -1, entry.TitleLength, entry.DescriptionLength);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "quick_filing correlationId={CorrelationId} outcome={Outcome} chosenId={ChosenId} pressedButton={PressedButton} runMs={RunMs} waitMs={WaitMs} titleChars={TitleChars} descriptionChars={DescriptionChars}")]
    private static partial void Log(ILogger logger, string correlationId, string outcome, string chosenId, string pressedButton, long runMs, long waitMs, int titleChars, int descriptionChars);
}
