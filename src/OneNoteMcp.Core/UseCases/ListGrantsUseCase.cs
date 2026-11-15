using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>list_grants</c>: a server-only <c>Read</c> tool (AD-7) that shows the stored grants and how each was created
/// (AD-6, AD-10). It never creates or changes one. It lists, in this order: AD-21's read grants, from
/// <c>protectedSections{}</c> (kind <c>readSection</c>); this process's live <c>once</c> grants (lifetime <c>once</c>,
/// <c>grantedVia: elicitation</c>, status <c>active</c>, <c>expiresAt</c> the window's end; expired windows aren't
/// listed); and AD-6's write grants, one row per <c>notebooks{}</c> entry with <c>write: true</c> (kind
/// <c>writeNotebook</c>, <c>notebookKey</c> its own key, lifetime <c>untilRevoked</c>); and the permanent-delete grants, one
/// row per such entry that also holds <c>permanentDelete: true</c> (kind <c>permanentDeleteNotebook</c>, otherwise like its
/// write row; story 5.6). A stored read grant is
/// <c>active</c> (an open section has its key), <c>sectionNotOpen</c> or <c>expired</c>; a write grant is <c>active</c>
/// (an open notebook has its key) or <c>notebookNotOpen</c>. With no stored grants it makes no COM call; otherwise one
/// <c>GetHierarchy</c> of every open notebook at <c>sections</c> level. Reads continue on an invalid or newer
/// <c>settings.json</c> (AD-10): it lists the entries it could read (those of a file invalid only for duplicate grant
/// keys, so the user can see what to revoke) with one <c>settingsInvalid</c> warning saying they count as absent.
/// </summary>
public sealed class ListGrantsUseCase(IOneNoteGateway gateway, Func<SettingsSnapshot> settings, TimeProvider clock, ReadAccess? access = null) : IUseCase
{
    public const string ReadSectionKind = "readSection";

    /// <summary>The kind of a write grant's row (Phill, 2026-11-12, 2.6 plan Q3).</summary>
    public const string WriteNotebookKind = "writeNotebook";

    /// <summary>The kind of a permanent-delete grant's row (Phill, 2026-11-13, 5.6 plan Q2).</summary>
    public const string PermanentDeleteNotebookKind = "permanentDeleteNotebook";

    public OperationClass Class => OperationClass.Read;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var snapshot = settings();
        var invalid = snapshot.Status is SettingsStatus.Invalid or SettingsStatus.NewerSchema;
        var grants = snapshot.ProtectedSectionEntries.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var writeGrants = snapshot.NotebookEntries.Where(g => g.Write).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var (open, openNotebooks) = grants.Count == 0 && writeGrants.Count == 0
            ? ([], [])
            : await OpenKeysAsync(ct).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var listed = grants
            .Select(g => new GrantListing(
                ReadSectionKind,
                g.Key,
                g.Name,
                g.NotebookKey,
                g.IdAtGrant,
                SettingsWriter.Format(g.GrantedAt),
                g.GrantedVia,
                g.Lifetime,
                g.IsExpired(now) ? "expired" : open.Contains(g.Key) ? "active" : "sectionNotOpen",
                g.ExpiresAt is { } end ? SettingsWriter.Format(end) : null))
            .ToList();

        // This process's live once windows: memory only, never stored (AD-21).
        listed.AddRange((access?.OnceGrants() ?? [])
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GrantListing(
                ReadSectionKind,
                g.Key,
                g.Name,
                g.NotebookKey,
                g.IdAtGrant,
                SettingsWriter.Format(g.GrantedAt),
                g.GrantedVia,
                ReadGrantLifetimes.Once,
                "active",
                g.ExpiresAt is { } end ? SettingsWriter.Format(end) : null)));
        // Write grants (AD-6): no lifetime, so untilRevoked; active while an open notebook has the key.
        listed.AddRange(writeGrants.Select(g => new GrantListing(
            WriteNotebookKind,
            g.Key,
            g.Name,
            g.Key,
            g.IdAtGrant,
            SettingsWriter.Format(g.GrantedAt),
            g.GrantedVia,
            ReadGrantLifetimes.UntilRevoked,
            openNotebooks.Contains(g.Key) ? "active" : "notebookNotOpen")));

        // Permanent-delete grants (AD-6; 5.6 plan Q2 b): their own row, only beside the write grant they count with. Only the
        // CLI stores one (Q1 c), so its grantedVia is always cli, whatever later relabelled the write grant's entry.
        listed.AddRange(writeGrants.Where(g => g.PermanentDelete).Select(g => new GrantListing(
            PermanentDeleteNotebookKind,
            g.Key,
            g.Name,
            g.Key,
            g.IdAtGrant,
            SettingsWriter.Format(g.GrantedAt),
            GrantedVia.Cli,
            ReadGrantLifetimes.UntilRevoked,
            openNotebooks.Contains(g.Key) ? "active" : "notebookNotOpen")));
        IReadOnlyList<ToolWarning> warnings = invalid
            ?
            [
                new ToolWarning(
                    ToolWarnings.SettingsInvalid,
                    snapshot.Status == SettingsStatus.NewerSchema
                        ? "settings.json was written by a newer version of OneNoteMcp, so its saved grants count as absent and none of them is listed. Live once grants (lifetime once) are in memory, not in the file, and still apply."
                        : $"settings.json is invalid ({snapshot.Reason}), so its saved grants count as absent: none of the listed grants with lifetime hours or untilRevoked applies. Live once grants (lifetime once) are in memory, not in the file, and still apply.",
                    DuplicateKeyAdvice(snapshot) ?? "Ask the user to fix settings.json, or to update OneNoteMcp if a newer version wrote it."),
            ]
            : [];
        return new ToolSuccess(null, new ToolMeta(context.CorrelationId, Grants: listed), warnings);
    }

    /// <summary>
    /// The <c>settingsInvalid</c> advice for a file invalid only for duplicate grant keys: which revoke fixes it, by area
    /// (AD-6, 2.3 plan Q7). Null for any other invalid file.
    /// </summary>
    private static string? DuplicateKeyAdvice(SettingsSnapshot snapshot)
    {
        var revokes = new List<string>();
        if (snapshot.DuplicateSectionKeys)
        {
            revokes.Add("that section's read grant with " + GrantCli.Run("--revoke --read-section \"<section name>\""));
        }

        if (snapshot.DuplicateNotebookKeys)
        {
            revokes.Add("that notebook's write grant with " + GrantCli.Run("--revoke --write \"<notebook name>\""));
        }

        return revokes.Count == 0
            ? null
            : "Two grants have the same key (exactly, or differing only in case). Ask the user to revoke "
                + string.Join(", and ", revokes) + ", then grant it again if they want.";
    }

    /// <summary>
    /// One <c>GetHierarchy</c> of every open notebook at <c>sections</c> level: the <c>SectionKey</c> of every open
    /// section, and the <c>NotebookKey</c> of every open notebook with a path.
    /// </summary>
    private async Task<(HashSet<string> Sections, HashSet<string> Notebooks)> OpenKeysAsync(CancellationToken ct)
    {
        var xml = await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Sections, ct).ConfigureAwait(false);
        var sections = XmlReads.Readable(() => SectionPaths.Find(xml), "GetHierarchy")
            .Select(GrantKeys.SectionKey)
            .ToHashSet(GrantKeys.Comparer);
        var notebooks = OpenNotebooks.Find(xml)
            .Where(n => n.Key.Length > 0)
            .Select(n => n.Key)
            .ToHashSet(GrantKeys.Comparer);
        return (sections, notebooks);
    }
}
