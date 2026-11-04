using System.Xml.Linq;
using OneNoteMcp.Com;
using OneNoteMcp.Com.Interop;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Integration;

/// <summary>
/// Opt-in proof of the hand-declared vtable (AD-1) against a real, already running OneNote desktop.
/// Skipped unless <c>ONENOTE_MCP_INTEGRATION=1</c>. Read-only calls only: never NavigateTo, NavigateToUrl,
/// QuickFiling or any write. Each slot exercised is spread across the vtable, ending at the last one (35).
/// </summary>
public sealed class VtableSmokeTests(VtableSmokeTests.OneNoteFixture fixture) : IClassFixture<VtableSmokeTests.OneNoteFixture>
{
    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    [Fact]
    public async Task Slot07_GetHierarchy_Notebooks()
    {
        var xml = await fixture.Run(app =>
        {
            app.GetHierarchy(string.Empty, HierarchyScope.hsNotebooks, out var x, XMLSchema.xs2013);
            return x;
        });

        var root = XDocument.Parse(xml).Root!;
        Assert.Equal(One + "Notebooks", root.Name);
        Assert.NotEmpty(root.Elements(One + "Notebook"));
    }

    [Fact]
    public async Task Slot07_GetHierarchy_PagesUnderNotebook()
    {
        var target = await fixture.Target();
        var xml = await fixture.Run(app =>
        {
            app.GetHierarchy(target.NotebookId, HierarchyScope.hsPages, out var x, XMLSchema.xs2013);
            return x;
        });

        var root = XDocument.Parse(xml).Root!;
        Assert.Equal(One + "Notebook", root.Name);
        Assert.Equal(target.NotebookId, (string?)root.Attribute("ID"));
        Assert.NotEmpty(root.Descendants(One + "Page"));
    }

    [Fact]
    public async Task Slot13_GetHierarchyParent_OfPageIsItsSection()
    {
        var target = await fixture.Target();
        var parent = await fixture.Run(app =>
        {
            app.GetHierarchyParent(target.PageId, out var p);
            return p;
        });

        Assert.Equal(target.SectionId, parent);
    }

    [Fact]
    public async Task Slot14_GetPageContent_Basic()
    {
        var target = await fixture.Target();
        var xml = await fixture.Run(app =>
        {
            app.GetPageContent(target.PageId, out var x, PageInfo.piBasic, XMLSchema.xs2013);
            return x;
        });

        var root = XDocument.Parse(xml).Root!;
        Assert.Equal(One + "Page", root.Name);
        Assert.Equal(target.PageId, (string?)root.Attribute("ID"));
    }

    [Fact]
    public async Task Slot22_GetHyperlinkToObject()
    {
        var target = await fixture.Target();
        var link = await fixture.Run(app =>
        {
            app.GetHyperlinkToObject(target.PageId, string.Empty, out var l);
            return l;
        });

        Assert.StartsWith("onenote:", link, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)] // slBackUpFolder
    [InlineData(1)] // slUnfiledNotesSection
    [InlineData(2)] // slDefaultNotebookFolder
    public async Task Slot25_GetSpecialLocation(int location)
    {
        var path = await fixture.Run(app =>
        {
            app.GetSpecialLocation((SpecialLocation)location, out var p);
            return p;
        });

        Assert.False(string.IsNullOrWhiteSpace(path));
    }

    [Fact]
    public async Task Slot30_Windows_Count()
    {
        var count = await fixture.Run(app =>
        {
            var windows = (IWindows)app.Windows;
            return windows.Count;
        });

        // OneNote is running with its UI, so it has at least one window.
        Assert.True(count >= 1, $"Windows.Count = {count}");
    }

    [Fact]
    public async Task Slot35_GetWebHyperlinkToObject()
    {
        var target = await fixture.Target();
        Assert.SkipUnless(target.IsCloud, "The chosen notebook is local; a web link needs a cloud notebook.");
        var link = await fixture.Run(app =>
        {
            app.GetWebHyperlinkToObject(target.PageId, string.Empty, out var l);
            return l;
        });

        Assert.StartsWith("https://", link, StringComparison.OrdinalIgnoreCase);
    }

    public sealed record PageTarget(string NotebookId, string SectionId, string PageId, bool IsCloud);

    /// <summary>Owns one STA thread and one COM reference for the class; creates nothing unless the flag is set.</summary>
    public sealed class OneNoteFixture : IDisposable
    {
        private readonly Lazy<StaThread> _sta = new(() => new StaThread("OneNote-STA-Integration"));
        private IApplication? _app;
        private PageTarget? _target;

        public static bool Enabled => Environment.GetEnvironmentVariable("ONENOTE_MCP_INTEGRATION") == "1";

        internal Task<T> Run<T>(Func<IApplication, T> call)
        {
            Assert.SkipUnless(Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
            return _sta.Value.InvokeAsync(() =>
            {
                if (_app is null)
                {
                    // Never activate OneNote: the suite needs the user's running instance.
                    Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
                    var type = Type.GetTypeFromCLSID(OneNoteClsid.Application, throwOnError: true)!;
                    _app = (IApplication)Activator.CreateInstance(type)!;
                }

                return call(_app);
            });
        }

        /// <summary>The first non-recycle-bin page, preferring a cloud notebook so the web-link slot can run.</summary>
        public async Task<PageTarget> Target()
        {
            if (_target is not null)
            {
                return _target;
            }

            var xml = await Run(app =>
            {
                app.GetHierarchy(string.Empty, HierarchyScope.hsPages, out var x, XMLSchema.xs2013);
                return x;
            });

            var candidates =
                from notebook in XDocument.Parse(xml).Root!.Elements(One + "Notebook")
                let isCloud = ((string?)notebook.Attribute("path") ?? string.Empty).StartsWith("http", StringComparison.OrdinalIgnoreCase)
                from section in notebook.Descendants(One + "Section")
                where (string?)section.Attribute("isInRecycleBin") != "true"
                    && (string?)section.Attribute("locked") != "true"
                from page in section.Elements(One + "Page")
                where (string?)page.Attribute("isInRecycleBin") != "true"
                orderby isCloud descending
                select new PageTarget(
                    (string)notebook.Attribute("ID")!,
                    (string)section.Attribute("ID")!,
                    (string)page.Attribute("ID")!,
                    isCloud);

            _target = candidates.FirstOrDefault()
                ?? throw new InvalidOperationException("No readable page found in any open notebook.");
            return _target;
        }

        public void Dispose()
        {
            if (!_sta.IsValueCreated)
            {
                return;
            }

            _sta.Value.InvokeAsync(() =>
            {
                if (_app is not null)
                {
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(_app);
                    _app = null;
                }

                return true;
            }).Wait(TimeSpan.FromSeconds(5));
            _sta.Value.Dispose();
        }
    }
}
