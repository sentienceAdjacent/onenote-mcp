using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Access;

/// <summary>
/// AD-21 "What's protected" for one call (entry 15): a section is protected when OneNote marks it <c>encrypted</c>, or when
/// its <c>path</c> (or, where the XML shows it, its notebook's) lies under a protected root: each <c>exportRoots</c> entry
/// and the journal's <c>backups</c> folder. An opened backup, export or package carries no trustworthy marker, so its
/// location decides (epic 3 Q11, H1). Every check that decides "protected", for reads, writes and journal snapshots alike
/// (Phill, 2026-11-17, 3.8 Q4), goes through this one value; the Xml layer takes <see cref="UnderRoot"/> as a required test.
/// <see cref="Warnings"/> carries what the rule ran on, when the reader should know (an invalid <c>settings.json</c>, an
/// export root that holds the user's notebooks).
/// </summary>
public sealed class Protection
{
    /// <summary>Appended to a refusal's message when the section is protected by its path only, so the caller learns why.</summary>
    public const string ByPathReason =
        " It is under an export root or the backups folder, so it is treated as password-protected.";

    /// <param name="roots">The protected roots; an empty list leaves the marker as the only rule.</param>
    /// <param name="warnings">Warnings about the roots themselves, attached to reads.</param>
    public Protection(IEnumerable<string> roots, IReadOnlyList<ToolWarning>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        Roots = [.. roots.Where(r => !string.IsNullOrWhiteSpace(r))];
        Warnings = warnings ?? [];
    }

    /// <summary>The protected roots: the export roots, then the journal's <c>backups</c> folder.</summary>
    public IReadOnlyList<string> Roots { get; }

    /// <summary>Warnings about the roots this value was built from (<c>settingsInvalid</c>, <c>exportRootHoldsNotebooks</c>).</summary>
    public IReadOnlyList<ToolWarning> Warnings { get; }

    /// <summary>The roots <paramref name="settings"/> names: its export roots and its journal's <c>backups</c> folder.</summary>
    public static IReadOnlyList<string> RootsOf(SettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return [.. settings.ExportRoots.Paths, Path.Combine(settings.Journal.Path, JournalFiles.BackupsFolderName)];
    }

    /// <summary>The rule on <paramref name="settings"/>'s roots, as the grant CLI and other one-off readers build it.</summary>
    public static Protection From(SettingsSnapshot settings) => new(RootsOf(settings));

    /// <summary>True when <paramref name="path"/> lies under a protected root (<see cref="PathUnder"/>).</summary>
    public bool UnderRoot(string? path) => PathUnder.IsUnder(path, Roots);

    /// <summary>True for a section that is marked <c>encrypted</c> or under a protected root. Anything else isn't protected.</summary>
    public bool IsProtected(HierarchyNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Kind == HierarchyKind.Section && (node.EncryptedMarker || UnderRoot(node.Path));
    }

    /// <summary>True for a section target that is marked or under a protected root.</summary>
    public bool IsProtected(HierarchyTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Kind == HierarchyKind.Section && (target.EncryptedMarker || UnderRoot(target.Path));
    }

    /// <summary>True for a notebook's section that is marked or under a protected root.</summary>
    public bool IsProtected(NotebookSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        return section.EncryptedMarker || UnderRoot(section.Path);
    }

    /// <summary>True for a section protected by its path alone (no marker): its messages say why, and advice that only fits a password is left out.</summary>
    public bool ByPathOnly(HierarchyNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Kind == HierarchyKind.Section && !node.EncryptedMarker && UnderRoot(node.Path);
    }

    /// <summary><see cref="ByPathReason"/> for a section protected by its path alone, else empty.</summary>
    public string Why(HierarchyNode node) => ByPathOnly(node) ? ByPathReason : string.Empty;

    /// <summary>
    /// How a refusal says a section is protected: "is password-protected" for a marked one, and for one protected by its
    /// path alone, "is under an export root or the backups folder, so it is treated as password-protected" (entry 15).
    /// </summary>
    public static string Phrase(bool marked) =>
        marked ? "is password-protected" : "is under an export root or the backups folder, so it is treated as password-protected";
}
