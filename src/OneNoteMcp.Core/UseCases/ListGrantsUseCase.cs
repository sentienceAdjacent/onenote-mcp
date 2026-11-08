using System.Xml.Linq;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>list_grants</c>: a server-only <c>Read</c> tool (AD-7) that shows the stored grants and how each was created
/// (AD-6, AD-10). It never creates or changes one. For now these are AD-21's read grants, from
/// <c>protectedSections{}</c>, plus this process's live <c>once</c> grants (lifetime <c>once</c>, <c>grantedVia:
/// elicitation</c>, status <c>active</c>, <c>expiresAt</c> the window's end); expired windows aren't listed. A stored
/// grant is <c>active</c> (an open section has its key), <c>sectionNotOpen</c> or <c>expired</c>. With no stored grants it makes no COM call; otherwise one
/// <c>GetHierarchy</c> of every open notebook at <c>sections</c> level. Reads continue on an invalid or newer
/// <c>settings.json</c> (AD-10): it lists the entries it could read (those of a file invalid only for keys that differ in
/// case, so the user can see what to revoke) with one <c>settingsInvalid</c> warning saying they count as absent.
/// </summary>
public sealed class ListGrantsUseCase(IOneNoteGateway gateway, Func<SettingsSnapshot> settings, TimeProvider clock, ReadAccess? access = null) : IUseCase
{
    public const string ReadSectionKind = "readSection";

    public OperationClass Class => OperationClass.Read;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var snapshot = settings();
        var invalid = snapshot.Status is SettingsStatus.Invalid or SettingsStatus.NewerSchema;
        var grants = snapshot.ProtectedSectionEntries.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var open = grants.Count == 0 ? new HashSet<string>() : await OpenSectionKeysAsync(ct).ConfigureAwait(false);
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
        IReadOnlyList<ToolWarning> warnings = invalid
            ?
            [
                new ToolWarning(
                    ToolWarnings.SettingsInvalid,
                    snapshot.Status == SettingsStatus.NewerSchema
                        ? "settings.json was written by a newer version of OneNoteMcp, so its saved grants count as absent and none of them is listed. Live once grants (lifetime once) are in memory, not in the file, and still apply."
                        : $"settings.json is invalid ({snapshot.Reason}), so its saved grants count as absent: none of the listed grants with lifetime hours or untilRevoked applies. Live once grants (lifetime once) are in memory, not in the file, and still apply.",
                    snapshot.DuplicateGrantKeysOnly
                        ? "Two grants have keys that differ only in case. Ask the user to revoke that section's grant with " + GrantCli.Run("--revoke --read-section \"<section name>\"") + ", then grant it again if they want."
                        : "Ask the user to fix settings.json, or to update OneNoteMcp if a newer version wrote it."),
            ]
            : [];
        return new ToolSuccess(null, new ToolMeta(context.CorrelationId, Grants: listed), warnings);
    }

    private async Task<HashSet<string>> OpenSectionKeysAsync(CancellationToken ct)
    {
        var xml = await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Sections, ct).ConfigureAwait(false);
        try
        {
            return XDocument.Parse(xml)
                .Descendants(OneNoteXml.One + "Section")
                .Select(s => (string?)s.Attribute("path"))
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => GrantKeys.SectionKey(p!))
                .ToHashSet(GrantKeys.Comparer);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new OneNoteException(ErrorCode.InvalidXml, $"GetHierarchy returned XML the server can't read (line {ex.LineNumber}).", innerException: ex);
        }
    }
}
