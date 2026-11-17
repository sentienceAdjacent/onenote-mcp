namespace OneNoteMcp.Core;

/// <summary>
/// The formats Core asks <c>Publish</c> for (AD-5, AD-11). Core can't see Com's internal <c>PublishFormat</c>, so the
/// gateway maps each member to it and refuses any other value with <see cref="ErrorCode.NotSupported"/> before COM.
/// <see cref="OneNote"/> is also the <c>PermanentDelete</c> section backup's (story 5.6); every member is <c>publish</c>'s for
/// a page or a section (stories 3.3 and 3.13; spike 3.1 X1). There is never a member for <c>pfOneNote2007</c>, which ends
/// ONENOTE.EXE (spike 3.1 Implication 1; AD-11).
/// </summary>
public enum ExportFormat
{
    /// <summary>OneNote's own section format (<c>pfOneNote</c>), a <c>.one</c> file.</summary>
    OneNote,

    /// <summary>PDF (<c>pfPDF</c>).</summary>
    Pdf,

    /// <summary>XPS (<c>pfXPS</c>).</summary>
    Xps,

    /// <summary>Word (<c>pfWord</c>), a <c>.docx</c> file.</summary>
    Word,

    /// <summary>A single-file web page (<c>pfMHTML</c>).</summary>
    Mhtml,

    /// <summary>A web page (<c>pfHTML</c>); its images may land in a folder beside it (3.13 plan Q3).</summary>
    Html,

    /// <summary>An enhanced metafile (<c>pfEMF</c>); pages only: a section's holds only its first page, so publish refuses it (the 3.13 sitting).</summary>
    Emf,

    /// <summary>A OneNote package (<c>pfOneNotePackage</c>), a <c>.onepkg</c> file that can land after the call returns.</summary>
    OneNotePackage,
}
