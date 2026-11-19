using OneNoteMcp.Core.LargeResults;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Access;

/// <summary>
/// AD-21's one access check, run by every read use case before any content is returned and before the
/// <c>SectionLocked</c> check, and by <c>read_result</c>. A section is protected when its <c>one:Section</c> has
/// <c>encrypted</c> true, or its path lies under an export root or the journal's backups folder (<see cref="Protection"/>,
/// entry 15), read live from <c>GetHierarchy</c> on every call and never cached. A read grant is keyed by
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
/// <remarks>
/// Its parts: <see cref="TargetAccess"/> and <see cref="BroadAccess"/> (what a check decided), <see cref="HierarchyWalk"/>
/// (the parent walks), <see cref="OnceWindows"/>, <see cref="GrantDialog"/> (asking and saving) and
/// <see cref="ResultGrantCheck"/> (<c>read_result</c>'s check).
/// </remarks>
public sealed class ReadAccess
{
    public static string WithheldUserAction { get; } =
        "Reading a protected section (password-protected, or under the opened-copies folder, an export root or the backups folder) needs the user's permission, which you can't give. Tell the user, and if they want it read, "
        + "they can run " + GrantCli.Run("--read-section \"<section name>\"") + ", or accept the dialog that appears when you read one of its pages "
        + "(get_page_content) in a client that supports it, such as Claude Code. Then repeat this call.";

    public static string LapsedUserAction { get; } =
        "The read grant that allowed the original call has ended. Ask the user whether to grant it again: they can run "
        + GrantCli.Run("--read-section \"<section name>\"") + ", or accept the dialog when you repeat the original call. Then repeat the original call.";

    private readonly IOneNoteGateway gateway;
    private readonly Func<SettingsSnapshot> settings;
    private readonly TimeProvider clock;
    private readonly HierarchyWalk walk;
    private readonly OnceWindows windows = new();
    private readonly GrantDialog dialog;
    private readonly ExportRootGuard? guard;
    private readonly Lock rootsGate = new();
    private IReadOnlyList<string>? lastValidRoots;
    private IReadOnlyList<string>? lastValidExportRoots;
    private readonly Func<OneDriveFolders> oneDrive;

    /// <param name="gateway">The gateway the owning section is resolved through.</param>
    /// <param name="settings">Reads <c>settings.json</c>; called once per check.</param>
    /// <param name="writer">Stores <c>hours</c> and <c>untilRevoked</c> grants; null, every such grant becomes a <c>once</c> window (<c>grantNotSaved</c>).</param>
    /// <param name="clock">The clock grant expiry runs on.</param>
    /// <param name="guard">The export-root guard (entry 15 Q1); null leaves reads without its warning (tests that don't exercise it).</param>
    /// <param name="oneDrive">The OneDrive folders on this machine (backlog 13), asked once per check; null for none known.</param>
    public ReadAccess(IOneNoteGateway gateway, Func<SettingsSnapshot> settings, SettingsWriter? writer, TimeProvider clock, ExportRootGuard? guard = null, Func<OneDriveFolders>? oneDrive = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        this.gateway = gateway;
        this.settings = settings;
        this.clock = clock;
        walk = new HierarchyWalk(gateway);
        dialog = new GrantDialog(walk, windows, writer, clock);
        this.guard = guard;
        this.oneDrive = oneDrive ?? (() => OneDriveFolders.None);
    }

    /// <summary>
    /// AD-21's protection rule for this call (entry 15), from <c>settings.json</c> read now. While the file is invalid or
    /// newer, this run's last valid roots keep protecting; with none yet, the default export root and the default journal's
    /// backups folder do, with the warning <c>settingsInvalid</c> (Phill, 2026-11-17, Q2). Never reads OneNote.
    /// </summary>
    public Protection Protection()
    {
        var snapshot = settings();
        if (snapshot.Status is SettingsStatus.Ok or SettingsStatus.Absent)
        {
            var roots = Access.Protection.RootsOf(snapshot);
            lock (rootsGate)
            {
                lastValidRoots = roots;
                lastValidExportRoots = snapshot.ExportRoots.Paths;
            }

            return new Protection(roots, oneDrive: oneDrive());
        }

        IReadOnlyList<string>? remembered;
        lock (rootsGate)
        {
            remembered = lastValidRoots;
        }

        if (remembered is not null)
        {
            return new Protection(remembered, oneDrive: oneDrive());
        }

        return new Protection(
            Access.Protection.RootsOf(NoSettings()),
            [new ToolWarning(
                ToolWarnings.SettingsInvalid,
                $"settings.json is unreadable ({snapshot.Reason}), so only the default export folder, the opened-copies folder and the journal's backups folder count as protected locations.",
                "Fix settings.json, then retry.")],
            oneDrive());
    }

    /// <summary>
    /// <see cref="Protection()"/> plus the export-root guard's verdict (Q1) as the warning <c>exportRootHoldsNotebooks</c>.
    /// The root keeps protecting either way.
    /// </summary>
    public async Task<Protection> ProtectionAsync(CancellationToken ct)
    {
        var protection = Protection();

        // The guard checks the export roots that protect this call: this run's last valid ones while the file is invalid (Q2).
        IReadOnlyList<string> exportRoots;
        lock (rootsGate)
        {
            exportRoots = lastValidExportRoots ?? NoSettings().ExportRoots.Paths;
        }

        // Backlog 13 Q3: a root too close to a OneDrive folder is reported as settings.json's problem; the root still protects.
        if (ExportRootGuard.ShallowProblem(exportRoots, protection.OneDrive) is { } shallow)
        {
            return protection.WithWarnings(
                [.. protection.Warnings, new ToolWarning(ToolWarnings.SettingsInvalid, shallow, "Tell the user, and ask them to change exportRoots in settings.json.")]);
        }

        if (guard is null || await guard.ProblemAsync(exportRoots, ct).ConfigureAwait(false) is not { } problem)
        {
            return protection;
        }

        return protection.WithWarnings(
            [.. protection.Warnings, new ToolWarning(ToolWarnings.ExportRootHoldsNotebooks, problem, "Tell the user, and ask them to change exportRoots in settings.json.")]);
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
    public IReadOnlyList<ProtectedSectionGrant> OnceGrants() => windows.Live(clock.GetUtcNow());

    /// <summary>
    /// A target-scoped read of <paramref name="objectId"/> (a page, or any hierarchy object): resolves the section that
    /// owns it through a <c>GetHierarchyParent</c> chain (AD-6), then that section's <c>encrypted</c> and <c>path</c>
    /// from <c>GetHierarchy</c>. An object that isn't inside a section (a section, section group or notebook) needs no
    /// grant. Throws <see cref="ErrorCode.ReadNotGranted"/> when the section is protected and the user doesn't grant.
    /// </summary>
    public async Task<TargetAccess> CheckObjectAsync(ToolCallContext context, string objectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var section = await walk.OwningSectionAsync(objectId, ct).ConfigureAwait(false);
        var protection = await ProtectionAsync(ct).ConfigureAwait(false);
        return section is not null && protection.IsProtected(section)
            ? With(await CheckProtectedAsync(context, section, ct).ConfigureAwait(false), protection.Warnings)
            : new TargetAccess(null, protection.Warnings);
    }

    /// <summary>
    /// A target-scoped read of the protected section <paramref name="section"/> itself (<c>get_hierarchy</c> at scope
    /// <c>children</c> or <c>pages</c>), or of something inside it.
    /// </summary>
    public async Task<TargetAccess> CheckSectionAsync(ToolCallContext context, HierarchyNode section, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(section);
        var protection = await ProtectionAsync(ct).ConfigureAwait(false);
        return protection.IsProtected(section)
            ? With(await CheckProtectedAsync(context, section, ct).ConfigureAwait(false), protection.Warnings)
            : new TargetAccess(null, protection.Warnings);
    }

    private static TargetAccess With(TargetAccess access, IReadOnlyList<ToolWarning> warnings) =>
        warnings.Count == 0 ? access : access with { Warnings = [.. warnings, .. access.Warnings] };

    /// <summary>The grant check for a section already known to be protected.</summary>
    private async Task<TargetAccess> CheckProtectedAsync(ToolCallContext context, HierarchyNode section, CancellationToken ct)
    {
        var key = GrantKeys.SectionKey(section.Path);
        var snapshot = settings();
        var now = clock.GetUtcNow();
        if (Stored(snapshot, key, now) is { } stored)
        {
            return new TargetAccess(Allowing(stored, section.Id, wholeResult: true), []);
        }

        if (windows.IsOpen(key, now))
        {
            return new TargetAccess(new ResultGrant(key, section.Id, ReadGrantLifetime.Once, null, WholeResult: true), []);
        }

        return await dialog.AskAsync(context, section, key, snapshot.ReadGrants, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// For a write's own reads (AD-7, AD-21): true when <paramref name="section"/> isn't protected, or when
    /// <see cref="HoldsReadGrantGivenBy"/> holds for it at <paramref name="at"/> (the write call's start; now when null).
    /// Never asks and never stores. Without one, a <c>PageWrite</c>'s refusals and warnings name nothing inside the page.
    /// </summary>
    public bool HoldsReadGrant(HierarchyNode section, DateTimeOffset? at = null)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (!Protection().IsProtected(section))
        {
            return true;
        }

        return HoldsReadGrantGivenBy(GrantKeys.SectionKey(section.Path), at ?? clock.GetUtcNow());
    }

    /// <summary>
    /// The one rule for a write that needs a read grant held at the call's start (AD-7, AD-21; backlog 9): true when a
    /// stored read grant or a <c>once</c> window for the section key <paramref name="key"/> was given at or before
    /// <paramref name="at"/> (its <see cref="ProtectedSectionGrant.GrantedAt"/>) and is unexpired then. A grant saved, or
    /// a window opened, after <paramref name="at"/> doesn't count, a grant renewed during the call included (Phill,
    /// 2026-11-17, Q1 a), so the write answers as if there were none and a retry passes. A stored <c>grantedAt</c> in the
    /// future counts for no write until the clock passes it. Never asks and never stores.
    /// </summary>
    public bool HoldsReadGrantGivenBy(string key, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(key);
        return (Stored(settings(), key, at) is { } stored && stored.GrantedAt <= at) || windows.OpenedBy(key, at);
    }

    /// <summary>
    /// For a check that must not miss a grant (story 5.4 and 3.12 security reviews): true when a stored read grant
    /// (<c>hours</c> not yet expired, or <c>untilRevoked</c>) or a live <c>once</c> window is held at <paramref name="at"/>
    /// (now when null), whenever it was given, for <paramref name="key"/> or any key that names the same location by
    /// <see cref="GrantKeys.SameLocation"/>, percent-encoded or not, whatever section is at that location now, or none.
    /// A rename or move checks where it would put sections, and where they read back, with it, so our own write never
    /// hands one section's grant to another (AD-21). Never asks, never stores, and never allows a read.
    /// </summary>
    public bool HoldsReadGrantAtLocation(string key, DateTimeOffset? at = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        var now = at ?? clock.GetUtcNow();
        var snapshot = settings();
        if (Stored(snapshot, key, now) is not null || windows.IsOpen(key, now))
        {
            return true;
        }

        return snapshot.ProtectedSections.Values.Any(g => !g.IsExpired(now) && GrantKeys.SameLocation(g.Key, key))
            || windows.Live(now).Any(w => GrantKeys.SameLocation(w.Key, key));
    }

    /// <summary>The current time on this check's clock: a write records it at its start, for <see cref="HoldsReadGrant"/>.</summary>
    public DateTimeOffset Now() => clock.GetUtcNow();

    /// <summary>
    /// A broad read (AD-21): <paramref name="sections"/> are the protected sections in its scope, found with
    /// <see cref="EncryptedSections"/> under <paramref name="protection"/>'s roots, whose warnings the result carries. Never asks. <paramref name="includesPages"/> is false for a read that returns no
    /// pages at all (a scope above pages), which then withholds nothing and needs no grant.
    /// </summary>
    public BroadAccess CheckBroad(IReadOnlyList<EncryptedSection> sections, bool includesPages, Protection protection)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(protection);
        if (!includesPages || sections.Count == 0)
        {
            return protection.Warnings.Count == 0 ? BroadAccess.None : BroadAccess.None with { Extra = protection.Warnings };
        }

        var snapshot = settings();
        var now = clock.GetUtcNow();
        var withheld = new List<string>();
        var byPath = new List<string>();
        var granted = new List<ResultGrant>();
        foreach (var section in sections)
        {
            var key = GrantKeys.SectionKey(section.Path);
            if (Stored(snapshot, key, now) is { } stored)
            {
                granted.Add(Allowing(stored, section.Id, wholeResult: false));
            }
            else if (windows.IsOpen(key, now))
            {
                granted.Add(new ResultGrant(key, section.Id, ReadGrantLifetime.Once, null, WholeResult: false));
            }
            else if (!withheld.Contains(section.Id, StringComparer.Ordinal))
            {
                withheld.Add(section.Id);
                if (!section.Marked)
                {
                    byPath.Add(section.Id);
                }
            }
        }

        return new BroadAccess(withheld, granted) { Extra = protection.Warnings, ByPath = byPath };
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
        var protection = await ProtectionAsync(ct).ConfigureAwait(false);
        if (root.Kind == HierarchyKind.Page)
        {
            var section = await walk.OwningSectionAsync(startNodeId!, ct).ConfigureAwait(false);
            if (section is null || !protection.IsProtected(section))
            {
                return BroadAccess.None with { Extra = protection.Warnings };
            }

            // Every result is in the start page's section, so a grant for it covers the whole stored result (bug 1.22).
            var access = CheckBroad([new EncryptedSection(section.Id, section.Name, section.Path, section.Locked, [], Marked: section.EncryptedMarker)], includesPages: true, protection);
            return access.Withheld.Count > 0 ? access with { WithholdAll = true } : WholeResult(access, section.Id);
        }

        // A start node that is itself a protected section: every result is in it, whatever the search XML wraps them in.
        // Withheld, everything is; granted, the grant covers the whole stored result, since the section is its root,
        // whose ID a stored result's index never holds (AD-20, bug 1.22).
        var broad = CheckBroad(Readable(() => EncryptedSections.Find(xml, protection.UnderRoot)), includesPages: true, protection);
        if (root.Kind != HierarchyKind.Section || !protection.IsProtected(root))
        {
            return broad;
        }

        return broad.Withheld.Contains(root.Id, StringComparer.Ordinal) ? broad with { WithholdAll = true } : WholeResult(broad, root.Id);
    }

    /// <summary><paramref name="access"/> with the grant for <paramref name="sectionId"/>, if any, covering the whole result.</summary>
    private static BroadAccess WholeResult(BroadAccess access, string sectionId) => access with
    {
        Granted = [.. access.Granted.Select(g => string.Equals(g.SectionId, sectionId, StringComparison.Ordinal) ? g with { WholeResult = true } : g)],
    };

    /// <summary>
    /// The section that owns <paramref name="objectId"/>: up the <c>GetHierarchyParent</c> chain to the first
    /// <c>one:Section</c>. Null when the chain reaches a section group or notebook first, or ends (an empty parent, AD-6).
    /// An exception from either call is an ordinary error and never ends the chain.
    /// </summary>
    public Task<HierarchyNode?> OwningSectionAsync(string objectId, CancellationToken ct) => walk.OwningSectionAsync(objectId, ct);

    /// <summary>The stored grant for <paramref name="key"/> that still holds at <paramref name="now"/>, or null.</summary>
    internal static ProtectedSectionGrant? Stored(SettingsSnapshot snapshot, string key, DateTimeOffset now) =>
        snapshot.ProtectedSections.TryGetValue(key, out var grant) && !grant.IsExpired(now) ? grant : null;

    internal static ResultGrant Allowing(ProtectedSectionGrant stored, string sectionId, bool wholeResult) => new(
        stored.Key,
        sectionId,
        stored.ExpiresAt is null ? ReadGrantLifetime.UntilRevoked : ReadGrantLifetime.Hours,
        stored.ExpiresAt,
        wholeResult);

    internal static OneNoteException NotGranted(HierarchyNode section, string why) => new(
        ErrorCode.ReadNotGranted,
        $"Section {section.Id} {Access.Protection.Phrase(section.EncryptedMarker)}, and reading it needs a read grant from the user. {why}",
        userAction: ToolErrors.ReadNotGrantedUserAction);

    private static T Readable<T>(Func<T> read) => XmlReads.Readable(read, "GetHierarchy");
}
