using System.Globalization;
using System.Xml.Linq;
using Xunit;
using static OneNoteMcp.Integration.WriteProbes.ProbeXml;
using CoreScope = OneNoteMcp.Core.HierarchyScope;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Story 2.2, scenario <c>write-probes-search</c> (Q5): <c>FindPages</c> with symbols, <c>FindMeta</c> reliability,
/// <c>encrypted</c> on Ballast Vault in the <c>FindPages</c> and <c>FindMeta</c> XML (no content of the Vault is read),
/// and the guide skill's claims about lists and image <c>alt</c>. Writes only to MCP Test's <c>Probes</c> section,
/// through the guard. Matrix row: Search (Q5).
/// </summary>
public sealed class SearchProbes(ITestOutputHelper output)
{
    public const string ScenarioName = "write-probes-search";

    /// <summary>Each query is the token as written on the probe page; the page holds them all.</summary>
    internal static readonly string[] SymbolTokens =
    [
        "C#", "C++", "a+b", "50%", "foo_bar", "x@y.example", "O'Brien", "semi;colon", "a-b", "(paren)", "[bracket]",
        "100$", "#hashtag", "a&b", "a/b", "dot.ted", "\"quoted phrase\"", "café", "*star", "q?mark",
    ];

    private static readonly TimeSpan IndexWait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task WriteProbesSearch()
    {
        ProbeSession.RequireScenario(ScenarioName);
        using var s = new ProbeSession(ScenarioName, output);
        var notebookId = await s.McpTestIdAsync();
        var sectionId = await s.SectionIdAsync(notebookId, TestNotebook.NotebookName, ProbeSession.ProbesSectionName);
        var stamp = DateTime.UtcNow.ToString("MMddHHmmss", CultureInfo.InvariantCulture);

        await s.StepAsync("search-symbols", () => SymbolsAsync(s, notebookId, sectionId, stamp));
        await s.StepAsync("search-findmeta", () => FindMetaAsync(s, notebookId, sectionId, stamp));
        await s.StepAsync("search-encrypted-vault", () => EncryptedVaultAsync(s, notebookId));
        await s.StepAsync("skill-list-claim", () => ListClaimAsync(s, sectionId, stamp));
        await s.StepAsync("skill-image-alt-claim", () => ImageAltClaimAsync(s, sectionId, stamp));

        s.Finish();
    }

    // --- FindPages with symbols -----------------------------------------------------------------------------------

    private static async Task SymbolsAsync(ProbeSession s, string notebookId, string sectionId, string stamp)
    {
        var anchor = "zqsym" + stamp;
        var pageId = await s.CreatePageAsync(sectionId, $"probe search symbols {stamp}");
        await s.MustUpdateAsync(pageId, Outline(null, [.. SymbolTokens.Select(t => Oe(null, $"{anchor} {Escape(t)}"))]));

        // The anchor alone, as a control for the page being found at all.
        foreach (var token in new[] { anchor }.Concat(SymbolTokens))
        {
            var unindexed = await FindAsync(s, notebookId, token, includeUnindexed: true, pageId);
            var indexed = await FindAsync(s, notebookId, token, includeUnindexed: false, pageId);
            s.Recorder.Row("search-symbol", "Search (Q5)", "FindPages", new { query = token, start = "MCP Test" }, unindexed.Attempt, new
            {
                unindexed = new { unindexed.Found, unindexed.Pages, unindexed.Attempt.HResult },
                indexedAt0s = new { indexed.Found, indexed.Pages, indexed.Attempt.HResult },
            });
        }

        // Indexed-only again after a wait, for the anchor and the symbol tokens together.
        await Task.Delay(IndexWait, ProbeSession.Ct);
        foreach (var token in new[] { anchor }.Concat(SymbolTokens))
        {
            var indexed = await FindAsync(s, notebookId, token, includeUnindexed: false, pageId);
            s.Recorder.Row("search-symbol-indexed-later", "Search (Q5)", "FindPages", new { query = token, includeUnindexed = false, afterSeconds = IndexWait.TotalSeconds }, indexed.Attempt, new { indexed.Found, indexed.Pages });
        }
    }

    // --- FindMeta -------------------------------------------------------------------------------------------------

    private static async Task FindMetaAsync(ProbeSession s, string notebookId, string sectionId, string stamp)
    {
        const string Q = "Search (Q5)";
        var name = "probe-meta-" + stamp;
        var pageId = await s.CreatePageAsync(sectionId, $"probe findmeta {stamp}");
        await s.MustUpdateAsync(pageId, Meta(name, "x") + Outline(null, Oe(null, "fm-1")));

        foreach (var (when, delay) in new[] { ("0s", TimeSpan.Zero), ("30s", IndexWait) })
        {
            await Task.Delay(delay, ProbeSession.Ct);
            foreach (var unindexed in new[] { true, false })
            {
                var r = await FindMetaAsync(s, notebookId, name, unindexed, pageId);
                s.Recorder.Row("findmeta-fresh-page", Q, "FindMeta", new { metaName = "probe-meta-<stamp>", includeUnindexed = unindexed, when }, r.Attempt, new { r.Found, r.Pages, r.PagesCarryingMeta });
            }
        }

        // Ballast's Metas against the truth: how many pages carry them in their own page XML (hierarchy scope counted too).
        var tree = await s.TreeAsync(notebookId, CoreScope.Pages);
        foreach (var metaName in new[] { BallastContent.CompleteMeta, BallastContent.MarkMeta })
        {
            var hierarchyCount = tree.Descendants(One + "Page").Count(p => (string?)p.Attribute("isInRecycleBin") != "true"
                && p.Elements(One + "Meta").Any(m => (string?)m.Attribute("name") == metaName));
            foreach (var unindexed in new[] { true, false })
            {
                var r = await FindMetaAsync(s, notebookId, metaName, unindexed, null);
                s.Recorder.Row("findmeta-ballast", Q, "FindMeta", new { metaName, includeUnindexed = unindexed }, r.Attempt, new
                {
                    resultPages = r.Pages,
                    r.PagesCarryingMeta,
                    r.RecycleBinPages,
                    hierarchyPagesWithMeta = hierarchyCount,
                });
            }
        }
    }

    // --- encrypted on Ballast Vault -------------------------------------------------------------------------------

    private static async Task EncryptedVaultAsync(ProbeSession s, string notebookId)
    {
        // One capture in the Vault's current lock state, then Phill switches it and a second capture follows.
        var locked = await CaptureAsync("search-encrypted-vault-first");
        var instruction = locked == "true"
            ? "In OneNote, unlock the section Ballast Vault (MCP Test): select it and enter its password. Then continue."
            : "In OneNote, lock the section Ballast Vault (MCP Test): press Ctrl+Alt+L (Lock All Protected Sections). Then continue.";
        if (await s.PauseAsync("search-encrypted-vault-switch", instruction))
        {
            await CaptureAsync("search-encrypted-vault-second");
        }

        async Task<string?> CaptureAsync(string probe)
        {
            var vault = Vault(await s.TreeAsync(notebookId, CoreScope.Sections));
            var findPages = await TryXmlAsync(() => s.Gateway.FindPagesAsync(notebookId, "Ballast", includeUnindexed: true, ProbeSession.Ct));
            var findMeta = await TryXmlAsync(() => s.Gateway.FindMetaAsync(notebookId, BallastContent.CompleteMeta, includeUnindexed: true, ProbeSession.Ct));
            var vaultLocked = (string?)vault?.Attribute("locked");
            s.Recorder.Row(probe, "Search (Q5)", "FindPages+FindMeta", new { findPagesQuery = "Ballast", findMetaName = BallastContent.CompleteMeta, includeUnindexed = true }, findPages.Attempt, new
            {
                vaultLocked,
                hierarchy = Describe(vault),
                findPages = new { findPages.Attempt.HResult, vault = Describe(findPages.Root is null ? null : Vault(findPages.Root)) },
                findMeta = new { findMeta.Attempt.HResult, vault = Describe(findMeta.Root is null ? null : Vault(findMeta.Root)) },
            });
            return vaultLocked;
        }

        static XElement? Vault(XElement root) =>
            root.Descendants(One + "Section").FirstOrDefault(e => (string?)e.Attribute("name") == BallastContent.VaultSectionName && (string?)e.Attribute("isInRecycleBin") != "true");

        // Attributes and a page count only: nothing from inside the Vault.
        static object Describe(XElement? section) => section is null
            ? new { present = false }
            : new
            {
                present = true,
                encrypted = (string?)section.Attribute("encrypted"),
                locked = (string?)section.Attribute("locked"),
                pagesListed = section.Elements(One + "Page").Count(),
            };
    }

    // --- The guide skill's claims ---------------------------------------------------------------------------------

    private static async Task ListClaimAsync(ProbeSession s, string sectionId, string stamp)
    {
        var pageId = await s.CreatePageAsync(sectionId, $"probe lists {stamp}");
        var bullet = OeRaw(null, $"""<one:List><one:Bullet bullet="2" fontSize="11.0" /></one:List><one:T>{Cdata("li-bullet")}</one:T>""");
        var number = OeRaw(null, $"""<one:List><one:Number numberSequence="0" numberFormat="##." fontSize="11.0" font="Calibri" text="1." /></one:List><one:T>{Cdata("li-number")}</one:T>""");
        var attempt = await s.UpdateAsync(pageId, Page(pageId, Outline(null, bullet, number, Oe(null, "li-plain"))), DateTime.MinValue);
        var page = await s.PageAsync(pageId);
        s.Recorder.Row("skill-list-claim", "Search (Q5)", "UpdatePageContent", new { shape = "an OE with List/Bullet, one with List/Number, one plain" }, attempt, new
        {
            bullet = ListShape(OeByText(page, "li-bullet")),
            number = ListShape(OeByText(page, "li-number")),
            plain = ListShape(OeByText(page, "li-plain")),
        });

        // Read-only: lists Phill made in the OneNote UI on MCP Test Cloud's Text Page, if it's open.
        if (await TestNotebook.FindCloudAsync(s.Gateway, ProbeSession.Ct) is { } cloudId)
        {
            var cloudSection = TestNotebookGuard.FindSectionId((await s.TreeAsync(cloudId, CoreScope.Sections)).ToString(), ProbeSession.ProbesSectionName);
            var textPage = cloudSection is null ? null : await s.PageIdByTitleAsync(cloudSection, CloudWriteProbes.TextPageTitle);
            if (textPage is not null)
            {
                var ui = await s.PageAsync(textPage);
                s.Recorder.Row("skill-list-claim-ui-page", "Search (Q5)", "GetPageContent", new { page = "MCP Test Cloud > Probes > Text Page" }, null, new
                {
                    oes = Oes(ui).Count(),
                    oesWithList = Oes(ui).Count(oe => oe.Element(One + "List") is not null),
                    listKinds = Oes(ui).Select(oe => oe.Element(One + "List")?.Elements().FirstOrDefault()?.Name.LocalName).Where(n => n is not null).Distinct().ToList(),
                });
            }
        }
    }

    private static async Task ImageAltClaimAsync(ProbeSession s, string sectionId, string stamp)
    {
        var pageId = await s.CreatePageAsync(sectionId, $"probe image alt {stamp}");
        var image = $"""<one:Image format="png" alt="probe alt text"><one:Size width="40.0" height="40.0" /><one:Data>{PngBase64}</one:Data></one:Image>""";
        var attempt = await s.UpdateAsync(pageId, Page(pageId, Outline(null, OeRaw(null, image))), DateTime.MinValue);
        var page = await s.PageAsync(pageId);
        var images = page.Descendants(One + "Image").ToList();
        s.Recorder.Row("skill-image-alt-claim", "Search (Q5)", "UpdatePageContent", new { shape = "an OE with an Image (PNG, Data) and alt=\"probe alt text\"" }, attempt, new
        {
            images = images.Count,
            alt = images.Select(i => (string?)i.Attribute("alt")).ToList(),
        });
    }

    // --- Helpers --------------------------------------------------------------------------------------------------

    private static string Escape(string token) => token.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal);

    private static object? ListShape(XElement? oe) =>
        oe is null ? null : new { list = oe.Element(One + "List")?.Elements().Select(e => e.Name.LocalName).ToList() };

    private sealed record FindResult(Attempt Attempt, bool Found, int Pages, int PagesCarryingMeta, int RecycleBinPages);

    private static async Task<FindResult> FindAsync(ProbeSession s, string notebookId, string query, bool includeUnindexed, string pageId)
    {
        var r = await TryXmlAsync(() => s.Gateway.FindPagesAsync(notebookId, query, includeUnindexed, ProbeSession.Ct));
        return Summarise(r, pageId, null);
    }

    private static async Task<FindResult> FindMetaAsync(ProbeSession s, string notebookId, string name, bool includeUnindexed, string? pageId)
    {
        var r = await TryXmlAsync(() => s.Gateway.FindMetaAsync(notebookId, name, includeUnindexed, ProbeSession.Ct));
        return Summarise(r, pageId, name);
    }

    private static FindResult Summarise((Attempt Attempt, XElement? Root) r, string? pageId, string? metaName)
    {
        var pages = r.Root?.Descendants(One + "Page").ToList() ?? [];
        return new FindResult(
            r.Attempt,
            pageId is not null && pages.Any(p => (string?)p.Attribute("ID") == pageId),
            pages.Count,
            metaName is null ? 0 : pages.Count(p => p.Elements(One + "Meta").Any(m => (string?)m.Attribute("name") == metaName)),
            pages.Count(p => (string?)p.Attribute("isInRecycleBin") == "true" || p.Ancestors(One + "Section").Any(sec => (string?)sec.Attribute("isInRecycleBin") == "true")));
    }

    private static async Task<(Attempt Attempt, XElement? Root)> TryXmlAsync(Func<Task<string>> call)
    {
        string? xml = null;
        var attempt = await ProbeSession.TryAsync(async () => xml = await call());
        return (attempt, xml is null ? null : XDocument.Parse(xml).Root);
    }
}
