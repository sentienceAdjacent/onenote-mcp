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
/// <para>
/// Backlog 13 (Phill, 2026-11-18): OneNote may report a notebook in OneDrive by web address, so a root also covers URLs.
/// A root inside a known OneDrive folder (<see cref="OneDriveFolders"/>) has a <i>tail</i>, its path below that folder;
/// with no OneDrive folder known, its own last two segments are its tail (Q2 (ii)). An <c>http</c>/<c>https</c> path
/// (<see cref="UrlPath"/>) is under the root when it holds the tail as whole segments with at least one segment after it,
/// whatever the host or account (Q1 (ii): the tail always applies; an account's exact address is always covered by it).
/// It fails closed: a same-named folder in another OneDrive counts too. A root outside every known OneDrive folder keeps
/// the local rule only.
/// </para>
/// </summary>
public sealed class Protection
{
    /// <summary>Appended to a refusal's message when the section is protected by its path only, so the caller learns why.</summary>
    public const string ByPathReason =
        " It is under the opened-copies folder, an export root or the backups folder, so it is treated as password-protected.";

    /// <param name="roots">The protected roots; an empty list leaves the marker as the only rule.</param>
    /// <param name="warnings">Warnings about the roots themselves, attached to reads.</param>
    /// <param name="oneDrive">The OneDrive folders on this machine; null for <see cref="OneDriveFolders.None"/>.</param>
    public Protection(IEnumerable<string> roots, IReadOnlyList<ToolWarning>? warnings = null, OneDriveFolders? oneDrive = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        Roots = [.. roots.Where(r => !string.IsNullOrWhiteSpace(r))];
        Warnings = warnings ?? [];
        OneDrive = oneDrive ?? OneDriveFolders.None;

        // Made canonical once here, not once per section (a broad read tests every section of every notebook).
        var forms = Roots.Select(r => (Root: r, Prefixes: PathUnder.Prefixes(r))).ToList();
        prefixes = [.. forms.SelectMany(f => f.Prefixes).Distinct(StringComparer.OrdinalIgnoreCase)];
        tails = Tails(forms, OneDrive);
    }

    private readonly IReadOnlyList<string> prefixes;
    private readonly IReadOnlyList<(string Root, IReadOnlyList<string> Tail)> tails;

    /// <summary>The protected roots: the export roots, then the journal's <c>backups</c> folder.</summary>
    public IReadOnlyList<string> Roots { get; }

    /// <summary>Warnings about the roots this value was built from (<c>settingsInvalid</c>, <c>exportRootHoldsNotebooks</c>).</summary>
    public IReadOnlyList<ToolWarning> Warnings { get; }

    /// <summary>The OneDrive folders this rule was built with, so a copy with more warnings keeps them.</summary>
    public OneDriveFolders OneDrive { get; }

    /// <summary>
    /// The local path below a protected root that the web address <paramref name="url"/> stands for, by the first root
    /// whose tail it holds (the root plus the segments after the tail), or null when it is under no root's tail or the
    /// result would leave the root.
    /// </summary>
    public string? LocalPathOfUrl(string? url)
    {
        foreach (var segments in UrlPath.Readings(url))
        {
            foreach (var (root, tail) in tails)
            {
                if (UrlPath.IndexOfTail(segments, tail) is var at and >= 0)
                {
                    return UrlPath.Below(root, segments.Skip(at + tail.Count));
                }
            }
        }

        return null;
    }

    /// <summary>Copies this rule with <paramref name="warnings"/> in place of its own.</summary>
    public Protection WithWarnings(IReadOnlyList<ToolWarning> warnings) => new(Roots, warnings, OneDrive);

    /// <summary>The roots <paramref name="settings"/> names: its export roots, its journal's <c>backups</c> folder, and the opened-copies folder (story 3.8 Q3).</summary>
    public static IReadOnlyList<string> RootsOf(SettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return [.. settings.ExportRoots.Paths, Path.Combine(settings.Journal.Path, JournalFiles.BackupsFolderName), settings.OpenedCopies];
    }

    /// <summary>The rule on <paramref name="settings"/>'s roots and the OneDrive folders <paramref name="oneDrive"/>, as the grant CLI and other one-off readers build it.</summary>
    public static Protection From(SettingsSnapshot settings, OneDriveFolders? oneDrive = null) => new(RootsOf(settings), oneDrive: oneDrive);

    /// <summary>True when <paramref name="path"/> lies under a protected root: a local path by <see cref="PathUnder"/>, a web address by a root's tail (backlog 13).</summary>
    public bool UnderRoot(string? path) =>
        PathUnder.IsUnderPrefixes(path, prefixes)
        || (tails.Count > 0 && UrlPath.Readings(path).Any(segments => tails.Any(t => UrlPath.IndexOfTail(segments, t.Tail) >= 0)));

    /// <summary>Each root's tails (see the class remarks), with the root's plain full path to map a URL back to.</summary>
    private static List<(string Root, IReadOnlyList<string> Tail)> Tails(IReadOnlyList<(string Root, IReadOnlyList<string> Prefixes)> roots, OneDriveFolders oneDrive)
    {
        var found = new List<(string Root, IReadOnlyList<string> Tail)>();
        foreach (var (root, forms) in roots)
        {
            if (PathUnder.Full(root) is not { } full)
            {
                continue;
            }

            foreach (var form in forms)
            {
                if (oneDrive.Accounts.Count == 0)
                {
                    Add(full, [.. UrlPath.LocalSegments(form).TakeLast(2)]);
                    continue;
                }

                foreach (var (_, folderForms) in oneDrive.FolderPrefixes)
                {
                    foreach (var folder in folderForms.Where(f => form.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
                    {
                        Add(full, UrlPath.LocalSegments(form[folder.Length..]));
                    }
                }
            }
        }

        return found;

        void Add(string root, IReadOnlyList<string> tail)
        {
            // An empty tail (a root that is a OneDrive folder; settings.json refuses one, Q3) covers every address: fails closed.
            if (!found.Any(f => string.Equals(f.Root, root, StringComparison.OrdinalIgnoreCase) && f.Tail.SequenceEqual(tail, StringComparer.OrdinalIgnoreCase)))
            {
                found.Add((root, tail));
            }
        }
    }

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
    /// path alone, "is under the opened-copies folder, an export root or the backups folder, so it is treated as password-protected" (entry 15).
    /// </summary>
    public static string Phrase(bool marked) =>
        marked ? "is password-protected" : "is under the opened-copies folder, an export root or the backups folder, so it is treated as password-protected";
}
