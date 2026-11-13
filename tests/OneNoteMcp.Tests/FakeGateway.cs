using System.Globalization;
using OneNoteMcp.Core;

namespace OneNoteMcp.Tests;

/// <summary>
/// Fixture-backed <see cref="IOneNoteGateway"/> (AD-16). Every port method answers from <c>Fixtures/manifest.json</c>
/// with the captured file's text unchanged, or throws the error OneNote gave for that call. A call with no fixture
/// throws <see cref="ErrorCode.ObjectNotFound"/>, as OneNote does for an unknown ID. Like the real adapter, it throws
/// only <see cref="OneNoteException"/>.
/// </summary>
/// <remarks>
/// The story-3 scripting stays: <see cref="HierarchyXml"/>, when set, answers every <c>GetHierarchy</c>;
/// <see cref="Failure"/>, when set, is thrown by every method; <see cref="Calls"/> records <c>GetHierarchy</c> calls.
/// <see cref="NavigateCalls"/> records <c>NavigateTo</c> calls (story 1.21), and <see cref="CreateCalls"/> records
/// <c>CreateNewPage</c> calls (story 2.4) and <see cref="CreateSectionCalls"/> section creates (story 2.11); <see cref="UpdateCalls"/> and <see cref="DeleteCalls"/> record the page
/// writes (story 2.7), and <see cref="AbandonedWrites"/> scripts an outstanding abandoned write. None has a fixture.
/// <para>
/// Story 1.20's access check resolves a page's section (<c>GetHierarchyParent</c>, then <c>GetHierarchy</c> of the
/// parent at <c>Self</c>) and reads a search's scope at <c>Sections</c>. <see cref="Parents"/> and
/// <see cref="Hierarchies"/> answer those for the fixture pages that have no captured parent, all in unprotected
/// sections; tests add protected ones. An ID in neither still falls through to the manifest.
/// </para>
/// </remarks>
internal sealed class FakeGateway : IOneNoteGateway
{
    /// <summary>The committed fixtures: <c>tests/OneNoteMcp.Tests/Fixtures</c> in the source tree.</summary>
    public static readonly string DefaultDirectory = RepoPaths.Combine("tests", "OneNoteMcp.Tests", "Fixtures");

    private static readonly Lazy<Fixtures> Shared = new(() => Fixtures.Load(DefaultDirectory));

    private readonly Fixtures fixtures;

    public FakeGateway()
        : this(Shared.Value)
    {
    }

    private FakeGateway(Fixtures fixtures) => this.fixtures = fixtures;

    /// <summary>The committed manifest, for tests that look up fixture IDs.</summary>
    public static FixtureManifest Manifest => Shared.Value.Manifest;

    /// <summary>The section the fixture cloud and XPS pages are given (synthetic, unprotected).</summary>
    public const string SyntheticSectionId = "{5EC70000-0000-4000-8000-000000000001}{1}{B0}";

    /// <summary>The captured Fixtures section of MCP Test.</summary>
    public const string FixturesSectionId = "{A20A4FC1-E5F2-4154-ADC8-8A513656DFEE}{1}{B0}";

    /// <summary>When set, every <c>GetHierarchy</c> returns it, whatever the arguments.</summary>
    public string? HierarchyXml { get; set; }

    /// <summary><c>GetHierarchyParent</c> answers that win over the manifest, by object ID.</summary>
    public Dictionary<string, string> Parents { get; } = new(StringComparer.Ordinal)
    {
        ["{A20A4FC1-E5F2-4154-ADC8-8A513656DFEE}{1}{E19478355080928453447520102198986629748415351}"] = FixturesSectionId,
        ["{A20A4FC1-E5F2-4154-ADC8-8A513656DFEE}{1}{E19516938799045219860520175612925891145658211}"] = FixturesSectionId,
        ["{A20A4FC1-E5F2-4154-ADC8-8A513656DFEE}{1}{E19565193393940819141020118324689590846097081}"] = FixturesSectionId,
        ["{A20A4FC1-E5F2-4154-ADC8-8A513656DFEE}{1}{E19576028730475398412520134068976138901756251}"] = FixturesSectionId,
        ["{C10D0000-0000-4000-8000-000000000001}{11}{E1000000000000000000000000000000000000000001}"] = SyntheticSectionId,
        ["{5E000000-0000-4000-8000-000000000001}{11}{E1000000000000000000000000000000000000000002}"] = SyntheticSectionId,
    };

    /// <summary><c>GetHierarchy</c> answers that win over the manifest (not over <see cref="HierarchyXml"/>), by ID and scope.</summary>
    public Dictionary<(string Id, HierarchyScope Scope), string> Hierarchies { get; } = new()
    {
        [(SyntheticSectionId, HierarchyScope.Self)] =
            "<?xml version=\"1.0\"?>\n<one:Section xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\" name=\"Synthetic\" ID=\"" + SyntheticSectionId + "\" path=\"C:\\synthetic\\Synthetic.one\" />",
        [(string.Empty, HierarchyScope.Sections)] =
            "<?xml version=\"1.0\"?>\n<one:Notebooks xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\" />",
    };

    public OneNoteException? Failure { get; set; }

    public List<(string StartNodeId, HierarchyScope Scope)> Calls { get; } = [];

    /// <summary>The text of a fixture file, relative to <c>Fixtures/</c>.</summary>
    public static string ReadFixture(string relativePath) => Shared.Value.Read(relativePath);

    public Task<string> GetHierarchyAsync(string startNodeId, HierarchyScope scope, CancellationToken ct)
    {
        Calls.Add((startNodeId, scope));
        return HierarchyXml is not null && Failure is null ? Task.FromResult(HierarchyXml)
            : Failure is null && Hierarchies.TryGetValue((startNodeId, scope), out var xml) ? Task.FromResult(xml)
            : Serve(nameof(IOneNoteGateway.GetHierarchyAsync), startNodeId, scope.ToString());
    }

    public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct) =>
        Failure is null && Parents.TryGetValue(objectId, out var parent)
            ? Task.FromResult(parent)
            : Serve(nameof(IOneNoteGateway.GetHierarchyParentAsync), objectId);

    /// <summary>
    /// <c>GetPageContent</c> answers that win over the manifest, by page ID, whatever the <c>PageInfo</c> (story 2.9). A
    /// test changes an entry from <see cref="OnUpdate"/>, so a write's read-back sees the page as written.
    /// </summary>
    public Dictionary<string, string> Pages { get; } = new(StringComparer.Ordinal);

    /// <summary>When an entry is set, <c>GetPageContent</c> of that page throws it (story 2.9's read-back failure).</summary>
    public Dictionary<string, Exception> PageFailures { get; } = new(StringComparer.Ordinal);

    /// <summary>Runs with the sent XML after each <c>UpdatePageContent</c> call that succeeds (story 2.9).</summary>
    public Action<string>? OnUpdate { get; set; }

    /// <summary>Every <c>GetPageContent</c> call, in order (story 2.9).</summary>
    public List<(string PageId, PageInfo PageInfo)> PageCalls { get; } = [];

    public Task<string> GetPageContentAsync(string pageId, PageInfo pageInfo, CancellationToken ct)
    {
        PageCalls.Add((pageId, pageInfo));
        return Failure is null && PageFailures.TryGetValue(pageId, out var failure) ? Task.FromException<string>(failure)
            : Failure is null && Pages.TryGetValue(pageId, out var xml) ? Task.FromResult(xml)
            : Serve(nameof(IOneNoteGateway.GetPageContentAsync), pageId, pageInfo.ToString());
    }

    public Task<string> GetBinaryPageContentAsync(string pageId, string callbackId, CancellationToken ct) =>
        Serve(nameof(IOneNoteGateway.GetBinaryPageContentAsync), pageId, callbackId);

    public Task<string> FindPagesAsync(string startNodeId, string query, bool includeUnindexed, CancellationToken ct) =>
        Serve(nameof(IOneNoteGateway.FindPagesAsync), startNodeId, query, Bool(includeUnindexed));

    public Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct) =>
        Serve(nameof(IOneNoteGateway.FindMetaAsync), startNodeId, name, Bool(includeUnindexed));

    public Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) =>
        Serve(nameof(IOneNoteGateway.GetHyperlinkToObjectAsync), hierarchyId, pageContentObjectId);

    public Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) =>
        Serve(nameof(IOneNoteGateway.GetWebHyperlinkToObjectAsync), hierarchyId, pageContentObjectId);

    public Task<string> GetSpecialLocationAsync(SpecialLocation location, CancellationToken ct) =>
        Serve(nameof(IOneNoteGateway.GetSpecialLocationAsync), location.ToString());

    /// <summary>Every <c>NavigateTo</c> call, in order. It returns no content, so it has no fixture: it records and succeeds.</summary>
    public List<(string HierarchyId, string ObjectId)> NavigateCalls { get; } = [];

    public Task NavigateToAsync(string hierarchyId, string objectId, CancellationToken ct)
    {
        NavigateCalls.Add((hierarchyId, objectId));
        return Failure is not null ? Task.FromException(Failure) : Task.CompletedTask;
    }

    /// <summary>Every <c>CreateNewPage</c> call, in order (story 2.4). It has no fixture: it records and returns <see cref="CreatedPageId"/>.</summary>
    public List<(string SectionId, NewPageStyle Style)> CreateCalls { get; } = [];

    /// <summary>When set, <c>CreateNewPage</c> alone throws it, so the steps before the call still succeed.</summary>
    public Exception? CreateFailure { get; set; }

    /// <summary>When set, runs inside <c>CreateNewPage</c> before it answers, with the call's token (a hook to observe or cancel).</summary>
    public Func<CancellationToken, Task>? OnCreate { get; set; }

    /// <summary>The synthetic ID the <paramref name="n"/>th create (from 1) returns.</summary>
    public static string CreatedPageId(int n) =>
        string.Create(CultureInfo.InvariantCulture, $"{{C4EA7E00-0000-4000-8000-000000000001}}{{1}}{{E{n:D44}}}");

    public async Task<string> CreateNewPageAsync(string sectionId, NewPageStyle style, CancellationToken ct)
    {
        CreateCalls.Add((sectionId, style));
        if (OnCreate is { } hook)
        {
            await hook(ct).ConfigureAwait(false);
        }

        if ((Failure ?? CreateFailure) is { } failure)
        {
            throw failure;
        }

        var created = CreatedPageId(CreateCalls.Count);
        OnCreated?.Invoke(sectionId, created);
        return created;
    }

    /// <summary>Runs with the section and the new page's ID after each <c>CreateNewPage</c> call that succeeds (story 2.11), so a test can serve the new page.</summary>
    public Action<string, string>? OnCreated { get; set; }

    /// <summary>Every <c>OpenHierarchy</c> section create, in order (story 2.11). It has no fixture: it records and returns <see cref="CreatedSectionId"/>.</summary>
    public List<(string ParentId, string SectionFileName)> CreateSectionCalls { get; } = [];

    /// <summary>When set, the section create alone throws it.</summary>
    public Exception? CreateSectionFailure { get; set; }

    /// <summary>Runs with the parent, the file name and the new section's ID after each section create that succeeds.</summary>
    public Action<string, string, string>? OnCreateSection { get; set; }

    /// <summary>The synthetic ID the <paramref name="n"/>th section create (from 1) returns.</summary>
    public static string CreatedSectionId(int n) =>
        string.Create(CultureInfo.InvariantCulture, $"{{5EC7C4EA-0000-4000-8000-{n:D12}}}{{1}}{{B0}}");

    public Task<string> CreateSectionAsync(string parentId, string sectionFileName, CancellationToken ct)
    {
        CreateSectionCalls.Add((parentId, sectionFileName));
        if ((Failure ?? CreateSectionFailure) is { } failure)
        {
            return Task.FromException<string>(failure);
        }

        var created = CreatedSectionId(CreateSectionCalls.Count);
        OnCreateSection?.Invoke(parentId, sectionFileName, created);
        return Task.FromResult(created);
    }

    /// <summary>Every <c>OpenHierarchy</c> section-group create, in order (story 5.3). It has no fixture: it records and returns <see cref="CreatedGroupId"/>.</summary>
    public List<(string ParentId, string GroupName)> CreateGroupCalls { get; } = [];

    /// <summary>When set, the section-group create alone throws it.</summary>
    public Exception? CreateGroupFailure { get; set; }

    /// <summary>The synthetic ID the <paramref name="n"/>th section-group create (from 1) returns.</summary>
    public static string CreatedGroupId(int n) =>
        string.Create(CultureInfo.InvariantCulture, $"{{6C70C4EA-0000-4000-8000-{n:D12}}}{{1}}{{B0}}");

    public Task<string> CreateSectionGroupAsync(string parentId, string groupName, CancellationToken ct)
    {
        CreateGroupCalls.Add((parentId, groupName));
        if ((Failure ?? CreateGroupFailure) is { } failure)
        {
            return Task.FromException<string>(failure);
        }

        return Task.FromResult(CreatedGroupId(CreateGroupCalls.Count));
    }

    /// <summary>Every <c>OpenHierarchy</c> notebook create, in order (story 5.3). It has no fixture: it records and returns <see cref="CreatedNotebookId"/>.</summary>
    public List<string> CreateNotebookCalls { get; } = [];

    /// <summary>When set, the notebook create alone throws it.</summary>
    public Exception? CreateNotebookFailure { get; set; }

    /// <summary>When set, the notebook create returns its answer instead of <see cref="CreatedNotebookId"/>, after recording the call.</summary>
    public Func<string, string>? OnCreateNotebook { get; set; }

    /// <summary>The synthetic ID the <paramref name="n"/>th notebook create (from 1) returns.</summary>
    public static string CreatedNotebookId(int n) =>
        string.Create(CultureInfo.InvariantCulture, $"{{4B0BC4EA-0000-4000-8000-{n:D12}}}{{1}}{{B0}}");

    public Task<string> CreateNotebookAsync(string folderPath, CancellationToken ct)
    {
        CreateNotebookCalls.Add(folderPath);
        if ((Failure ?? CreateNotebookFailure) is { } failure)
        {
            return Task.FromException<string>(failure);
        }

        return Task.FromResult(OnCreateNotebook?.Invoke(folderPath) ?? CreatedNotebookId(CreateNotebookCalls.Count));
    }

    /// <summary>Every <c>UpdatePageContent</c> call, in order (story 2.7). It has no fixture: it records and succeeds.</summary>
    public List<(string PageXml, string ExpectedLastModified)> UpdateCalls { get; } = [];

    /// <summary>When set, <c>UpdatePageContent</c> alone throws it.</summary>
    public Exception? UpdateFailure { get; set; }

    /// <summary>Failures for the next <c>UpdatePageContent</c> calls, one per call, before <see cref="UpdateFailure"/> (story 2.11).</summary>
    public Queue<Exception> UpdateFailures { get; } = new();

    /// <summary>Every <c>DeletePageContent</c> call, in order (story 2.7). It has no fixture: it records and succeeds.</summary>
    public List<(string PageId, string ObjectId, string ExpectedLastModified)> DeleteCalls { get; } = [];

    /// <summary>When set, <c>DeletePageContent</c> alone throws it.</summary>
    public Exception? DeleteFailure { get; set; }

    /// <summary>
    /// When set, <c>WaitForAbandonedWritesAsync</c> waits on it, as if an abandoned write were outstanding until it
    /// completes; when null there is none, and the wait completes at once.
    /// </summary>
    public TaskCompletionSource? AbandonedWrites { get; set; }

    /// <summary>When set, runs inside <c>UpdatePageContent</c> before it answers, with the call's token (story 2.11: a hook to cancel during the call).</summary>
    public Func<CancellationToken, Task>? OnUpdateCall { get; set; }

    public async Task UpdatePageContentAsync(string pageXml, string expectedLastModified, CancellationToken ct)
    {
        UpdateCalls.Add((pageXml, expectedLastModified));
        if (OnUpdateCall is { } hook)
        {
            await hook(ct).ConfigureAwait(false);
        }

        if (Failure is null && UpdateFailures.TryDequeue(out var next))
        {
            throw next;
        }

        if ((Failure ?? UpdateFailure) is { } failure)
        {
            throw failure;
        }

        OnUpdate?.Invoke(pageXml);
    }

    public Task DeletePageContentAsync(string pageId, string objectId, string expectedLastModified, CancellationToken ct)
    {
        DeleteCalls.Add((pageId, objectId, expectedLastModified));
        if ((Failure ?? DeleteFailure) is { } failure)
        {
            return Task.FromException(failure);
        }

        OnDelete?.Invoke(pageId, objectId);
        return Task.CompletedTask;
    }

    /// <summary>Runs with the page and object IDs after each <c>DeletePageContent</c> call that succeeds (story 2.10).</summary>
    public Action<string, string>? OnDelete { get; set; }

    /// <summary>Every <c>UpdateHierarchy</c> call's XML, in order (story 5.4). It has no fixture: it records and succeeds.</summary>
    public List<string> UpdateHierarchyCalls { get; } = [];

    /// <summary>When set, <c>UpdateHierarchy</c> alone throws it.</summary>
    public Exception? UpdateHierarchyFailure { get; set; }

    /// <summary>Runs with the sent XML after each <c>UpdateHierarchy</c> call that succeeds (story 5.4), so a test can serve the changed hierarchy to the read-back.</summary>
    public Action<string>? OnUpdateHierarchy { get; set; }

    /// <summary>When set, runs inside <c>UpdateHierarchy</c> before it answers, with the call's token (a hook to observe or cancel).</summary>
    public Func<CancellationToken, Task>? OnUpdateHierarchyCall { get; set; }

    public async Task UpdateHierarchyAsync(string changesXml, CancellationToken ct)
    {
        UpdateHierarchyCalls.Add(changesXml);
        if (OnUpdateHierarchyCall is { } hook)
        {
            await hook(ct).ConfigureAwait(false);
        }

        if ((Failure ?? UpdateHierarchyFailure) is { } failure)
        {
            throw failure;
        }

        OnUpdateHierarchy?.Invoke(changesXml);
    }

    public Task WaitForAbandonedWritesAsync(CancellationToken ct) =>
        AbandonedWrites is { } pending ? pending.Task.WaitAsync(ct) : Task.CompletedTask;

    private static string Bool(bool value) => value ? "true" : "false";

    private Task<string> Serve(string asyncMethod, params string?[] args)
    {
        if (Failure is not null)
        {
            return Task.FromException<string>(Failure);
        }

        var method = asyncMethod[..^"Async".Length];
        if (!fixtures.Entries.TryGetValue(FixtureManifest.Key(method, [.. args.Select(a => a ?? string.Empty)]), out var entry))
        {
            // OneNote's own words for an unknown ID (hrObjectDoesNotExist); no content in the message (AD-9).
            return Task.FromException<string>(new OneNoteException(
                ErrorCode.ObjectNotFound,
                $"{method} failed: hrObjectDoesNotExist (0x80042014).",
                unchecked((int)0x80042014)));
        }

        if (entry.Error is { } error)
        {
            return Task.FromException<string>(new OneNoteException(
                Enum.Parse<ErrorCode>(error.Code),
                error.Message,
                error.HResult is null ? null : unchecked((int)uint.Parse(error.HResult.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture))));
        }

        return Task.FromResult(fixtures.Read(entry.File!));
    }

    private sealed class Fixtures
    {
        private readonly string directory;
        private readonly Dictionary<string, string> texts = new(StringComparer.Ordinal);

        private Fixtures(string directory, FixtureManifest manifest)
        {
            this.directory = directory;
            Manifest = manifest;
            Entries = manifest.Entries.ToDictionary(e => e.Key, StringComparer.Ordinal);
        }

        public FixtureManifest Manifest { get; }

        public Dictionary<string, FixtureEntry> Entries { get; }

        public static Fixtures Load(string directory) =>
            new(directory, FixtureManifest.Load(Path.Combine(directory, FixtureManifest.FileName)));

        public string Read(string relativePath)
        {
            lock (texts)
            {
                if (!texts.TryGetValue(relativePath, out var text))
                {
                    // UTF-8 without a BOM, as the capture writes it: the bytes on disk are the string served.
                    text = File.ReadAllText(Path.Combine(directory, relativePath), new System.Text.UTF8Encoding(false));
                    texts.Add(relativePath, text);
                }

                return text;
            }
        }
    }
}
