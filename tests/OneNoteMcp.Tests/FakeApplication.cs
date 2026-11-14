using OneNoteMcp.Com.Interop;

namespace OneNoteMcp.Tests;

/// <summary>One call the fake received: the method name, its arguments, and the thread it ran on.</summary>
internal sealed record FakeCall(string Method, object?[] Args, int ThreadId);

/// <summary>
/// Managed stand-in for the OneNote COM object. The read methods are programmable: <see cref="OnCall"/>, when set,
/// answers every one of them (hang on a gate, throw a sequence, return a value); otherwise GetHierarchy answers from
/// <see cref="OnGetHierarchy"/> and the rest throw. Every read call is recorded. <see cref="Factory"/> counts activations.
/// Write and app-state methods always throw, except <c>NavigateTo</c>, <c>CreateNewPage</c>, <c>OpenHierarchy</c>, <c>UpdatePageContent</c>
/// and <c>DeletePageContent</c>, which are recorded and scripted like a read.
/// </summary>
internal sealed class FakeApplication(Func<string>? onGetHierarchy = null) : IApplication
{
    private readonly Lock gate = new();
    private readonly List<FakeCall> calls = [];
    private int activations;

    public Func<string> OnGetHierarchy { get; set; } = onGetHierarchy ?? (() => throw new NotSupportedException());

    public Func<FakeCall, string>? OnCall { get; set; }

    public int Activations => Volatile.Read(ref activations);

    /// <summary>A <c>createApplication</c> seam that counts each activation and returns this fake.</summary>
    public Func<object> Factory => () =>
    {
        Interlocked.Increment(ref activations);
        return this;
    };

    public IReadOnlyList<FakeCall> Calls
    {
        get
        {
            lock (gate)
            {
                return [.. calls];
            }
        }
    }

    public void GetHierarchy(string bstrStartNodeID, HierarchyScope hsScope, out string pbstrHierarchyXmlOut, XMLSchema xsSchema) =>
        pbstrHierarchyXmlOut = Answer(nameof(GetHierarchy), [bstrStartNodeID, hsScope, xsSchema], OnGetHierarchy);

    public void GetHierarchyParent(string bstrObjectID, out string pbstrParentID) =>
        pbstrParentID = Answer(nameof(GetHierarchyParent), [bstrObjectID]);

    public void GetPageContent(string bstrPageID, out string pbstrPageXmlOut, PageInfo pageInfoToExport, XMLSchema xsSchema) =>
        pbstrPageXmlOut = Answer(nameof(GetPageContent), [bstrPageID, pageInfoToExport, xsSchema]);

    public void GetBinaryPageContent(string bstrPageID, string bstrCallbackID, out string pbstrBinaryObjectB64Out) =>
        pbstrBinaryObjectB64Out = Answer(nameof(GetBinaryPageContent), [bstrPageID, bstrCallbackID]);

    public void GetHyperlinkToObject(string bstrHierarchyID, string bstrPageContentObjectID, out string pbstrHyperlinkOut) =>
        pbstrHyperlinkOut = Answer(nameof(GetHyperlinkToObject), [bstrHierarchyID, bstrPageContentObjectID]);

    public void FindPages(string bstrStartNodeID, string bstrSearchString, out string pbstrHierarchyXmlOut, bool fIncludeUnindexedPages, bool fDisplay, XMLSchema xsSchema) =>
        pbstrHierarchyXmlOut = Answer(nameof(FindPages), [bstrStartNodeID, bstrSearchString, fIncludeUnindexedPages, fDisplay, xsSchema]);

    public void FindMeta(string bstrStartNodeID, string bstrSearchStringName, out string pbstrHierarchyXmlOut, bool fIncludeUnindexedPages, XMLSchema xsSchema) =>
        pbstrHierarchyXmlOut = Answer(nameof(FindMeta), [bstrStartNodeID, bstrSearchStringName, fIncludeUnindexedPages, xsSchema]);

    public void GetSpecialLocation(SpecialLocation slToGet, out string pbstrSpecialLocationPath) =>
        pbstrSpecialLocationPath = Answer(nameof(GetSpecialLocation), [slToGet]);

    public void GetWebHyperlinkToObject(string bstrHierarchyID, string bstrPageContentObjectID, out string pbstrHyperlinkOut) =>
        pbstrHyperlinkOut = Answer(nameof(GetWebHyperlinkToObject), [bstrHierarchyID, bstrPageContentObjectID]);

    private string Answer(string method, object?[] args, Func<string>? fallback = null)
    {
        var call = new FakeCall(method, args, Environment.CurrentManagedThreadId);
        lock (gate)
        {
            calls.Add(call);
        }

        if (OnCall is { } onCall)
        {
            return onCall(call);
        }

        return fallback is not null ? fallback() : throw new NotSupportedException($"{method} is not scripted.");
    }

    public void UpdateHierarchy(string bstrChangesXmlIn, XMLSchema xsSchema) => throw new NotSupportedException();

    // Recorded and scripted like the reads (story 2.11), so the section create's argument mapping and no-retry rule can be checked.
    public void OpenHierarchy(string bstrPath, string bstrRelativeToObjectID, out string pbstrObjectID, CreateFileType cftIfNotExist) =>
        pbstrObjectID = Answer(nameof(OpenHierarchy), [bstrPath, bstrRelativeToObjectID, cftIfNotExist]);

    public void DeleteHierarchy(string bstrObjectID, DateTime dateExpectedLastModified, bool deletePermanently) => throw new NotSupportedException();

    // Recorded and scripted like the reads (story 2.4), so the gateway's argument mapping and no-retry rule can be checked.
    public void CreateNewPage(string bstrSectionID, out string pbstrPageID, NewPageStyle npsNewPageStyle) =>
        pbstrPageID = Answer(nameof(CreateNewPage), [bstrSectionID, npsNewPageStyle]);

    public void CloseNotebook(string bstrNotebookID, bool force) => throw new NotSupportedException();

    // Recorded and scripted like the reads (story 2.7), so the gateway's argument mapping, UTC date and no-retry rule can be checked.
    public void UpdatePageContent(string bstrPageChangesXmlIn, DateTime dateExpectedLastModified, XMLSchema xsSchema, bool force) =>
        Answer(nameof(UpdatePageContent), [bstrPageChangesXmlIn, dateExpectedLastModified, xsSchema, force]);

    public void DeletePageContent(string bstrPageID, string bstrObjectID, DateTime dateExpectedLastModified, bool force) =>
        Answer(nameof(DeletePageContent), [bstrPageID, bstrObjectID, dateExpectedLastModified, force]);

    // Recorded and scripted like the reads (story 1.21), so the gateway's argument mapping can be checked.
    public void NavigateTo(string bstrHierarchyObjectID, string bstrObjectID, bool fNewWindow) =>
        Answer(nameof(NavigateTo), [bstrHierarchyObjectID, bstrObjectID, fNewWindow]);

    public void NavigateToUrl(string bstrUrl, bool fNewWindow) => throw new NotSupportedException();

    public void Publish(string bstrHierarchyID, string bstrTargetFilePath, PublishFormat pfPublishFormat, string bstrCLSIDofExporter) => throw new NotSupportedException();

    public void OpenPackage(string bstrPathPackage, string bstrPathDest, out string pbstrPathOut) => throw new NotSupportedException();

    public void MergeFiles(string bstrBaseFile, string bstrClientFile, string bstrServerFile, string bstrTargetFile) => throw new NotSupportedException();

    public object QuickFiling() => throw new NotSupportedException();

    public void SyncHierarchy(string bstrHierarchyID) => throw new NotSupportedException();

    public void SetFilingLocation(FilingLocation flToSet, FilingLocationType fltToSet, string bstrFilingSectionID) => throw new NotSupportedException();

    public object Windows => throw new NotSupportedException();

    public bool Dummy1 => throw new NotSupportedException();

    public void MergeSections(string bstrSectionSourceId, string bstrSectionDestinationId) => throw new NotSupportedException();

    public object COMAddIns => throw new NotSupportedException();

    public object LanguageSettings => throw new NotSupportedException();
}
