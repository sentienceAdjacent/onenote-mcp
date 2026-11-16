namespace OneNoteMcp.Core;

/// <summary>
/// The formats Core asks <c>Publish</c> for (AD-5, AD-11). Core can't see Com's internal <c>PublishFormat</c>, so the
/// gateway maps each member to it and refuses any other value with <see cref="ErrorCode.NotSupported"/> before COM.
/// <see cref="OneNote"/> is the <c>PermanentDelete</c> section backup's (story 5.6); <see cref="Pdf"/> is <c>publish</c>'s
/// (story 3.3). Story 3.13 adds the other formats. There is never a member for <c>pfOneNote2007</c>, which ends
/// ONENOTE.EXE (spike 3.1 Implication 1; AD-11).
/// </summary>
public enum ExportFormat
{
    /// <summary>OneNote's own section format (<c>pfOneNote</c>), a <c>.one</c> file.</summary>
    OneNote,

    /// <summary>PDF (<c>pfPDF</c>).</summary>
    Pdf,
}
