using System.Text.RegularExpressions;
using OneNoteMcp.Core.Writes;

namespace OneNoteMcp.Core.Journal;

/// <summary>
/// Which attachment and media <c>pathCache</c>s <c>restore_from_journal</c> sends back to OneNote (Phill, 2026-11-12, 2.11
/// plan Q2, with guardrails): only OneNote's own temporary copy, so nothing a page's author named is ever read. A
/// <c>pathCache</c> is kept when its canonical form (<see cref="FilePathGate.Canonical(string, out FilePathRefusal)"/>,
/// which refuses network, device and relative paths and resolves links) is a file directly in the current user's temp
/// folder (made canonical the same way), named <c>{GUID}.bin</c> as OneNote names it, that exists now. Paths never go into
/// messages or logs.
/// </summary>
public static partial class RestoreAttachments
{
    /// <summary><see cref="Keeps(string, string)"/> against <see cref="Path.GetTempPath"/>.</summary>
    public static bool Keeps(string pathCache) => Keeps(pathCache, Path.GetTempPath());

    /// <summary>True when <paramref name="pathCache"/> may be sent: a <c>{GUID}.bin</c> file directly in <paramref name="tempFolder"/>, present now.</summary>
    public static bool Keeps(string pathCache, string tempFolder)
    {
        if (string.IsNullOrEmpty(pathCache) || string.IsNullOrEmpty(tempFolder))
        {
            return false;
        }

        var file = FilePathGate.Canonical(pathCache, out _);
        var folder = FilePathGate.Canonical(tempFolder, out _);
        if (file is null || folder is null)
        {
            return false;
        }

        var parent = Path.GetDirectoryName(file);
        return parent is not null
            && string.Equals(parent.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            && CacheName().IsMatch(Path.GetFileName(file))
            && File.Exists(file);
    }

    /// <summary>OneNote's temporary copy of an inserted file: <c>{GUID}.bin</c>.</summary>
    [GeneratedRegex(@"^\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}\.bin$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CacheName();
}
