using System.Globalization;
using Serilog;
using Serilog.Core;
using Serilog.Sinks.File;

namespace OneNoteMcp.Server.Logging;

/// <summary>Where and how big the rolling file log is (AD-13). The size cap is injectable for tests.</summary>
public sealed record FileLogOptions(string Directory)
{
    public const long DefaultFileSizeLimitBytes = 10L * 1024 * 1024;
    public const int DefaultRetainedFileCount = 5;

    public long FileSizeLimitBytes { get; init; } = DefaultFileSizeLimitBytes;

    public int RetainedFileCount { get; init; } = DefaultRetainedFileCount;

    public DateTime StartedAt { get; init; } = DateTime.Now;

    public int ProcessId { get; init; } = Environment.ProcessId;
}

/// <summary>What <see cref="FileLog.Start"/> did. When <see cref="Logger"/> is null the server runs on stderr logging only.</summary>
public sealed record FileLogStart(Logger? Logger, string Directory, string? FilePath, string? FailureReason);

/// <summary>
/// The rolling file log beside stderr (AD-13): one file per process,
/// <c>onenote-mcp-{yyyyMMddTHHmmss}-{pid}.log</c>, 10 MB per file, 5 files kept. It carries the same records as
/// stderr, so it inherits their rule: IDs, HRESULTs and timings only, never XML or page content.
/// </summary>
public static class FileLog
{
    /// <summary>Test-only override of the log folder (Conventions: env vars only for test overrides of paths).</summary>
    public const string DirectoryOverrideVariable = "ONENOTE_MCP_LOG_DIR";

    public const string FilePattern = "onenote-mcp-*.log";

    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary><see cref="DirectoryOverrideVariable"/> when set, else <c>%LOCALAPPDATA%\OneNoteMcp\logs</c>.</summary>
    public static string ResolveDirectory(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var overridden = getEnvironmentVariable(DirectoryOverrideVariable);
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneNoteMcp", "logs")
            : Path.GetFullPath(overridden);
    }

    public static string FileName(DateTime startedAt, int processId) =>
        string.Create(CultureInfo.InvariantCulture, $"onenote-mcp-{startedAt:yyyyMMddTHHmmss}-{processId}.log");

    /// <summary>
    /// Creates the folder, prunes old files from every process, and opens this process's file log. Never throws:
    /// a folder that can't be used leaves the server on stderr only, with the reason returned for the startup log.
    /// </summary>
    public static FileLogStart Start(FileLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            Directory.CreateDirectory(options.Directory);

            // Leave room for this process's own file, so the folder holds at most RetainedFileCount after start.
            Prune(options.Directory, options.RetainedFileCount - 1);

            var filePath = Path.Combine(options.Directory, FileName(options.StartedAt, options.ProcessId));
            var logger = new LoggerConfiguration()
                // Information, like stderr. AddSerilog lets every level through to this provider, and the MCP SDK logs
                // whole JSON-RPC messages (tool results with XML) at Debug and Trace, which AD-13 keeps out of the log.
                .MinimumLevel.Information()
                .WriteTo.File(
                    filePath,
                    outputTemplate: OutputTemplate,
                    formatProvider: CultureInfo.InvariantCulture,
                    fileSizeLimitBytes: options.FileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: options.RetainedFileCount,
                    shared: false,
                    hooks: new PruneOnOpen(options.Directory, options.RetainedFileCount))
                .CreateLogger();
            return new FileLogStart(logger, options.Directory, filePath, FailureReason: null);
        }
#pragma warning disable CA1031 // Logging failures never stop the server (AD-13): stderr logging still works.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new FileLogStart(Logger: null, options.Directory, FilePath: null, FailureReason: ex.GetType().Name);
        }
    }

    /// <summary>
    /// Prunes every process's files each time Serilog opens a file, at start and on every roll. Serilog's own
    /// retention counts only this process's files, so without this a rolling server could leave more than
    /// RetainedFileCount in the folder. The just-opened file is held open, so the prune cannot delete it.
    /// </summary>
    private sealed class PruneOnOpen(string directory, int keep) : FileLifecycleHooks
    {
        public override Stream OnFileOpened(string path, Stream underlyingStream, System.Text.Encoding encoding)
        {
            Prune(directory, keep); // never throws
            return underlyingStream;
        }
    }

    /// <summary>
    /// Keeps the newest <paramref name="keep"/> <c>onenote-mcp-*.log</c> files in the folder and deletes the rest.
    /// Serilog's own retention counts only this process's files; this covers every process. Files locked by another
    /// process, or that vanish meanwhile, are skipped. Returns how many files were deleted.
    /// </summary>
    public static int Prune(string directory, int keep)
    {
        ArgumentNullException.ThrowIfNull(directory);

        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(directory).GetFiles(FilePattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        var deleted = 0;
        foreach (var file in files.OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).Skip(Math.Max(keep, 0)))
        {
            try
            {
                file.Delete();
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked by a running server, or read-only: leave it for a later prune.
            }
        }

        return deleted;
    }
}
