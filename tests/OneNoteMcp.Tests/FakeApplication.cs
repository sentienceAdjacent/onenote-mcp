using OneNoteMcp.Com.Interop;

namespace OneNoteMcp.Tests;

/// <summary>Managed stand-in for the OneNote COM object. Only GetHierarchy is scripted; the rest throw.</summary>
internal sealed class FakeApplication(Func<string> onGetHierarchy) : IApplication
{
    public Func<string> OnGetHierarchy { get; set; } = onGetHierarchy;

    public void GetHierarchy(string bstrStartNodeID, HierarchyScope hsScope, out string pbstrHierarchyXmlOut, XMLSchema xsSchema) =>
        pbstrHierarchyXmlOut = OnGetHierarchy();

    public void UpdateHierarchy(string bstrChangesXmlIn, XMLSchema xsSchema) => throw new NotSupportedException();

    public void OpenHierarchy(string bstrPath, string bstrRelativeToObjectID, out string pbstrObjectID, CreateFileType cftIfNotExist) => throw new NotSupportedException();

    public void DeleteHierarchy(string bstrObjectID, DateTime dateExpectedLastModified, bool deletePermanently) => throw new NotSupportedException();

    public void CreateNewPage(string bstrSectionID, out string pbstrPageID, NewPageStyle npsNewPageStyle) => throw new NotSupportedException();

    public void CloseNotebook(string bstrNotebookID, bool force) => throw new NotSupportedException();

    public void GetHierarchyParent(string bstrObjectID, out string pbstrParentID) => throw new NotSupportedException();

    public void GetPageContent(string bstrPageID, out string pbstrPageXmlOut, PageInfo pageInfoToExport, XMLSchema xsSchema) => throw new NotSupportedException();

    public void UpdatePageContent(string bstrPageChangesXmlIn, DateTime dateExpectedLastModified, XMLSchema xsSchema, bool force) => throw new NotSupportedException();

    public void GetBinaryPageContent(string bstrPageID, string bstrCallbackID, out string pbstrBinaryObjectB64Out) => throw new NotSupportedException();

    public void DeletePageContent(string bstrPageID, string bstrObjectID, DateTime dateExpectedLastModified, bool force) => throw new NotSupportedException();

    public void NavigateTo(string bstrHierarchyObjectID, string bstrObjectID, bool fNewWindow) => throw new NotSupportedException();

    public void NavigateToUrl(string bstrUrl, bool fNewWindow) => throw new NotSupportedException();

    public void Publish(string bstrHierarchyID, string bstrTargetFilePath, PublishFormat pfPublishFormat, string bstrCLSIDofExporter) => throw new NotSupportedException();

    public void OpenPackage(string bstrPathPackage, string bstrPathDest, out string pbstrPathOut) => throw new NotSupportedException();

    public void GetHyperlinkToObject(string bstrHierarchyID, string bstrPageContentObjectID, out string pbstrHyperlinkOut) => throw new NotSupportedException();

    public void FindPages(string bstrStartNodeID, string bstrSearchString, out string pbstrHierarchyXmlOut, bool fIncludeUnindexedPages, bool fDisplay, XMLSchema xsSchema) => throw new NotSupportedException();

    public void FindMeta(string bstrStartNodeID, string bstrSearchStringName, out string pbstrHierarchyXmlOut, bool fIncludeUnindexedPages, XMLSchema xsSchema) => throw new NotSupportedException();

    public void GetSpecialLocation(SpecialLocation slToGet, out string pbstrSpecialLocationPath) => throw new NotSupportedException();

    public void MergeFiles(string bstrBaseFile, string bstrClientFile, string bstrServerFile, string bstrTargetFile) => throw new NotSupportedException();

    public object QuickFiling() => throw new NotSupportedException();

    public void SyncHierarchy(string bstrHierarchyID) => throw new NotSupportedException();

    public void SetFilingLocation(FilingLocation flToSet, FilingLocationType fltToSet, string bstrFilingSectionID) => throw new NotSupportedException();

    public object Windows => throw new NotSupportedException();

    public bool Dummy1 => throw new NotSupportedException();

    public void MergeSections(string bstrSectionSourceId, string bstrSectionDestinationId) => throw new NotSupportedException();

    public object COMAddIns => throw new NotSupportedException();

    public object LanguageSettings => throw new NotSupportedException();

    public void GetWebHyperlinkToObject(string bstrHierarchyID, string bstrPageContentObjectID, out string pbstrHyperlinkOut) => throw new NotSupportedException();
}
