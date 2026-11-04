// Read-only probe: page lastModifiedTime from GetPageContent vs GetHierarchy. Prints timestamps only.
using System.Diagnostics;
using System.Xml.Linq;
using OneNoteMcp.Com.Interop;
if (Process.GetProcessesByName("ONENOTE").Length == 0) { Console.WriteLine("OneNote not running"); return; }
var t = new Thread(() =>
{
    var app = (IApplication)Activator.CreateInstance(Type.GetTypeFromCLSID(OneNoteClsid.Application)!)!;
    XNamespace ns = "http://schemas.microsoft.com/office/onenote/2013/onenote";
    app.GetHierarchy("", HierarchyScope.hsPages, out var h, XMLSchema.xs2013);
    var pages = XDocument.Parse(h).Descendants(ns + "Page").Where(p => (string?)p.Attribute("isInRecycleBin") != "true").ToList();
    int step = Math.Max(1, pages.Count / 12), same = 0, readTime = 0, other = 0;
    foreach (var p in pages.Where((_, i) => i % step == 0).Take(12))
    {
        var id = (string)p.Attribute("ID")!;
        var hier = DateTime.Parse((string)p.Attribute("lastModifiedTime")!).ToUniversalTime();
        var before = DateTime.UtcNow;
        app.GetPageContent(id, out var x, PageInfo.piBasic, XMLSchema.xs2013);
        var pg = DateTime.Parse((string)XDocument.Parse(x).Root!.Attribute("lastModifiedTime")!).ToUniversalTime();
        var kind = Math.Abs((pg - hier).TotalSeconds) < 1 ? "same" : Math.Abs((pg - before).TotalSeconds) < 5 ? "READ-TIME" : "other";
        if (kind == "same") same++; else if (kind == "READ-TIME") readTime++; else other++;
        Console.WriteLine($"hier={hier:u} page={pg:u} -> {kind}");
    }
    Console.WriteLine($"same={same} readTime={readTime} other={other} of {pages.Count} pages");
    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(app);
});
t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
