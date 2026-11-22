using System.Globalization;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Writes;

/// <summary>
/// The best-effort re-read after <c>0x80042004</c> on a page move, a section or section-group move and a restore from the
/// recycle bin (AD-7 Moves; backlog 17): one copy of what the three writes each carried before. The write calls it from its
/// own catch, under the lock and after its cloud settle (3.12 Q4), and adds the sentence to the original failure's message;
/// a re-read that fails only adds to that sentence. IDs and counts only, never a name (AD-9); in a password-protected target,
/// the count of new copies without their IDs (AD-21 Messages).
/// </summary>
internal static class PartialCopyReRead
{
    /// <summary>hrSectionDoesNotExist (and friends): on a move or restore it may have left a partial copy (AD-7 Moves, AD-9).</summary>
    internal const int HResult = unchecked((int)0x80042004);

    /// <summary>
    /// Re-reads <paramref name="source"/> (when given) for <paramref name="objectId"/> by ID, then <paramref name="target"/> for
    /// the object by ID and for new children of <paramref name="kind"/> named <paramref name="name"/>: children whose IDs the
    /// target didn't hold before and that aren't the object itself. Page names compare ordinally (same-named pages are allowed
    /// and a copy keeps the exact title); section and section-group names ignoring case, as OneNote refuses a sibling's name
    /// that way (backlog 17 Q1). Returns the sentence the write adds to its failure, starting "The <paramref name="verb"/> may
    /// have left a partial copy".
    /// </summary>
    internal static async Task<string> FindingsAsync(
        IOneNoteGateway gateway,
        Protection protection,
        string verb,
        HierarchyTarget? source,
        HierarchyTarget target,
        string objectId,
        HierarchyKind kind,
        string name,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(target);
        var noun = HierarchyRules.Noun(kind);
        var found = new List<string>();
        if (source is not null)
        {
            found.Add(await SourceAsync(gateway, source, objectId, noun, ct).ConfigureAwait(false));
        }

        found.Add(await TargetAsync(gateway, protection, target, objectId, kind, name, ct).ConfigureAwait(false));
        return $"The {verb} may have left a partial copy. Re-read straight after the failure: {string.Join(", and ", found)}; a copy in a cloud notebook can land later.";
    }

    /// <summary>The comparison for a name of <paramref name="kind"/>: ordinal for a page, ignoring case for a section or group (Q1).</summary>
    internal static StringComparison NameComparison(HierarchyKind kind) =>
        kind == HierarchyKind.Page ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static async Task<string> SourceAsync(IOneNoteGateway gateway, HierarchyTarget source, string objectId, string noun, CancellationToken ct)
    {
        var where = $"{HierarchyRules.Noun(source.Kind)} {source.Id}";
        try
        {
            var after = await HierarchyWriteSteps.ReadChildrenAsync(gateway, source.Id, ct).ConfigureAwait(false);
            return Holds(after, objectId)
                ? $"{noun} {objectId} is still in {where}"
                : $"{noun} {objectId} is no longer in {where}";
        }
#pragma warning disable CA1031 // Best effort: the original failure is what the caller gets; the re-read only adds to its message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return $"{where} couldn't be re-read ({MoveReadBack.Why(ex)})";
        }
    }

    private static async Task<string> TargetAsync(
        IOneNoteGateway gateway, Protection protection, HierarchyTarget target, string objectId, HierarchyKind kind, string name, CancellationToken ct)
    {
        var noun = HierarchyRules.Noun(kind);
        var where = $"{HierarchyRules.Noun(target.Kind)} {target.Id}";
        try
        {
            var after = await HierarchyWriteSteps.ReadChildrenAsync(gateway, target.Id, ct).ConfigureAwait(false);
            var before = target.Children.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            var comparison = NameComparison(kind);
            var copies = after.Children
                .Where(c => c.Kind == kind && !before.Contains(c.Id) && !string.Equals(c.Id, objectId, StringComparison.Ordinal)
                    && string.Equals(c.Name, name, comparison))
                .Select(c => c.Id)
                .ToList();
            var there = Holds(after, objectId) ? "is in" : "isn't in";
            var news = copies.Count switch
            {
                0 => $"no new {noun} with its name",
                // In a password-protected target, a count only: nothing inside it is named (AD-21 Messages).
                _ when protection.IsProtected(target) => string.Create(CultureInfo.InvariantCulture, $"{copies.Count} new {noun}(s) with its name"),
                _ => string.Create(CultureInfo.InvariantCulture, $"{copies.Count} new {noun}(s) with its name ({string.Join(", ", copies)})"),
            };
            return $"{noun} {objectId} {there} {where}, which has {news}";
        }
#pragma warning disable CA1031 // As above.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return $"{where} couldn't be re-read ({MoveReadBack.Why(ex)})";
        }
    }

    private static bool Holds(HierarchyTarget after, string objectId) =>
        after.Children.Any(c => string.Equals(c.Id, objectId, StringComparison.Ordinal));
}
