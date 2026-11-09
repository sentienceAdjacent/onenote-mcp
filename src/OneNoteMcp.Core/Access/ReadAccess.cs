using OneNoteMcp.Core.LargeResults;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Access;

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
/// <remarks>
/// Its parts: <see cref="TargetAccess"/> and <see cref="BroadAccess"/> (what a check decided), <see cref="HierarchyWalk"/>
/// (the parent walks), <see cref="OnceWindows"/>, <see cref="GrantDialog"/> (asking and saving) and
/// <see cref="ResultGrantCheck"/> (<c>read_result</c>'s check).
/// </remarks>
public sealed class ReadAccess
{
    public static string WithheldUserAction { get; } =
        "Reading a password-protected section needs the user's permission, which you can't give. Tell the user, and if they want it read, "
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
        this.clock = clock;
        walk = new HierarchyWalk(gateway);
        dialog = new GrantDialog(walk, windows, writer, clock);
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

        if (windows.IsOpen(key, now))
        {
            return new TargetAccess(new ResultGrant(key, section.Id, ReadGrantLifetime.Once, null, WholeResult: true), []);
        }

        return await dialog.AskAsync(context, section, key, snapshot.ReadGrants, ct).ConfigureAwait(false);
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
            else if (windows.IsOpen(key, now))
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
            var section = await walk.OwningSectionAsync(startNodeId!, ct).ConfigureAwait(false);
            if (section is not { Encrypted: true })
            {
                return BroadAccess.None;
            }

            // Every result is in the start page's section, so a grant for it covers the whole stored result (bug 1.22).
            var access = CheckBroad([new EncryptedSection(section.Id, section.Name, section.Path, section.Locked, [])], includesPages: true);
            return access.Withheld.Count > 0 ? access with { WithholdAll = true } : WholeResult(access, section.Id);
        }

        // A start node that is itself a protected section: every result is in it, whatever the search XML wraps them in.
        // Withheld, everything is; granted, the grant covers the whole stored result, since the section is its root,
        // whose ID a stored result's index never holds (AD-20, bug 1.22).
        var broad = CheckBroad(Readable(() => EncryptedSections.Find(xml)), includesPages: true);
        if (root.Kind != HierarchyKind.Section || !root.Encrypted)
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

    internal static OneNoteException NotGranted(string sectionId, string why) => new(
        ErrorCode.ReadNotGranted,
        $"Section {sectionId} is password-protected, and reading it needs a read grant from the user. {why}",
        userAction: ToolErrors.ReadNotGrantedUserAction);

    private static T Readable<T>(Func<T> read) => XmlReads.Readable(read, "GetHierarchy");
}
