using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using OneNoteMcp.Com;
using Interop = OneNoteMcp.Com.Interop;

namespace StressHarness;

/// <summary>
/// Spike 1.15 image-download probe (Phill approved SyncHierarchy on one page of notebook A, 2026-11-06). Does
/// <c>SyncHierarchy(pageId)</c> make OneNote download an image whose binary isn't on this PC? ComGateway has no
/// SyncHierarchy, so this uses the spike's own compiled-in copy of the hand-declared interop on its own STA thread.
/// Calls only GetHierarchy, GetPageContent, GetBinaryPageContent and SyncHierarchy: never NavigateTo, never a write,
/// never closes or starts OneNote. Prints IDs, codes, ms and lengths only.
/// </summary>
internal static class ProbeSync
{
    private const int BinaryObjectDoesNotExist = unchecked((int)0x8004200F);

    public static int Run(string notebookId, string? pageId)
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
        var thread = new Thread(() => exit = Body(notebookId, pageId)) { IsBackground = false };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exit;
    }

    private static int Body(string notebookId, string? wantedPage)
    {
        var type = Type.GetTypeFromCLSID(Interop.OneNoteClsid.Application, throwOnError: true)!;
        var app = (Interop.IApplication)Activator.CreateInstance(type)!;
        try
        {
            app.GetHierarchy(notebookId, Interop.HierarchyScope.hsPages, out var tree, Interop.XMLSchema.xs2013);
            var model = XmlFacts.Model(notebookId, tree);
            var candidates = model.Pages
                .Where(p => !p.InRecycleBin && (wantedPage is null || p.Id == wantedPage))
                .Where(p => !model.Sections.First(s => s.Id == p.SectionId).Locked)
                .ToList();

            foreach (var page in candidates)
            {
                app.GetPageContent(page.Id, out var basic, Interop.PageInfo.piBasic, Interop.XMLSchema.xs2013);
                var images = XmlFacts.Parse(basic).Descendants(XmlFacts.One + "Image")
                    .Select(i => (string?)i.Element(XmlFacts.One + "CallbackID")?.Attribute("callbackID"))
                    .Where(c => !string.IsNullOrEmpty(c))
                    .ToList();
                foreach (var cb in images)
                {
                    var (code, len) = Binary(app, page.Id, cb!);
                    if (code != BinaryObjectDoesNotExist)
                    {
                        continue;
                    }

                    Console.WriteLine($"page {page.Id} section {page.SectionId}: {images.Count} image CallbackID(s); chosen one before sync: {Hex(code)} len {len}");
                    var ok = SyncAndPoll(app, page.Id, page.Id, cb!, "page");
                    if (!ok)
                    {
                        ok = SyncAndPoll(app, page.SectionId, page.Id, cb!, "section");
                    }

                    // The page's other image CallbackIDs, after the syncs.
                    var after = images.Select(c => Binary(app, page.Id, c!)).ToList();
                    Console.WriteLine($"all {after.Count} image CallbackID(s) on the page after: returned {after.Count(a => a.Code == 0)}, 0x8004200F {after.Count(a => a.Code == BinaryObjectDoesNotExist)}, other {after.Count(a => a.Code != 0 && a.Code != BinaryObjectDoesNotExist)}");
                    return Exit.Ok;
                }
            }

            Console.WriteLine("no page with an unavailable image was found");
            return Exit.Precondition;
        }
        finally
        {
            Marshal.FinalReleaseComObject(app);
        }
    }

    private static bool SyncAndPoll(Interop.IApplication app, string syncId, string pageId, string callbackId, string what)
    {
        var sw = Stopwatch.StartNew();
        int syncCode;
        try
        {
            app.SyncHierarchy(syncId);
            syncCode = 0;
        }
        catch (COMException ex)
        {
            syncCode = ex.HResult;
        }

        sw.Stop();
        Console.WriteLine($"SyncHierarchy({what} {syncId}): {Hex(syncCode)} in {sw.ElapsedMilliseconds} ms");
        var poll = Stopwatch.StartNew();
        for (var i = 1; i <= 30; i++)
        {
            Thread.Sleep(2000);
            var (code, len) = Binary(app, pageId, callbackId);
            Console.WriteLine($"  poll {i} at {poll.ElapsedMilliseconds / 1000.0:0.0} s: {Hex(code)} len {len}");
            if (code == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static (int Code, int Len) Binary(Interop.IApplication app, string pageId, string callbackId)
    {
        try
        {
            app.GetBinaryPageContent(pageId, callbackId, out var b64);
            return (0, b64?.Length ?? 0);
        }
        catch (COMException ex)
        {
            return (ex.HResult, 0);
        }
    }

    private static string Hex(int hr) => "0x" + hr.ToString("X8", CultureInfo.InvariantCulture);
}
