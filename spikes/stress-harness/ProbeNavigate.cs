using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using OneNoteMcp.Com;
using Interop = OneNoteMcp.Com.Interop;

namespace StressHarness;

/// <summary>
/// Story 1.21 image-download probe (Phill approved NavigateTo on one already-read page of notebook A, 2026-11-07).
/// Does <c>NavigateTo(pageId)</c>, which shows the page in OneNote and takes focus, make OneNote download an image
/// whose binary isn't on this PC? Uses the spike's compiled-in copy of the hand-declared interop on its own STA thread.
/// Calls only GetHierarchy, GetPageContent, GetBinaryPageContent and one NavigateTo: never a write, never closes or
/// starts OneNote. Picks only a page that is not unread and not in a locked, encrypted or recycle-bin section, so
/// showing it changes nothing. Prints IDs, codes, ms and lengths only.
/// </summary>
internal static class ProbeNavigate
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
            var unread = XmlFacts.Parse(tree).Descendants(XmlFacts.One + "Page")
                .Where(p => string.Equals((string?)p.Attribute("isUnread"), "true", StringComparison.OrdinalIgnoreCase))
                .Select(p => (string?)p.Attribute("ID"))
                .ToHashSet(StringComparer.Ordinal);
            var candidates = model.Pages
                .Where(p => !p.InRecycleBin && !unread.Contains(p.Id) && (wantedPage is null || p.Id == wantedPage))
                .Where(p => model.Sections.First(s => s.Id == p.SectionId) is { Locked: false, Encrypted: false, InRecycleBin: false })
                .ToList();

            foreach (var page in candidates)
            {
                app.GetPageContent(page.Id, out var basic, Interop.PageInfo.piBasic, Interop.XMLSchema.xs2013);
                var images = XmlFacts.Parse(basic).Descendants(XmlFacts.One + "Image")
                    .Select(i => (string?)i.Element(XmlFacts.One + "CallbackID")?.Attribute("callbackID"))
                    .Where(c => !string.IsNullOrEmpty(c))
                    .ToList();
                var before = images.Select(c => Binary(app, page.Id, c!)).ToList();
                var missing = before.Count(b => b.Code == BinaryObjectDoesNotExist);
                if (missing == 0)
                {
                    continue;
                }

                Console.WriteLine($"page {page.Id} section {page.SectionId}: {images.Count} image CallbackID(s); before: returned {before.Count(b => b.Code == 0)}, 0x8004200F {missing}");
                var sw = Stopwatch.StartNew();
                int navCode;
                try
                {
                    app.NavigateTo(page.Id, string.Empty, false);
                    navCode = 0;
                }
                catch (COMException ex)
                {
                    navCode = ex.HResult;
                }

                Console.WriteLine($"NavigateTo(page): {Hex(navCode)} in {sw.ElapsedMilliseconds} ms");
                var poll = Stopwatch.StartNew();
                for (var i = 1; i <= 30; i++)
                {
                    Thread.Sleep(2000);
                    var now = images.Select(c => Binary(app, page.Id, c!)).ToList();
                    var got = now.Count(b => b.Code == 0);
                    Console.WriteLine($"  poll {i} at {poll.ElapsedMilliseconds / 1000.0:0.0} s: returned {got}, 0x8004200F {now.Count(b => b.Code == BinaryObjectDoesNotExist)}, other {now.Count(b => b.Code != 0 && b.Code != BinaryObjectDoesNotExist)}");
                    if (now.All(b => b.Code != BinaryObjectDoesNotExist))
                    {
                        break;
                    }
                }

                return Exit.Ok;
            }

            Console.WriteLine("no read page with an unavailable image was found");
            return Exit.Precondition;
        }
        finally
        {
            Marshal.FinalReleaseComObject(app);
        }
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
