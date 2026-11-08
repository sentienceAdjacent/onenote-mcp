using OneNoteMcp.Core.LargeResults;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Access;

/// <summary>
/// What a target-scoped check decided (AD-21): no grant was needed (<see cref="Grant"/> null), or the read is allowed
/// under <see cref="Grant"/>. A denied read throws <see cref="ErrorCode.ReadNotGranted"/> instead. <see cref="Warnings"/>
/// carries <c>grantNotSaved</c> when the user's grant couldn't be stored.
/// </summary>
public sealed record TargetAccess(ResultGrant? Grant, IReadOnlyList<ToolWarning> Warnings)
{
    public static TargetAccess Unprotected { get; } = new(null, []);

    /// <summary>The grants a stored result made under this decision carries (AD-20).</summary>
    public ResultGrants ResultGrants => Grant is { } grant ? new ResultGrants([grant]) : ResultGrants.None;
}

/// <summary>
/// What a broad check decided (AD-21): the protected sections whose pages are withheld, by ID, and the grants for the
/// protected sections whose pages are included. <see cref="WithholdAll"/> is true when the read's start node is itself
/// inside a withheld section, so every page in it is withheld.
/// </summary>
public sealed record BroadAccess(IReadOnlyList<string> Withheld, IReadOnlyList<ResultGrant> Granted, bool WithholdAll = false)
{
    public static BroadAccess None { get; } = new([], []);

    /// <summary>
    /// True for a page in a withheld section. Fails closed: when anything is withheld, a page the XML shows outside any
    /// <c>one:Section</c> (an empty <paramref name="sectionId"/>) is withheld too, since its section can't be told apart.
    /// </summary>
    public bool IsWithheld(string sectionId) =>
        WithholdAll || (sectionId.Length > 0 ? Withheld.Contains(sectionId, StringComparer.Ordinal) : Withheld.Count > 0);

    /// <summary>
    /// True for a page in any protected section of the scope, withheld or granted: such a page is never looked up in or
    /// added to the search page-text cache (AD-12). A page the XML shows outside any section counts when the scope has
    /// any protected section.
    /// </summary>
    public bool IsProtected(string sectionId) =>
        IsWithheld(sectionId)
        || (sectionId.Length > 0 ? Granted.Any(g => string.Equals(g.SectionId, sectionId, StringComparison.Ordinal)) : Granted.Count > 0);

    /// <summary>One <c>readNotGranted</c> warning per withheld section, naming its ID only (AD-9).</summary>
    public IReadOnlyList<ToolWarning> Warnings =>
    [
        .. Withheld.Select(id => new ToolWarning(
            ToolWarnings.ReadNotGranted,
            $"Section {id} is password-protected, so its pages were left out: reading them needs the user's permission.",
            ReadAccess.WithheldUserAction)),
    ];

    /// <summary><c>meta.withheld</c>: the section IDs, or null when nothing was withheld, so the field is omitted.</summary>
    public IReadOnlyList<string>? Meta => Withheld.Count > 0 ? Withheld : null;

    public ResultGrants ResultGrants => Granted.Count > 0 ? new ResultGrants(Granted) : ResultGrants.None;
}

/// <summary>
/// AD-21's one access check, run by every read use case before any content is returned and before the
/// <c>SectionLocked</c> check, and by <c>read_result</c>. A section is protected when its <c>one:Section</c> has
/// <c>encrypted</c> true, read live from <c>GetHierarchy</c> on every call and never cached. A read grant is keyed by
/// <see cref="GrantKeys.SectionKey"/> of the section's live <c>path</c>. Stored grants are re-read from
/// <c>settings.json</c> at every check (AD-10); an invalid or newer file's grants count as absent.
/// <para>
/// A <c>once</c> grant from the dialog (Phill, 2026-11-07) opens a window: every read of that section, target-scoped and
/// broad, passes for <c>readGrants.onceMinutes</c> (default 5) from when the user's answer arrives. Windows live in this
/// object's memory only, keyed by <see cref="GrantKeys.SectionKey"/>, are checked after the stored grants when a call
/// starts, are never written, and can't be revoked: they end with their window or the process. A dialog grant for some
/// hours or until revoked that couldn't be saved (<c>grantNotSaved</c>) opens the same window instead.
/// </para>
/// <para>
/// Only a target-scoped read asks the user, through <see cref="IGrantPrompt"/>; no gateway call is in flight and no
/// settings lock is held while it waits. Broad reads never ask. Messages carry section IDs only (AD-9).
/// </para>
/// </summary>
public sealed class ReadAccess
{
    /// <summary>A guard against a parent chain that never ends; real hierarchies are a few levels deep.</summary>
    private const int MaxChainDepth = 64;

    public static string WithheldUserAction { get; } =
        "Reading a password-protected section needs the user's permission, which you can't give. Tell the user, and if they want it read, "
        + "they can run " + GrantCli.Run("--read-section \"<section name>\"") + ", or accept the dialog that appears when you read one of its pages "
        + "(get_page_content) in a client that supports it, such as Claude Code. Then repeat this call.";

    public static string LapsedUserAction { get; } =
        "The read grant that allowed the original call has ended. Ask the user whether to grant it again: they can run "
        + GrantCli.Run("--read-section \"<section name>\"") + ", or accept the dialog when you repeat the original call. Then repeat the original call.";

    private readonly IOneNoteGateway gateway;
    private readonly Func<SettingsSnapshot> settings;
    private readonly SettingsWriter? writer;
    private readonly TimeProvider clock;
    private readonly Dictionary<string, ProtectedSectionGrant> windows = new(GrantKeys.Comparer);
    private readonly Lock windowGate = new();

    /// <param name="gateway">The gateway the owning section is resolved through.</param>
    /// <param name="settings">Reads <c>settings.json</c>; called once per check.</param>
    /// <param name="writer">Stores <c>hours</c> and <c>untilRevoked</c> grants; null, every such grant becomes a <c>once</c> window (<c>grantNotSaved</c>).</param>
    /// <param name="clock">The clock grant expiry runs on.</param>
    public ReadAccess(IOneNoteGateway gateway, Func<SettingsSnapshot> settings, SettingsWriter? writer, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        this.gateway = gateway;
        this.settings = settings;
        this.writer = writer;
        this.clock = clock;
    }

    /// <summary>A check with no stored grants and nowhere to store one, for use cases built by hand. It still withholds.</summary>
    public static ReadAccess WithoutStoredGrants(IOneNoteGateway gateway) =>
        new(gateway, NoSettings, writer: null, TimeProvider.System);

    /// <summary>The <c>read_result</c> check (AD-20) on this check's settings and clock.</summary>
    public ResultGrantCheck Results => new(settings, clock);

    internal static SettingsSnapshot NoSettings() => SettingsSnapshot.Defaults("(none)", SettingsStatus.Absent);

    /// <summary>
    /// This process's live <c>once</c> grants, for <c>list_grants</c> (AD-21): each with its section's key, name and the
    /// window's end as <see cref="ProtectedSectionGrant.ExpiresAt"/>. Expired windows are dropped, not listed.
    /// </summary>
    public IReadOnlyList<ProtectedSectionGrant> OnceGrants()
    {
        var now = clock.GetUtcNow();
        lock (windowGate)
        {
            foreach (var key in windows.Where(w => w.Value.IsExpired(now)).Select(w => w.Key).ToList())
            {
                windows.Remove(key);
            }

            return [.. windows.Values];
        }
    }

    /// <summary>
    /// A target-scoped read of <paramref name="objectId"/> (a page, or any hierarchy object): resolves the section that
    /// owns it through a <c>GetHierarchyParent</c> chain (AD-6), then that section's <c>encrypted</c> and <c>path</c>
    /// from <c>GetHierarchy</c>. An object that isn't inside a section (a section, section group or notebook) needs no
    /// grant. Throws <see cref="ErrorCode.ReadNotGranted"/> when the section is protected and the user doesn't grant.
    /// </summary>
    public async Task<TargetAccess> CheckObjectAsync(ToolCallContext context, string objectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var section = await OwningSectionAsync(objectId, ct).ConfigureAwait(false);
        return section is { Encrypted: true } ? await CheckSectionAsync(context, section, ct).ConfigureAwait(false) : TargetAccess.Unprotected;
    }

    /// <summary>
    /// A target-scoped read of the protected section <paramref name="section"/> itself (<c>get_hierarchy</c> at scope
    /// <c>children</c> or <c>pages</c>), or of something inside it.
    /// </summary>
    public async Task<TargetAccess> CheckSectionAsync(ToolCallContext context, HierarchyNode section, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(section);
        if (!section.Encrypted)
        {
            return TargetAccess.Unprotected;
        }

        var key = GrantKeys.SectionKey(section.Path);
        var snapshot = settings();
        var now = clock.GetUtcNow();
        if (Stored(snapshot, key, now) is { } stored)
        {
            return new TargetAccess(Allowing(stored, section.Id, wholeResult: true), []);
        }

        if (Window(key, now))
        {
            return new TargetAccess(new ResultGrant(key, section.Id, ReadGrantLifetime.Once, null, WholeResult: true), []);
        }

        return await AskAsync(context, section, key, snapshot.ReadGrants, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A broad read (AD-21): <paramref name="sections"/> are the protected sections in its scope, found with
    /// <see cref="EncryptedSections"/>. Never asks. <paramref name="includesPages"/> is false for a read that returns no
    /// pages at all (a scope above pages), which then withholds nothing and needs no grant.
    /// </summary>
    public BroadAccess CheckBroad(IReadOnlyList<EncryptedSection> sections, bool includesPages)
    {
        ArgumentNullException.ThrowIfNull(sections);
        if (!includesPages || sections.Count == 0)
        {
            return BroadAccess.None;
        }

        var snapshot = settings();
        var now = clock.GetUtcNow();
        var withheld = new List<string>();
        var granted = new List<ResultGrant>();
        foreach (var section in sections)
        {
            var key = GrantKeys.SectionKey(section.Path);
            if (Stored(snapshot, key, now) is { } stored)
            {
                granted.Add(Allowing(stored, section.Id, wholeResult: false));
            }
            else if (Window(key, now))
            {
                granted.Add(new ResultGrant(key, section.Id, ReadGrantLifetime.Once, null, WholeResult: false));
            }
            else if (!withheld.Contains(section.Id, StringComparer.Ordinal))
            {
                withheld.Add(section.Id);
            }
        }

        return new BroadAccess(withheld, granted);
    }

    /// <summary>
    /// The search tools' check (AD-21): one <c>GetHierarchy</c> of the searched scope at <c>sections</c> level, never the
    /// search XML's own attributes. A start node that is a page is resolved to its section, so every result is
    /// withheld when that section is.
    /// </summary>
    public async Task<BroadAccess> CheckSearchScopeAsync(string startNodeId, CancellationToken ct)
    {
        var xml = await gateway.GetHierarchyAsync(startNodeId ?? string.Empty, HierarchyScope.Sections, ct).ConfigureAwait(false);
        var root = Readable(() => HierarchyNode.Root(xml));
        if (root.Kind == HierarchyKind.Page)
        {
            var section = await OwningSectionAsync(startNodeId!, ct).ConfigureAwait(false);
            if (section is not { Encrypted: true })
            {
                return BroadAccess.None;
            }

            var access = CheckBroad([new EncryptedSection(section.Id, section.Name, section.Path, section.Locked, [])], includesPages: true);
            return access.Withheld.Count > 0 ? access with { WithholdAll = true } : access;
        }

        // A start node that is itself a withheld section: every result is in it, whatever the search XML wraps them in.
        var broad = CheckBroad(Readable(() => EncryptedSections.Find(xml)), includesPages: true);
        return root.Kind == HierarchyKind.Section && root.Encrypted && broad.Withheld.Contains(root.Id, StringComparer.Ordinal)
            ? broad with { WithholdAll = true }
            : broad;
    }

    /// <summary>
    /// The section that owns <paramref name="objectId"/>: up the <c>GetHierarchyParent</c> chain to the first
    /// <c>one:Section</c>. Null when the chain reaches a section group or notebook first, or ends (an empty parent, AD-6).
    /// An exception from either call is an ordinary error and never ends the chain.
    /// </summary>
    public async Task<HierarchyNode?> OwningSectionAsync(string objectId, CancellationToken ct)
    {
        var current = objectId ?? string.Empty;
        for (var depth = 0; depth < MaxChainDepth; depth++)
        {
            var parent = await gateway.GetHierarchyParentAsync(current, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(parent))
            {
                return null;
            }

            var xml = await gateway.GetHierarchyAsync(parent, HierarchyScope.Self, ct).ConfigureAwait(false);
            var node = Readable(() => HierarchyNode.Root(xml));
            switch (node.Kind)
            {
                case HierarchyKind.Section:
                    return node;
                case HierarchyKind.Page:
                    current = parent;
                    continue;
                default:
                    return null;
            }
        }

        throw new OneNoteException(ErrorCode.Internal, $"The parent chain of {objectId} is more than {MaxChainDepth} levels deep.");
    }

    /// <summary>The stored grant for <paramref name="key"/> that still holds at <paramref name="now"/>, or null.</summary>
    internal static ProtectedSectionGrant? Stored(SettingsSnapshot snapshot, string key, DateTimeOffset now) =>
        snapshot.ProtectedSections.TryGetValue(key, out var grant) && !grant.IsExpired(now) ? grant : null;

    private static ResultGrant Allowing(ProtectedSectionGrant stored, string sectionId, bool wholeResult) => new(
        stored.Key,
        sectionId,
        stored.ExpiresAt is null ? ReadGrantLifetime.UntilRevoked : ReadGrantLifetime.Hours,
        stored.ExpiresAt,
        wholeResult);

    /// <summary>True when a <c>once</c> window for <paramref name="key"/> is open at <paramref name="now"/>.</summary>
    private bool Window(string key, DateTimeOffset now)
    {
        if (key.Length == 0)
        {
            return false;
        }

        lock (windowGate)
        {
            return windows.TryGetValue(key, out var window) && !window.IsExpired(now);
        }
    }

    /// <summary>Opens (or reopens) <paramref name="key"/>'s window from now, the moment the user's answer arrived.</summary>
    private ResultGrant OpenWindow(string key, HierarchyNode section, string notebookKey, ReadGrantSettings readGrants)
    {
        var now = clock.GetUtcNow();
        var window = new ProtectedSectionGrant(key, section.Name, notebookKey, section.Id, now, GrantedVia.Elicitation, now + readGrants.OnceWindow);
        lock (windowGate)
        {
            windows[key] = window;
        }

        return new ResultGrant(key, section.Id, ReadGrantLifetime.Once, null, WholeResult: true);
    }

    private async Task<TargetAccess> AskAsync(ToolCallContext context, HierarchyNode section, string key, ReadGrantSettings readGrants, CancellationToken ct)
    {
        if (context.Prompt is not { } prompt)
        {
            throw NotGranted(section.Id, "This client can't show the grant dialog.");
        }

        // An empty path gives an empty key, which settings.json can't hold (AD-21): no grant, and no dialog.
        if (key.Length == 0)
        {
            throw NotGranted(section.Id, "OneNote reports no path for this section, so no grant can be made for it.");
        }

        // Read before the dialog: no gateway call is in flight and no lock is held while the user decides.
        var (notebook, groups) = await LocationAsync(section.Id, ct).ConfigureAwait(false);
        GrantPromptResult answer;
        try
        {
            answer = await prompt.AskAsync(new GrantQuestion(section.Id, section.Name, notebook?.DisplayName ?? string.Empty, groups, readGrants.OnceMinutes), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            answer = new GrantPromptResult(GrantPromptStatus.Cancelled);
        }

        switch (answer.Status)
        {
            case GrantPromptStatus.Unsupported:
                throw NotGranted(section.Id, "This client can't show the grant dialog.");
            case GrantPromptStatus.Declined:
                throw NotGranted(section.Id, "The user declined the grant dialog.");
            case GrantPromptStatus.Cancelled:
                throw NotGranted(section.Id, "The grant dialog was cancelled.");
        }

        // The trimmed typed text must equal the section's name, ignoring case; anything else makes no grant (AD-21).
        if (!GrantKeys.NameMatches(answer.TypedName, section.Name))
        {
            throw NotGranted(section.Id, "The name typed in the grant dialog didn't match the section's name, so nothing was granted.");
        }

        var notebookKey = notebook is null ? string.Empty : GrantKeys.NotebookKey(notebook.Path);
        var lifetime = answer.Lifetime ?? ReadGrantLifetime.Once;
        if (lifetime == ReadGrantLifetime.Once)
        {
            return new TargetAccess(OpenWindow(key, section, notebookKey, readGrants), []);
        }

        var hours = answer.Hours ?? ReadGrantLifetimes.DefaultHours;
        if (lifetime == ReadGrantLifetime.Hours && hours is < ReadGrantLifetimes.MinHours or > ReadGrantLifetimes.MaxHours)
        {
            throw NotGranted(section.Id, $"The grant dialog's hours must be from {ReadGrantLifetimes.MinHours} to {ReadGrantLifetimes.MaxHours}, so nothing was granted.");
        }

        var now = clock.GetUtcNow();
        var grant = new ProtectedSectionGrant(
            key,
            section.Name,
            notebookKey,
            section.Id,
            now,
            GrantedVia.Elicitation,
            lifetime == ReadGrantLifetime.Hours ? now.AddHours(hours) : null);

        var notSaved = Save(grant, section.Id, readGrants);
        if (notSaved is not null)
        {
            // Works like once: a window in memory only (AD-21).
            return new TargetAccess(OpenWindow(key, section, notebookKey, readGrants), [notSaved]);
        }

        return new TargetAccess(Allowing(grant, section.Id, wholeResult: true), []);
    }

    /// <summary>Stores the grant; returns the <c>grantNotSaved</c> warning when it can't, or null.</summary>
    private ToolWarning? Save(ProtectedSectionGrant grant, string sectionId, ReadGrantSettings readGrants)
    {
        string reason;
        if (writer is null)
        {
            reason = "this server has nowhere to store grants";
        }
        else
        {
            try
            {
                writer.SaveReadGrant(grant);
                return null;
            }
            catch (OneNoteException ex)
            {
                reason = ex.Message;
            }
        }

        return new ToolWarning(
            ToolWarnings.GrantNotSaved,
            $"The read grant for section {sectionId} couldn't be saved ({reason}), so it works like a one-time grant: it covers that section for {readGrants.OnceMinutes} minute(s), in this server only.",
            "Fix settings.json (or update OneNoteMcp if a newer version wrote it), then grant again with " + GrantCli.Run("--read-section \"<section name>\"") + ".");
    }

    /// <summary>The notebook and section groups around <paramref name="sectionId"/>, for the dialog. Only asked when a dialog is raised.</summary>
    private async Task<(HierarchyNode? Notebook, IReadOnlyList<string> Groups)> LocationAsync(string sectionId, CancellationToken ct)
    {
        var groups = new List<string>();
        var current = sectionId;
        for (var depth = 0; depth < MaxChainDepth; depth++)
        {
            var parent = await gateway.GetHierarchyParentAsync(current, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(parent))
            {
                break;
            }

            var xml = await gateway.GetHierarchyAsync(parent, HierarchyScope.Self, ct).ConfigureAwait(false);
            var node = Readable(() => HierarchyNode.Root(xml));
            if (node.Kind == HierarchyKind.Notebook)
            {
                groups.Reverse();
                return (node, groups);
            }

            groups.Add(node.Name);
            current = parent;
        }

        groups.Reverse();
        return (null, groups);
    }

    internal static OneNoteException NotGranted(string sectionId, string why) => new(
        ErrorCode.ReadNotGranted,
        $"Section {sectionId} is password-protected, and reading it needs a read grant from the user. {why}",
        userAction: ToolErrors.ReadNotGrantedUserAction);

    private static T Readable<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (OneNoteXmlException ex)
        {
            // OneNoteXmlException messages carry only positions and element names, never content (AD-13).
            throw new OneNoteException(ErrorCode.InvalidXml, $"GetHierarchy returned XML the server can't read: {ex.Message}", innerException: ex);
        }
    }
}

/// <summary>
/// <c>read_result</c>'s AD-21 check (AD-20): a part of a stored result that lies inside a protected section is served only
/// while the grant that allowed the original call holds. <c>once</c> holds for as long as the entry lives; <c>hours</c>
/// until its <c>expiresAt</c>, and <c>untilRevoked</c> until revoked, both re-read from <c>settings.json</c>. It never asks.
/// </summary>
public sealed class ResultGrantCheck(Func<SettingsSnapshot> settings, TimeProvider clock)
{
    /// <summary>The check for results stored by use cases built by hand: no stored grants.</summary>
    public static ResultGrantCheck WithoutStoredGrants { get; } = new(ReadAccess.NoSettings, TimeProvider.System);

    /// <summary>
    /// Throws <see cref="ErrorCode.ReadNotGranted"/> when the response spanning [<paramref name="start"/>,
    /// <paramref name="end"/>) of <paramref name="entry"/>'s body overlaps a protected section whose grant has lapsed.
    /// </summary>
    public void Check(StoredResult entry, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Grants.Grants.Count == 0)
        {
            return;
        }

        SettingsSnapshot? snapshot = null;
        var now = clock.GetUtcNow();
        foreach (var grant in entry.Grants.Grants)
        {
            if (!Overlaps(entry, grant, start, end))
            {
                continue;
            }

            snapshot ??= settings();
            if (!Holds(grant, snapshot, now))
            {
                throw new OneNoteException(
                    ErrorCode.ReadNotGranted,
                    $"This part of result {entry.ResultId} is in password-protected section {grant.SectionId}, and the read grant that allowed the original call has ended.",
                    userAction: ReadAccess.LapsedUserAction);
            }
        }
    }

    private static bool Holds(ResultGrant grant, SettingsSnapshot snapshot, DateTimeOffset now) => grant.Lifetime switch
    {
        // The entry itself expires ttlMinutes after the original call (AD-20).
        ReadGrantLifetime.Once => true,
        ReadGrantLifetime.Hours => grant.ExpiresAt is { } end && now < end && ReadAccess.Stored(snapshot, grant.SectionKey, now) is not null,
        ReadGrantLifetime.UntilRevoked => ReadAccess.Stored(snapshot, grant.SectionKey, now) is not null,
        _ => false,
    };

    private static bool Overlaps(StoredResult entry, ResultGrant grant, int start, int end)
    {
        if (grant.WholeResult)
        {
            return true;
        }

        // A section absent from the stored body contributed nothing to it.
        return entry.Tree.Find(grant.SectionId) is { } section && start < section.End && section.Start < end;
    }
}
