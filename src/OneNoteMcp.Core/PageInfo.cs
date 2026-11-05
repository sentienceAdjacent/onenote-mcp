namespace OneNoteMcp.Core;

/// <summary>
/// Core's own page-content detail flags (<c>GetPageContent</c>'s <c>pageInfoToExport</c>). Com maps it to the
/// interop enum; no COM type leaks into Core (AD-2). Page reads exclude binary content unless asked (AD-4).
/// </summary>
public enum PageInfo
{
    Basic,
    BinaryData,
    Selection,
    BinaryDataSelection,
    FileType,
    BinaryDataFileType,
    SelectionFileType,
    All,
}
