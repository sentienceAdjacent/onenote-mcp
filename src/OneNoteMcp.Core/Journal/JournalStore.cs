using System.Globalization;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OneNoteMcp.Core.Settings;

namespace OneNoteMcp.Core.Journal;

/// <summary>
/// What <see cref="JournalStore.Write"/> stored: the entry's ID, the tool call that took it (AD-18), its file's full path,
/// and the file's size in bytes.
/// </summary>
public sealed record JournalWritten(Guid EntryId, string CorrelationId, string Path, long Bytes, bool Protected);

/// <summary>
/// The one Core type that owns journal entry files (AD-8, AD-10). <see cref="Write"/> stores one entry as gzip JSON in
/// the folder <c>journal.path</c> names, re-read from <c>settings.json</c> at every write: written once to
/// <c>.{name}.tmp</c> in that folder, flushed, then renamed. An entry with a <see cref="JournalEntry.SectionKey"/> is from
/// a password-protected section: its title and XML are encrypted with DPAPI for the current user before they reach disk.
/// Any failure to build or store the entry is <see cref="ErrorCode.JournalFailed"/>, whose message carries the entry's ID,
/// sizes and the exception's type only, never a path or content (AD-9, AD-13).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class JournalStore
{
    private const string EntropyPrefix = "OneNoteMcp.journal:";

    private readonly Func<SettingsSnapshot> settings;
    private readonly Action<JournalWritten>? written;

    /// <param name="settings">Reads <c>settings.json</c>, once per write, for <c>journal.path</c>.</param>
    /// <param name="written">Called after each stored entry, for the log (IDs and sizes only).</param>
    public JournalStore(Func<SettingsSnapshot> settings, Action<JournalWritten>? written = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = settings;
        this.written = written;
    }

    /// <summary>
    /// Stores <paramref name="entry"/> and returns where. Its <see cref="JournalEntry.Title"/> and
    /// <see cref="JournalEntry.Xml"/> are given in the clear; when it has a <see cref="JournalEntry.SectionKey"/>, they are
    /// encrypted into <see cref="JournalEntry.Protected"/> and left out of the file. Throws only
    /// <see cref="ErrorCode.JournalFailed"/>; a temporary file left by a failure is removed when possible, and the pruner
    /// removes any it couldn't.
    /// </summary>
    public JournalWritten Write(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var folder = settings().Journal.Path;
        var name = JournalFiles.EntryName(entry.TakenAtUtc, Environment.ProcessId, entry.EntryId);
        var final = Path.Combine(folder, name);
        var temp = Path.Combine(folder, JournalFiles.TempName(name));
        var isProtected = entry.SectionKey is not null;
        JournalWritten result;
        try
        {
            var stored = isProtected ? Protect(entry) : entry with { Protected = null };
            Directory.CreateDirectory(folder);
            long bytes;
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true))
                {
                    JsonSerializer.Serialize(gzip, stored, JournalEntry.JsonOptions);
                }

                file.Flush(flushToDisk: true);
                bytes = file.Length;
            }

            File.Move(temp, final, overwrite: false);
            result = new JournalWritten(entry.EntryId, entry.CorrelationId, final, bytes, isProtected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or NotSupportedException or ArgumentException)
        {
            TryDelete(temp);
            throw Failed(entry.EntryId, "written", ex);
        }

        // After the try: the entry is stored, so nothing the callback throws may turn it into a refused write.
        written?.Invoke(result);
        return result;
    }

    /// <summary>
    /// Reads the entry file at <paramref name="path"/>: decompresses it, and for a protected entry decrypts its title and
    /// XML back into <see cref="JournalEntry.Title"/> and <see cref="JournalEntry.Xml"/> (and clears
    /// <see cref="JournalEntry.Protected"/>). Members it doesn't know are ignored. Throws
    /// <see cref="ErrorCode.JournalFailed"/>, or <see cref="ErrorCode.NotSupported"/> for an entry whose
    /// <c>schemaVersion</c> is newer than <see cref="JournalEntry.CurrentSchemaVersion"/> (it is then never decrypted).
    /// </summary>
    public static JournalEntry Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        JournalEntry? entry = null;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            entry = JsonSerializer.Deserialize<JournalEntry>(gzip, JournalEntry.JsonOptions)
                ?? throw new JsonException("The entry is null.");
            if (entry.SchemaVersion > JournalEntry.CurrentSchemaVersion)
            {
                // Written by a newer server: its members may mean something this one can't honour (story 2.11).
                throw new OneNoteException(
                    ErrorCode.NotSupported,
                    string.Create(CultureInfo.InvariantCulture, $"Journal entry {entry.EntryId:D} has schemaVersion {entry.SchemaVersion}, newer than this server reads ({JournalEntry.CurrentSchemaVersion})."),
                    userAction: "Use a newer version of the OneNote MCP server to read this entry.");
            }

            return entry.Protected is null ? entry : Unprotect(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidDataException or FormatException or NotSupportedException)
        {
            throw Failed(entry?.EntryId, "read", ex);
        }
    }

    /// <summary>
    /// The one later change to an entry (AD-8, AD-10; story 5.5): adds <paramref name="recycleBinId"/> to the page soft
    /// delete's entry at <paramref name="path"/>, which must hold entry <paramref name="entryId"/>, by writing the whole
    /// entry again to <c>.{name}.tmp</c> beside it, flushed, then <see cref="File.Replace(string, string, string?)"/>.
    /// Called once, under the write lock. A protected entry's <see cref="JournalEntry.Protected"/> blob is copied as it is
    /// and never decrypted. It never throws for the file: it returns null when the entry was rewritten, else why not (a
    /// short phrase with no path or content, such as "the entry file is gone"); the entry then stays as first written.
    /// </summary>
    public static string? TryAddRecycleBinId(string path, Guid entryId, string recycleBinId)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(recycleBinId);
        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        var temp = Path.Combine(folder, JournalFiles.TempName(Path.GetFileName(path)));
        try
        {
            if (!File.Exists(path))
            {
                return "the entry file is gone";
            }

            JournalEntry entry;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            {
                entry = JsonSerializer.Deserialize<JournalEntry>(gzip, JournalEntry.JsonOptions) ?? throw new JsonException("The entry is null.");
            }

            if (entry.EntryId != entryId)
            {
                return "the entry file holds another entry";
            }

            // Written back as read: a protected entry keeps its blob, and its title and XML stay absent.
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true))
                {
                    JsonSerializer.Serialize(gzip, entry with { RecycleBinId = recycleBinId }, JournalEntry.JsonOptions);
                }

                file.Flush(flushToDisk: true);
            }

            // A scanner or the indexer can hold a just-written entry for a moment (a full test run hit it, 2026-11-13);
            // the settings' replace already waits that out and recovers ERROR_UNABLE_TO_MOVE_REPLACEMENT (bug 2.15).
            SettingsWriter.ReplaceWithRetry(temp, path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            TryDelete(temp);
            return ex is FileNotFoundException or DirectoryNotFoundException ? "the entry file is gone" : $"{ex.GetType().Name} while rewriting the entry";
        }
    }

    /// <summary>The DPAPI entropy: binds a protected blob to its entry, so a blob moved into another entry won't decrypt.</summary>
    private static byte[] Entropy(Guid entryId) =>
        Encoding.UTF8.GetBytes(EntropyPrefix + entryId.ToString("D", CultureInfo.InvariantCulture));

    private static JournalEntry Protect(JournalEntry entry)
    {
        var secret = JsonSerializer.SerializeToUtf8Bytes(new ProtectedContent(entry.Title, entry.Xml), JournalEntry.JsonOptions);
        var blob = ProtectedData.Protect(secret, Entropy(entry.EntryId), DataProtectionScope.CurrentUser);
        return entry with { Title = null, Xml = null, Protected = Convert.ToBase64String(blob) };
    }

    private static JournalEntry Unprotect(JournalEntry entry)
    {
        var secret = ProtectedData.Unprotect(Convert.FromBase64String(entry.Protected!), Entropy(entry.EntryId), DataProtectionScope.CurrentUser);
        var content = JsonSerializer.Deserialize<ProtectedContent>(secret, JournalEntry.JsonOptions)
            ?? throw new JsonException("The protected content is null.");
        return entry with { Title = content.Title, Xml = content.Xml, Protected = null };
    }

    private static OneNoteException Failed(Guid? entryId, string what, Exception ex) =>
        new(
            ErrorCode.JournalFailed,
            entryId is { } id
                ? $"Journal entry {id:D} couldn't be {what} ({ex.GetType().Name})."
                : $"A journal entry couldn't be {what} ({ex.GetType().Name}).",
            innerException: ex);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The pruner removes temporary files older than a day.
        }
    }

    /// <summary>What a protected entry's DPAPI blob holds.</summary>
    private sealed record ProtectedContent(string? Title, string? Xml);
}
