using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using OneNoteMcp.Com;
using OneNoteMcp.Xml;
using Interop = OneNoteMcp.Com.Interop;

namespace StressHarness;

/// <summary>
/// Backlog 10 section-download probe. Does <c>SyncHierarchy(sectionId)</c> make OneNote download a section whose
/// <c>areAllPagesAvailable</c> is false (story 3.7's refusal)? Watches the section for a 60 s baseline, calls
/// <c>SyncHierarchy</c> once, then polls every 2 s until the attribute clears or <c>--max-seconds</c>. Each line shows the
/// attribute, the page count, the foreground process (ONENOTE / other / none) and ONENOTE's visible top-level window count.
/// Calls only GetHierarchy and SyncHierarchy (an AppState call, AD-7/AD-11): never NavigateTo, a write, Publish, or
/// closing or starting OneNote. Prints codes, counts, times and attribute values only; the section ID is never echoed.
/// The STA body never throws: every COM call is caught and printed as an HRESULT.
/// </summary>
internal static class ProbeSectionSync
{
    private const int PollMs = 2000;
    private const int BaselineSeconds = 60;

    public static int Run(string sectionId, int maxSeconds)
    {
        var probe = new ProcessProbe();
        if (!probe.IsOneNoteRunning())
        {
            throw new RefusalException(Exit.Precondition, "OneNote is not running; start OneNote desktop first");
        }

        if (!probe.CheckIntegrity().Matches)
        {
            throw new RefusalException(Exit.Precondition, "integrity level differs from OneNote's");
        }

        var exit = Exit.Crashed;
        var thread = new Thread(() => exit = Body(sectionId, maxSeconds)) { IsBackground = false };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exit;
    }

    private static int Body(string sectionId, int maxSeconds)
    {
        Interop.IApplication? app = null;
        try
        {
            var type = Type.GetTypeFromCLSID(Interop.OneNoteClsid.Application, throwOnError: true)!;
            app = (Interop.IApplication)Activator.CreateInstance(type)!;

            // The first read decides whether the probe may go on. A COM failure (an ID not found among them) prints the
            // HRESULT only; a reply that isn't a section, or one the probe mustn't touch, is refused before SyncHierarchy.
            Interop.IApplication readApp = app;
            string tree;
            try
            {
                readApp.GetHierarchy(sectionId, Interop.HierarchyScope.hsPages, out tree, Interop.XMLSchema.xs2013);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"refused: the section could not be read: {Hex(ex.HResult)}");
                return Exit.Precondition;
            }

            var root = HierarchyNode.Root(tree);
            if (root.Kind != HierarchyKind.Section)
            {
                Console.WriteLine("refused: the ID is not a section");
                return Exit.Precondition;
            }

            if (root.Locked || root.EncryptedMarker || root.InRecycleBin)
            {
                Console.WriteLine($"refused: the section is locked {Flag(root.Locked)}, password-protected {Flag(root.EncryptedMarker)}, in the recycle bin {Flag(root.InRecycleBin)}");
                return Exit.Precondition;
            }

            var first = State.From(tree);
            if (!first.NotDownloaded)
            {
                Console.WriteLine($"already available: areAllPagesAvailable {first.Attribute}, pages {first.Pages}; no SyncHierarchy call");
                return Exit.Precondition;
            }

            Console.WriteLine($"before: {Line(first)}");

            // Baseline: does the section download by itself, without the call?
            var baseline = Stopwatch.StartNew();
            while (baseline.ElapsedMilliseconds < BaselineSeconds * 1000L)
            {
                Thread.Sleep(PollMs);
                var s = Read(readApp, sectionId);
                Console.WriteLine($"  baseline at {Seconds(baseline)} s: {Line(s)}");
                if (s.Cleared)
                {
                    Console.WriteLine($"cleared before the call, at {Seconds(baseline)} s of the baseline; no SyncHierarchy call");
                    Console.WriteLine($"end: {Line(s)}");
                    return Exit.Ok;
                }
            }

            var (fgBefore, winBefore) = Windows.Snapshot();
            Console.WriteLine($"calling SyncHierarchy; foreground {fgBefore}; ONENOTE visible windows {winBefore}");
            var call = Stopwatch.StartNew();
            int syncCode;
            try
            {
                readApp.SyncHierarchy(sectionId);
                syncCode = 0;
            }
            catch (Exception ex)
            {
                syncCode = ex.HResult;
            }

            call.Stop();
            var (foreground, windows) = Windows.Snapshot();
            Console.WriteLine($"SyncHierarchy(section): {Hex(syncCode)} in {call.ElapsedMilliseconds} ms; foreground {foreground}; ONENOTE visible windows {windows}");

            var after = Stopwatch.StartNew();
            var last = first;
            while (after.ElapsedMilliseconds < maxSeconds * 1000L)
            {
                Thread.Sleep(PollMs);
                last = Read(readApp, sectionId);
                Console.WriteLine($"  after call at {Seconds(after)} s: {Line(last)}");
                if (last.Cleared)
                {
                    Console.WriteLine($"downloaded: the attribute cleared {Seconds(after)} s after the call returned");
                    Console.WriteLine($"end: {Line(last)}");
                    return Exit.Ok;
                }
            }

            Console.WriteLine($"not downloaded in {Seconds(after)} s after the call");
            Console.WriteLine($"end: {Line(last)}");
            return Exit.Ok;
        }
        catch (Exception ex)
        {
            // Type name and HRESULT only: an exception message could carry a name or a path.
            Console.WriteLine($"crashed: {ex.GetType().Name} ({Hex(ex.HResult)})");
            return Exit.Crashed;
        }
        finally
        {
            if (app is not null)
            {
                try
                {
                    Marshal.FinalReleaseComObject(app);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    /// <summary>One poll. A failed read (e.g. 0x80042030 while a dialog is open) is a state with its HRESULT, not fatal.</summary>
    private static State Read(Interop.IApplication app, string sectionId)
    {
        try
        {
            app.GetHierarchy(sectionId, Interop.HierarchyScope.hsPages, out var tree, Interop.XMLSchema.xs2013);
            return State.From(tree);
        }
        catch (Exception ex)
        {
            return new State(ex.HResult, "?", -1);
        }
    }

    private static string Line(State s)
    {
        var (foreground, windows) = Windows.Snapshot();
        var read = s.Code == 0 ? $"areAllPagesAvailable {s.Attribute}, pages {s.Pages}" : $"read failed {Hex(s.Code)}";
        return $"{read}; foreground {foreground}; ONENOTE visible windows {windows}";
    }

    private static string Seconds(Stopwatch sw) => (sw.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Flag(bool value) => value ? "yes" : "no";

    private static string Hex(int hr) => "0x" + hr.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>The section root's <c>areAllPagesAvailable</c> (raw value, or "absent") and its page count.</summary>
    private sealed record State(int Code, string Attribute, int Pages)
    {
        public bool NotDownloaded => Code == 0 && Attribute is "false" or "0";

        public bool Cleared => Code == 0 && !NotDownloaded;

        public static State From(string tree)
        {
            var root = XmlFacts.Parse(tree).Root!;
            var raw = (string?)root.Attribute("areAllPagesAvailable");

            // Only the attribute's own boolean spellings are printed; anything else is reported as "other".
            var attribute = raw switch
            {
                null => "absent",
                "true" or "false" or "1" or "0" => raw,
                _ => "other",
            };
            return new State(0, attribute, root.Descendants(XmlFacts.One + "Page").Count());
        }
    }

    /// <summary>
    /// The foreground process (ONENOTE, other, or none) and the number of visible top-level windows ONENOTE.EXE owns in
    /// this session. ProcessProbe's window helpers are internal to OneNoteMcp.Com, so the probe declares its own.
    /// </summary>
    private static class Windows
    {
        public static (string Foreground, int OneNoteWindows) Snapshot()
        {
            var oneNote = OneNoteProcessIds();
            var foreground = "none";
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd != IntPtr.Zero)
            {
                _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                foreground = oneNote.Contains((int)pid) ? "ONENOTE" : "other";
            }

            var count = 0;
            NativeMethods.EnumWindowsProc counter = (w, param) =>
            {
                if (NativeMethods.IsWindowVisible(w))
                {
                    _ = NativeMethods.GetWindowThreadProcessId(w, out var owner);
                    if (oneNote.Contains((int)owner))
                    {
                        count++;
                    }
                }

                return true;
            };
            NativeMethods.EnumWindows(counter, IntPtr.Zero);
            GC.KeepAlive(counter);
            return (foreground, count);
        }

        private static HashSet<int> OneNoteProcessIds()
        {
            var session = Process.GetCurrentProcess().SessionId;
            var ids = new HashSet<int>();
            foreach (var p in Process.GetProcessesByName("ONENOTE"))
            {
                using (p)
                {
                    try
                    {
                        if (p.SessionId == session)
                        {
                            ids.Add(p.Id);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // The process exited between listing and reading.
                    }
                }
            }

            return ids;
        }
    }

    private static class NativeMethods
    {
        [return: MarshalAs(UnmanagedType.Bool)]
        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hwnd);
    }
}
