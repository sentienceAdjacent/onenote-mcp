using OneNoteMcp.Com.Interop;

namespace OneNoteMcp.Tests;

/// <summary>One call the fake received: the method name, its arguments, and the thread it ran on.</summary>
internal sealed record FakeCall(string Method, object?[] Args, int ThreadId);

/// <summary>
/// Managed stand-in for the OneNote COM object. The read methods are programmable: <see cref="OnCall"/>, when set,
/// answers every one of them (hang on a gate, throw a sequence, return a value); otherwise GetHierarchy answers from
/// <see cref="OnGetHierarchy"/> and the rest throw. Every read call is recorded. <see cref="Factory"/> counts activations.
/// <c>Windows</c> answers from <see cref="OnWindows"/> (story 4.6), <c>QuickFiling</c> from <see cref="OnQuickFiling"/> (story 4.9). Write and app-state methods always throw, except <c>NavigateTo</c>, <c>SyncHierarchy</c> (story 4.3), <c>CloseNotebook</c> (story 4.4), <c>CreateNewPage</c>, <c>OpenHierarchy</c>, <c>UpdatePageContent</c>,
/// <c>DeletePageContent</c>, <c>UpdateHierarchy</c>, <c>DeleteHierarchy</c> and <c>Publish</c> (story 5.6), which are recorded and scripted like a read.
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

    // Recorded and scripted like the reads (story 5.4), so the gateway's argument mapping and no-retry rule can be checked.
    public void UpdateHierarchy(string bstrChangesXmlIn, XMLSchema xsSchema) =>
        Answer(nameof(UpdateHierarchy), [bstrChangesXmlIn, xsSchema]);

    // Recorded and scripted like the reads (story 2.11), so the section create's argument mapping and no-retry rule can be checked.
    public void OpenHierarchy(string bstrPath, string bstrRelativeToObjectID, out string pbstrObjectID, CreateFileType cftIfNotExist) =>
        pbstrObjectID = Answer(nameof(OpenHierarchy), [bstrPath, bstrRelativeToObjectID, cftIfNotExist]);

    // Recorded and scripted like the reads (story 5.5), so the gateway's argument mapping, UTC date and no-retry rule can be checked.
    public void DeleteHierarchy(string bstrObjectID, DateTime dateExpectedLastModified, bool deletePermanently) =>
        Answer(nameof(DeleteHierarchy), [bstrObjectID, dateExpectedLastModified, deletePermanently]);

    // Recorded and scripted like the reads (story 2.4), so the gateway's argument mapping and no-retry rule can be checked.
    public void CreateNewPage(string bstrSectionID, out string pbstrPageID, NewPageStyle npsNewPageStyle) =>
        pbstrPageID = Answer(nameof(CreateNewPage), [bstrSectionID, npsNewPageStyle]);

    // Recorded and scripted like a read (story 4.4), so the gateway's force: false, no-retry and abandoned-write rules can be checked.
    public void CloseNotebook(string bstrNotebookID, bool force) =>
        Answer(nameof(CloseNotebook), [bstrNotebookID, force]);

    // Recorded and scripted like the reads (story 2.7), so the gateway's argument mapping, UTC date and no-retry rule can be checked.
    public void UpdatePageContent(string bstrPageChangesXmlIn, DateTime dateExpectedLastModified, XMLSchema xsSchema, bool force) =>
        Answer(nameof(UpdatePageContent), [bstrPageChangesXmlIn, dateExpectedLastModified, xsSchema, force]);

    public void DeletePageContent(string bstrPageID, string bstrObjectID, DateTime dateExpectedLastModified, bool force) =>
        Answer(nameof(DeletePageContent), [bstrPageID, bstrObjectID, dateExpectedLastModified, force]);

    // Recorded and scripted like the reads (story 1.21), so the gateway's argument mapping can be checked.
    public void NavigateTo(string bstrHierarchyObjectID, string bstrObjectID, bool fNewWindow) =>
        Answer(nameof(NavigateTo), [bstrHierarchyObjectID, bstrObjectID, fNewWindow]);

    // Recorded and scripted like the reads (story 4.7), so fNewWindow false can be checked.
    public void NavigateToUrl(string bstrUrl, bool fNewWindow) =>
        Answer(nameof(NavigateToUrl), [bstrUrl, fNewWindow]);

    // Recorded and scripted like the reads (story 5.6); a test's OnCall writes the file when it wants one to land.
    public void Publish(string bstrHierarchyID, string bstrTargetFilePath, PublishFormat pfPublishFormat, string bstrCLSIDofExporter) =>
        Answer(nameof(Publish), [bstrHierarchyID, bstrTargetFilePath, pfPublishFormat, bstrCLSIDofExporter]);

    // Recorded and scripted like OpenHierarchy (story 3.8).
    public void OpenPackage(string bstrPathPackage, string bstrPathDest, out string pbstrPathOut) =>
        pbstrPathOut = Answer(nameof(OpenPackage), [bstrPathPackage, bstrPathDest]);

    public void MergeFiles(string bstrBaseFile, string bstrClientFile, string bstrServerFile, string bstrTargetFile) => throw new NotSupportedException();

    /// <summary>
    /// Story 4.9: what <c>QuickFiling</c> returns, recorded as a <c>QuickFiling</c> call; when null it throws, as before. A test
    /// returns a <see cref="FakeQuickFilingDialog"/>, or throws from it.
    /// </summary>
    public Func<object>? OnQuickFiling { get; set; }

    public object QuickFiling()
    {
        var call = new FakeCall(nameof(QuickFiling), [], Environment.CurrentManagedThreadId);
        lock (gate)
        {
            calls.Add(call);
        }

        return OnQuickFiling is { } onQuickFiling ? onQuickFiling() : throw new NotSupportedException();
    }

    // Recorded and scripted like the reads (story 4.3), so the gateway's argument mapping and no-retry rule can be checked.
    public void SyncHierarchy(string bstrHierarchyID) =>
        Answer(nameof(SyncHierarchy), [bstrHierarchyID]);

    public void SetFilingLocation(FilingLocation flToSet, FilingLocationType fltToSet, string bstrFilingSectionID) => throw new NotSupportedException();

    /// <summary>
    /// Story 4.6: what <c>Windows</c> returns, recorded as a <c>Windows</c> call; when null it throws, as before. A test can
    /// throw from it (a disconnect) or return a <see cref="FakeWindows"/>.
    /// </summary>
    public Func<object>? OnWindows { get; set; }

    public object Windows
    {
        get
        {
            var call = new FakeCall(nameof(Windows), [], Environment.CurrentManagedThreadId);
            lock (gate)
            {
                calls.Add(call);
            }

            return OnWindows is { } onWindows ? onWindows() : throw new NotSupportedException();
        }
    }

    public bool Dummy1 => throw new NotSupportedException();

    // Recorded and scripted like the reads (story 3.6), so the gateway's argument mapping and no-retry rule can be checked.
    public void MergeSections(string bstrSectionSourceId, string bstrSectionDestinationId) =>
        Answer(nameof(MergeSections), [bstrSectionSourceId, bstrSectionDestinationId]);

    public object COMAddIns => throw new NotSupportedException();

    public object LanguageSettings => throw new NotSupportedException();
}

/// <summary>
/// Story 4.6: a managed <c>Windows</c> collection. <see cref="GetItem"/> past the end throws <see cref="ArgumentException"/>,
/// as OneNote's <c>E_INVALIDARG</c> does (spike 4.12 W1), and every index asked for is recorded.
/// </summary>
internal sealed class FakeWindows(params IWindow[] windows) : IWindows
{
    private readonly Lock gate = new();
    private readonly List<uint> indices = [];

    public IReadOnlyList<IWindow> Items { get; } = windows;

    /// <summary>What <c>CurrentWindow</c> returns; null for none, as with no window open (W6).</summary>
    public IWindow? Current { get; set; }

    /// <summary>Every index <see cref="GetItem"/> was called with, in order.</summary>
    public IReadOnlyList<uint> Indices
    {
        get
        {
            lock (gate)
            {
                return [.. indices];
            }
        }
    }

    public uint Count => (uint)Items.Count;

    public IWindow? CurrentWindow => Current;

    public IWindow GetItem(uint index)
    {
        lock (gate)
        {
            indices.Add(index);
        }

        return index < Items.Count ? Items[(int)index] : throw new ArgumentException("E_INVALIDARG", nameof(index));
    }

    void IWindows.NewEnumPlaceholder() => throw new NotSupportedException();
}

/// <summary>Story 4.6: a managed <c>Window</c> whose getters answer from its properties and whose <c>NavigateTo</c> (and, story 4.7, <c>NavigateToUrl</c>) is recorded.</summary>
internal sealed class FakeWindow(ulong handle) : IWindow
{
    private readonly Lock gate = new();
    private readonly List<(string HierarchyId, string ObjectId)> navigations = [];
    private readonly List<string> urlNavigations = [];

    public ulong WindowHandle { get; } = handle;

    public string CurrentPageId { get; set; } = string.Empty;

    public string CurrentSectionId { get; set; } = string.Empty;

    public string CurrentSectionGroupId { get; set; } = string.Empty;

    public string CurrentNotebookId { get; set; } = string.Empty;

    public bool FullPageView { get; set; }

    public bool Active { get; set; }

    public int DockedLocation { get; set; } = -1;

    public bool SideNote { get; set; }

    /// <summary>Every <c>NavigateTo</c> this window received, in order.</summary>
    public IReadOnlyList<(string HierarchyId, string ObjectId)> Navigations
    {
        get
        {
            lock (gate)
            {
                return [.. navigations];
            }
        }
    }

    /// <summary>Every <c>NavigateToUrl</c> this window received, in order (story 4.7).</summary>
    public IReadOnlyList<string> UrlNavigations
    {
        get
        {
            lock (gate)
            {
                return [.. urlNavigations];
            }
        }
    }

    public void NavigateToUrl(string bstrUrl)
    {
        lock (gate)
        {
            urlNavigations.Add(bstrUrl);
        }
    }

    public void NavigateTo(string bstrHierarchyObjectID, string bstrObjectID)
    {
        lock (gate)
        {
            navigations.Add((bstrHierarchyObjectID, bstrObjectID));
        }
    }

    public bool GetFullPageView() => FullPageView;

    public bool GetActive() => Active;

    public int GetDockedLocation() => DockedLocation;

    public bool GetSideNote() => SideNote;

    void IWindow.SetFullPageViewPlaceholder() => throw new NotSupportedException();

    void IWindow.SetActivePlaceholder() => throw new NotSupportedException();

    void IWindow.SetDockedLocationPlaceholder() => throw new NotSupportedException();

    void IWindow.ApplicationPlaceholder() => throw new NotSupportedException();


    void IWindow.SetDockedLocationMethodPlaceholder() => throw new NotSupportedException();
}

/// <summary>
/// Story 4.9: a managed <c>IQuickFilingDialog</c>. Every member call is recorded in order with its thread; <see cref="OnRun"/>
/// runs inside <c>Run</c> (to throw, or hang), and <see cref="Callback"/> keeps what <c>Run</c> was given so a test can answer
/// for the user, on any thread. The getters answer from <see cref="Selected"/> and <see cref="Pressed"/>, or throw
/// <see cref="GetterFailure"/>.
/// </summary>
internal sealed class FakeQuickFilingDialog : IQuickFilingDialog
{
    private readonly Lock gate = new();
    private readonly List<(string Member, int ThreadId)> members = [];
    private string title = string.Empty;
    private string description = string.Empty;
    private HierarchyElement treeDepth;

    public IReadOnlyList<(string Member, int ThreadId)> Members
    {
        get
        {
            lock (gate)
            {
                return [.. members];
            }
        }
    }

    public List<(string Text, HierarchyElement Allowed, HierarchyElement ReadOnly, bool Default)> Buttons { get; } = [];

    public IQuickFilingDialogCallback? Callback { get; private set; }

    public Action? OnRun { get; set; }

    public string Selected { get; set; } = string.Empty;

    public uint Pressed { get; set; } = 0xFFFFFFFF;

    public Exception? GetterFailure { get; set; }

    public string Title
    {
        get => Record("get_Title", title);
        set => title = Record("put_Title", value);
    }

    public string Description
    {
        get => Record("get_Description", description);
        set => description = Record("put_Description", value);
    }

    public HierarchyElement TreeDepth
    {
        get => Record("get_TreeDepth", treeDepth);
        set => treeDepth = Record("put_TreeDepth", value);
    }

    public string SelectedItem => GetterFailure is { } failure ? throw failure : Record("get_SelectedItem", Selected);

    public uint PressedButton => GetterFailure is { } failure ? throw failure : Record("get_PressedButton", Pressed);

    public void AddButton(string bstrText, HierarchyElement allowedElements, HierarchyElement allowedReadOnlyElements, bool fDefault)
    {
        Record("AddButton", 0);
        Buttons.Add((bstrText, allowedElements, allowedReadOnlyElements, fDefault));
    }

    public void Run(IQuickFilingDialogCallback piCallback)
    {
        Record("Run", 0);
        Callback = piCallback;
        OnRun?.Invoke();
    }

    /// <summary>Answers for the user: OneNote's call to <c>OnDialogClosed</c>, on the calling thread.</summary>
    public void Answer(string selected, uint pressed)
    {
        Selected = selected;
        Pressed = pressed;
        (Callback ?? throw new InvalidOperationException("Run wasn't called.")).OnDialogClosed(this);
    }

    private T Record<T>(string member, T value)
    {
        lock (gate)
        {
            members.Add((member, Environment.CurrentManagedThreadId));
        }

        return value;
    }

    void IQuickFilingDialog.GetCheckboxTextPlaceholder() => throw new NotSupportedException();

    void IQuickFilingDialog.SetCheckboxTextPlaceholder() => throw new NotSupportedException();

    void IQuickFilingDialog.GetCheckboxStatePlaceholder() => throw new NotSupportedException();

    void IQuickFilingDialog.SetCheckboxStatePlaceholder() => throw new NotSupportedException();

    void IQuickFilingDialog.GetWindowHandlePlaceholder() => throw new NotSupportedException();

    void IQuickFilingDialog.GetParentWindowHandlePlaceholder() => throw new NotSupportedException();

    void IQuickFilingDialog.SetParentWindowHandlePlaceholder() => throw new NotSupportedException();

    void IQuickFilingDialog.GetPositionPlaceholder() => throw new NotSupportedException();

    void IQuickFilingDialog.SetPositionPlaceholder() => throw new NotSupportedException();

    void IQuickFilingDialog.SetRecentResultsPlaceholder() => throw new NotSupportedException();

    void IQuickFilingDialog.SetTreeCollapsedStatePlaceholder() => throw new NotSupportedException();

    void IQuickFilingDialog.SetNotebookFilterOutPlaceholder() => throw new NotSupportedException();

    void IQuickFilingDialog.AddInitialEditorPlaceholder() => throw new NotSupportedException();
}
