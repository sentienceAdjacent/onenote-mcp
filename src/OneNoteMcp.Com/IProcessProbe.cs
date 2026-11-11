using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OneNoteMcp.Com;

/// <summary>
/// Tells the gateway whether OneNote desktop is running, so it never activates OneNote silently, and whether it
/// runs at this process's integrity level, so COM is never called across a mismatch (AD-3).
/// </summary>
public interface IProcessProbe
{
    /// <summary>
    /// True when an <c>ONENOTE.EXE</c> runs in this session that Windows Error Reporting isn't handling. A crashed one
    /// stays listed while WER dumps it; activating COM against it would start a hidden <c>-Embedding</c> copy (AD-3).
    /// </summary>
    bool IsOneNoteRunning();

    /// <summary>True when an <c>ONENOTE.EXE</c> in this session counts as crashing (see <see cref="IsOneNoteRunning"/>).</summary>
    bool IsOneNoteCrashing() => false;

    /// <summary>Compares this process's integrity level with every <c>ONENOTE.EXE</c> in this session. Never touches COM.</summary>
    IntegrityReport CheckIntegrity();
}

/// <summary>One <c>ONENOTE.EXE</c>'s integrity level: the mandatory-label RID, or null when its token can't be read.</summary>
public sealed record OneNoteIntegrity(int ProcessId, int? Level);

/// <summary>
/// The result of an integrity check. A level that can't be read counts as a mismatch: the only known cause is OneNote
/// running higher, and calling COM then hangs for 30–57 s (spike row "Probe elevated"). No OneNote at all is a match:
/// there is nothing to cross.
/// </summary>
public sealed record IntegrityReport(int? ServerLevel, IReadOnlyList<OneNoteIntegrity> OneNote)
{
    public bool Matches => ServerLevel is { } server && OneNote.All(o => o.Level == server);

    /// <summary><c>server=Medium onenote=[1234:High]</c>: levels and process IDs only.</summary>
    public string Describe() =>
        $"server={LevelName(ServerLevel)} onenote=[{string.Join(",", OneNote.Select(o => $"{o.ProcessId.ToString(CultureInfo.InvariantCulture)}:{LevelName(o.Level)}"))}]";

    /// <summary>The well-known name of a mandatory-label RID, else its hex.</summary>
    public static string LevelName(int? rid) => rid switch
    {
        null => "unreadable",
        0x0000 => "Untrusted",
        0x1000 => "Low",
        0x2000 => "Medium",
        0x2100 => "MediumPlus",
        0x3000 => "High",
        0x4000 => "System",
        { } other => "0x" + other.ToString("X4", CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// Looks for an <c>ONENOTE.EXE</c> process in the current Windows session only. Another user's OneNote
/// (in another session) doesn't count, because COM activation from here would start a new one in ours.
/// </summary>
public sealed class ProcessProbe : IProcessProbe
{
    private static readonly string[] WerProcessNames = ["WerFault", "WerFaultSecure"];

    public bool IsOneNoteRunning()
    {
        var oneNote = SessionProcessIds("ONENOTE");
        return oneNote.Count > 0 && Running(oneNote, WerCommandLines()).Count > 0;
    }

    public bool IsOneNoteCrashing()
    {
        var oneNote = SessionProcessIds("ONENOTE");
        return oneNote.Count > 0 && Running(oneNote, WerCommandLines()).Count < oneNote.Count;
    }

    /// <summary>
    /// The <c>ONENOTE.EXE</c> IDs that count as running: those no WER process targets. A WER process whose command line
    /// couldn't be read (null) might target any of them, so then none counts (fail safe: never start a hidden copy).
    /// </summary>
    internal static IReadOnlyList<int> Running(IReadOnlyList<int> oneNote, IReadOnlyList<string?> werCommandLines)
    {
        ArgumentNullException.ThrowIfNull(oneNote);
        ArgumentNullException.ThrowIfNull(werCommandLines);
        if (werCommandLines.Any(line => line is null))
        {
            return [];
        }

        var crashing = werCommandLines.Select(WerFaultTarget).OfType<int>().ToHashSet();
        return [.. oneNote.Where(pid => !crashing.Contains(pid))];
    }

    /// <summary>
    /// The process ID a <c>WerFault.exe</c> command line reports on: the value after <c>-p</c> or <c>/p</c>, any case
    /// (<c>WerFault.exe -u -p 1234 -s 568</c>). Null when there is none.
    /// </summary>
    internal static int? WerFaultTarget(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var tokens = commandLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if ((tokens[i].Equals("-p", StringComparison.OrdinalIgnoreCase) || tokens[i].Equals("/p", StringComparison.OrdinalIgnoreCase))
                && int.TryParse(tokens[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                return pid;
            }
        }

        return null;
    }

    public IntegrityReport CheckIntegrity()
    {
        int? server;
        using (var current = Process.GetCurrentProcess())
        {
            server = IntegrityLevel(current.Id);
        }

        return BuildReport(server, SessionProcessIds("ONENOTE"), IntegrityLevel, ProcessExists);
    }

    /// <summary>
    /// Builds the report. An unreadable level counts only while its process still exists: an <c>ONENOTE.EXE</c> that
    /// exited between listing and opening is skipped, so a closing OneNote reads as not running, not as a mismatch.
    /// </summary>
    internal static IntegrityReport BuildReport(int? server, IEnumerable<int> processIds, Func<int, int?> level, Func<int, bool> exists)
    {
        var oneNote = new List<OneNoteIntegrity>();
        foreach (var pid in processIds)
        {
            var rid = level(pid);
            if (rid is null && !exists(pid))
            {
                continue;
            }

            oneNote.Add(new OneNoteIntegrity(pid, rid));
        }

        return new IntegrityReport(server, oneNote.AsReadOnly());
    }

    /// <summary>False only when the process is gone. Access denied (an elevated OneNote) counts as existing.</summary>
    private static bool ProcessExists(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// The command line of every WER process (<c>WerFault.exe</c>, <c>WerFaultSecure.exe</c>) in this session; null for
    /// one that couldn't be read. Empty when none runs.
    /// </summary>
    private static List<string?> WerCommandLines()
    {
        var lines = new List<string?>();
        foreach (var name in WerProcessNames)
        {
            foreach (var pid in SessionProcessIds(name))
            {
                if (CommandLine(pid) is { } line)
                {
                    lines.Add(line);
                }
                else if (ProcessExists(pid))
                {
                    // Unreadable while it still runs: it might be handling OneNote.
                    lines.Add(null);
                }
            }
        }

        return lines;
    }

    /// <summary>The process's command line (<c>ProcessCommandLineInformation</c>), or null when it can't be read.</summary>
    private static string? CommandLine(int processId)
    {
        using var process = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid)
        {
            return null;
        }

        NativeMethods.NtQueryInformationProcess(process, NativeMethods.ProcessCommandLineInformation, IntPtr.Zero, 0, out var size);
        if (size <= 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NativeMethods.NtQueryInformationProcess(process, NativeMethods.ProcessCommandLineInformation, buffer, size, out _) != 0)
            {
                return null;
            }

            // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer }, the text following it in the buffer.
            var length = (ushort)Marshal.ReadInt16(buffer);
            var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            return text == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(text, length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static List<int> SessionProcessIds(string processName)
    {
        int sessionId;
        using (var current = Process.GetCurrentProcess())
        {
            sessionId = current.SessionId;
        }

        var ids = new List<int>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId == sessionId)
                    {
                        ids.Add(process.Id);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited while we looked.
                }
            }
        }

        return ids;
    }

    /// <summary>The token's mandatory-label RID, or null when the process or its token can't be opened.</summary>
    private static int? IntegrityLevel(int processId)
    {
        using var process = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid)
        {
            return null;
        }

        if (!NativeMethods.OpenProcessToken(process, NativeMethods.TokenQuery, out var token))
        {
            return null;
        }

        using (token)
        {
            NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, IntPtr.Zero, 0, out var size);
            if (size == 0)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, buffer, size, out _))
                {
                    return null;
                }

                // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label }: the first field is the label SID pointer.
                var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
                var value = sid.Value; // S-1-16-<rid>
                var rid = value[(value.LastIndexOf('-') + 1)..];
                return int.TryParse(rid, NumberStyles.None, CultureInfo.InvariantCulture, out var level) ? level : null;
            }
            catch (Exception ex) when (ex is ArgumentException or Win32Exception)
            {
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static class NativeMethods
    {
        internal const uint ProcessQueryLimitedInformation = 0x1000;
        internal const uint TokenQuery = 0x0008;
        internal const int TokenIntegrityLevel = 25;
        internal const int ProcessCommandLineInformation = 60;

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(SafeProcessHandle processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(SafeAccessTokenHandle tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

        /// <summary>Returns an NTSTATUS: 0 on success; <c>STATUS_INFO_LENGTH_MISMATCH</c> with the size needed for a short buffer.</summary>
        [DllImport("ntdll.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int NtQueryInformationProcess(SafeProcessHandle processHandle, int processInformationClass, IntPtr processInformation, int processInformationLength, out int returnLength);
    }
}
