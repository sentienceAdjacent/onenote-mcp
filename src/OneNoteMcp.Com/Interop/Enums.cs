// Values copied from the registered OneNote 15 type library {0EA692EE-BB50-4E3C-AEF0-356D91732725} 1.1.
namespace OneNoteMcp.Com.Interop;

internal enum HierarchyScope
{
    hsSelf = 0,
    hsChildren = 1,
    hsNotebooks = 2,
    hsSections = 3,
    hsPages = 4,
}

internal enum XMLSchema
{
    xs2007 = 0,
    xs2010 = 1,
    xs2013 = 2,
    xs2026 = 3,
}

internal enum PageInfo
{
    piBasic = 0,
    piBinaryData = 1,
    piSelection = 2,
    piBinaryDataSelection = 3,
    piFileType = 4,
    piBinaryDataFileType = 5,
    piSelectionFileType = 6,
    piAll = 7,
}

internal enum SpecialLocation
{
    slBackUpFolder = 0,
    slUnfiledNotesSection = 1,
    slDefaultNotebookFolder = 2,
}

internal enum CreateFileType
{
    cftNone = 0,
    cftNotebook = 1,
    cftFolder = 2,
    cftSection = 3,
}

internal enum NewPageStyle
{
    npsDefault = 0,
    npsBlankPageWithTitle = 1,
    npsBlankPageNoTitle = 2,
}

internal enum PublishFormat
{
    pfOneNote = 0,
    pfOneNotePackage = 1,
    pfMHTML = 2,
    pfPDF = 3,
    pfXPS = 4,
    pfWord = 5,
    pfEMF = 6,
    pfHTML = 7,
    pfOneNote2007 = 8,
}

internal enum FilingLocation
{
    flEMail = 0,
    flContacts = 1,
    flTasks = 2,
    flMeetings = 3,
    flWebContent = 4,
    flPrintOuts = 5,
}

internal enum FilingLocationType
{
    fltNamedSectionNewPage = 0,
    fltCurrentSectionNewPage = 1,
    fltCurrentPage = 2,
    fltNamedPage = 4,
}
