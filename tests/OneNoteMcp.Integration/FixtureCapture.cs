using System.Globalization;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using OneNoteMcp.Com;
using OneNoteMcp.Com.Interop;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Tests;
using OneNoteMcp.Xml;
using Xunit;
using CoreScope = OneNoteMcp.Core.HierarchyScope;
using CoreSpecialLocation = OneNoteMcp.Core.SpecialLocation;
using PageInfo = OneNoteMcp.Core.PageInfo;

namespace OneNoteMcp.Integration;

/// <summary>
/// Story 6's fixture capture, hitl and opt-in: <c>ONENOTE_MCP_INTEGRATION=1</c> plus <c>ONENOTE_MCP_SCENARIO</c>.
/// <list type="bullet">
/// <item><c>capture-fixtures</c> seeds a <c>Fixtures</c> section in MCP Test (writes only through
/// <see cref="TestNotebook.WriteAsync{T}"/>), records every read method into <c>tests/OneNoteMcp.Tests/Fixtures/</c>,
/// sanitised, and probes callbackID stability and the attributes OneNote changes on untouched elements.</item>
/// <item><c>capture-cloud</c> reads, read-only, the first page of the one section named <c>MCP Cloud Test 2</c> in the
/// cloud notebook <c>Phill's Notebook</c>: a throwaway page Phill made in OneNote for the web, with an image. Phill
/// reviews the sanitised file before commit.</item>
/// </list>
/// </summary>
public sealed class FixtureCapture : IDisposable
{
    public const string FixturesSectionName = "Fixtures";
    public const string MetaName = "onenote-mcp-fixture";
    public const string CloudNotebookName = "Phill's Notebook";
    public const string CloudSectionName = "MCP Cloud Test 2";

    // 2x2 PNG, four colours. OneNote re-encodes inserted images (spike 1), so its bytes won't round-trip.
    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGP4z8DwHwyBNBgw/IdQDACMsAj/6nrfNgAAAABJRU5ErkJggg==";

    private static readonly XNamespace One = OneNoteXml.Namespace2013;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string settingsDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-capture", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper output;

    public FixtureCapture(ITestOutputHelper output) => this.output = output;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string FixturesDir { get; } = Path.Combine(
        typeof(FixtureCapture).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "RepoRoot").Value!,
        "tests",
        "OneNoteMcp.Tests",
        "Fixtures");

    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // --- capture-fixtures -----------------------------------------------------------------------------------------

    [Fact]
    public async Task CaptureFixtures()
    {
        RequireScenario("capture-fixtures");
        using var gateway = Gateway();
        var notebook = await TestNotebook.EnsureAsync(gateway, Ct);
        var probe = new FixtureProbe { CapturedOn = Today() };

        if (Environment.GetEnvironmentVariable("ONENOTE_MCP_RESEED") == "1")
        {
            await RecycleSectionAsync(gateway, notebook.NotebookId);
        }

        var sectionId = await EnsureSectionAsync(gateway, notebook.NotebookId);
        var pages = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var seed in Seeds())
        {
            pages[seed.Title] = await EnsureSeedAsync(gateway, sectionId, seed, probe);
        }

        await EnsureSubpageAsync(gateway, sectionId, pages[SubpageTitle], probe);

        // Probe 1: callbackID stability across reads, and after an edit elsewhere in the notebook.
        var imagePageId = pages[ImageTitle];
        var callbackIds = new List<string?> { CallbackId(await gateway.GetPageContentAsync(imagePageId, PageInfo.Basic, Ct)) };
        callbackIds.Add(CallbackId(await gateway.GetPageContentAsync(imagePageId, PageInfo.Basic, Ct)));

        // Probe 2: edit one OE of the probe page's first Outline; compare the untouched elements before and after.
        var probePageId = pages[ProbeTitle];
        var before = await gateway.GetPageContentAsync(probePageId, PageInfo.Basic, Ct);
        var (outlineA, editedOe, outlineB) = ProbeTargets(before);
        var edit = $"""<?xml version="1.0"?><one:Page xmlns:one="{OneNoteXml.Namespace2013}" ID="{probePageId}"><one:Outline objectID="{outlineA}"><one:OEChildren><one:OE objectID="{editedOe}"><one:T><![CDATA[Probe edit {DateTime.UtcNow:O}]]></one:T></one:OE></one:OEChildren></one:Outline></one:Page>""";
        await TestNotebook.WriteAsync(
            gateway,
            probePageId,
            app =>
            {
                app.UpdatePageContent(edit, DateTime.MinValue, XMLSchema.xs2013, force: false);
                return true;
            },
            "UpdatePageContent(probe edit)",
            Ct);
        var after = await gateway.GetPageContentAsync(probePageId, PageInfo.Basic, Ct);
        callbackIds.Add(CallbackId(await gateway.GetPageContentAsync(imagePageId, PageInfo.Basic, Ct)));

        probe.CallbackIdStable = callbackIds[0] is not null && callbackIds.All(c => c == callbackIds[0]);
        probe.PageId = probePageId;
        probe.EditedObjectId = editedOe;
        probe.UntouchedOutlineId = outlineB;
        probe.ChangedAttributesOnUntouched = ChangedAttributesOnUntouched(before, after, editedOe);
        probe.UnexcludedChanges = probe.ChangedAttributesOnUntouched
            .Where(c => !ElementHash.ExcludedAttributes.Contains(c[(c.IndexOf('@', StringComparison.Ordinal) + 1)..]))
            .ToList();
        var beforeBasis = PageBasis.From(before);
        var afterBasis = PageBasis.From(after);
        probe.Notes.Add($"untouched Outline hash equal after the edit: {beforeBasis.ElementHashes[outlineB] == afterBasis.ElementHashes[outlineB]}");
        probe.Notes.Add($"edited OE hash changed: {beforeBasis.ElementHashes[editedOe] != afterBasis.ElementHashes[editedOe]}");
        probe.Notes.Add($"callbackIDs seen: {string.Join(", ", callbackIds.Select(c => c ?? "(none)"))}");

        // Record every read method.
        var recorder = new Recorder("mcp-test", notebook.NotebookId);
        recorder.ClearSource();
        probe.BeforeFile = recorder.WriteFile("probe/probe-before.xml", before);

        var nb = notebook.NotebookId;
        await recorder.Record("GetHierarchy", ["", "Notebooks"], "hierarchy/root-notebooks.xml", () => gateway.GetHierarchyAsync("", CoreScope.Notebooks, Ct), keepOnlyNotebook: true, note: "all notebooks, sanitised to MCP Test only");
        await recorder.Record("GetHierarchy", ["", "Pages"], "hierarchy/root-pages.xml", () => gateway.GetHierarchyAsync("", CoreScope.Pages, Ct), keepOnlyNotebook: true, note: "everything, sanitised to MCP Test only");
        foreach (var scope in Enum.GetValues<CoreScope>())
        {
            await recorder.Record("GetHierarchy", [nb, scope.ToString()], $"hierarchy/notebook-{scope}.xml", () => gateway.GetHierarchyAsync(nb, scope, Ct), note: $"MCP Test, {scope}");
        }

        await recorder.Record("GetHierarchy", [sectionId, "Self"], "hierarchy/section-Self.xml", () => gateway.GetHierarchyAsync(sectionId, CoreScope.Self, Ct), note: "Fixtures section");
        await recorder.Record("GetHierarchy", [sectionId, "Pages"], "hierarchy/section-Pages.xml", () => gateway.GetHierarchyAsync(sectionId, CoreScope.Pages, Ct), note: "Fixtures section");
        await recorder.Record("GetHierarchy", [imagePageId, "Self"], "hierarchy/page-Self.xml", () => gateway.GetHierarchyAsync(imagePageId, CoreScope.Self, Ct), note: ImageTitle);

        await recorder.Record("GetHierarchyParent", [imagePageId], "parent/page.txt", () => gateway.GetHierarchyParentAsync(imagePageId, Ct), note: "page -> Fixtures section");
        await recorder.Record("GetHierarchyParent", [sectionId], "parent/section.txt", () => gateway.GetHierarchyParentAsync(sectionId, Ct), note: "section -> MCP Test");
        await recorder.Record("GetHierarchyParent", [nb], "parent/notebook.txt", () => gateway.GetHierarchyParentAsync(nb, Ct), note: "notebook: the end of the chain (AD-6)");

        foreach (var (title, pageId) in pages)
        {
            var slug = Slug(title);
            await recorder.Record("GetPageContent", [pageId, "Basic"], $"page/{slug}-Basic.xml", () => gateway.GetPageContentAsync(pageId, PageInfo.Basic, Ct), note: title);
            await recorder.Record("GetPageContent", [pageId, "BinaryData"], $"page/{slug}-BinaryData.xml", () => gateway.GetPageContentAsync(pageId, PageInfo.BinaryData, Ct), note: title);
        }

        await recorder.Record("GetPageContent", [imagePageId, "All"], "page/image-All.xml", () => gateway.GetPageContentAsync(imagePageId, PageInfo.All, Ct), note: ImageTitle);

        var callbackId = callbackIds[^1] ?? throw new InvalidOperationException("The image page has no CallbackID.");
        await recorder.Record("GetBinaryPageContent", [imagePageId, callbackId], "binary/image.b64.txt", () => gateway.GetBinaryPageContentAsync(imagePageId, callbackId, Ct), note: "the seeded PNG, as OneNote re-encoded it");
        const string NoSuchCallback = "{00000000-0000-0000-0000-000000000000}{1}{B0}";
        await recorder.Record("GetBinaryPageContent", [imagePageId, NoSuchCallback], "binary/unknown-callback.txt", () => gateway.GetBinaryPageContentAsync(imagePageId, NoSuchCallback, Ct), note: "an unknown callbackID");

        await recorder.Record("FindPages", [nb, "Fixture", "true"], "search/findpages-fixture.xml", () => gateway.FindPagesAsync(nb, "Fixture", includeUnindexed: true, Ct), note: "unindexed included (AD-12)");
        await recorder.Record("FindMeta", [nb, MetaName, "true"], "search/findmeta-fixture.xml", () => gateway.FindMetaAsync(nb, MetaName, includeUnindexed: true, Ct), note: "unindexed included (AD-12)");

        await recorder.Record("GetHyperlinkToObject", [imagePageId, ""], "link/page.txt", () => gateway.GetHyperlinkToObjectAsync(imagePageId, "", Ct), note: ImageTitle);
        await recorder.Record("GetHyperlinkToObject", [probePageId, editedOe], "link/page-object.txt", () => gateway.GetHyperlinkToObjectAsync(probePageId, editedOe, Ct), note: "an OE on the probe page");
        await recorder.Record("GetHyperlinkToObject", [sectionId, ""], "link/section.txt", () => gateway.GetHyperlinkToObjectAsync(sectionId, "", Ct), note: "Fixtures section");
        await recorder.Record("GetWebHyperlinkToObject", [imagePageId, ""], "link/web-page.txt", () => gateway.GetWebHyperlinkToObjectAsync(imagePageId, "", Ct), note: "local notebook: empty (observed 2026-11-04)");

        foreach (var location in Enum.GetValues<CoreSpecialLocation>())
        {
            // These name the user's own folders and Quick Notes section, outside MCP Test: a OneDrive URL keeps its
            // shape, with the account ID and folder names replaced (FixtureSanitiser.ScrubCloudUrl).
            await recorder.Record(
                "GetSpecialLocation",
                [location.ToString()],
                $"special/{location}.txt",
                () => gateway.GetSpecialLocationAsync(location, Ct),
                note: $"{location}; outside MCP Test, so a OneDrive URL's account ID and folder names are placeholders",
                transform: FixtureSanitiser.ScrubCloudUrl);
        }

        var manifest = LoadManifest();
        manifest.Entries.RemoveAll(e => e.Source == "mcp-test");
        manifest.Entries.AddRange(recorder.Entries);
        manifest.Entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        manifest.Probe = probe;
        SaveManifest(manifest);

        output.WriteLine($"Fixtures written to {FixturesDir}: {recorder.Entries.Count} calls.");
        output.WriteLine($"Seeds: {string.Join("; ", probe.SeedResults.Select(kv => $"{kv.Key}: {kv.Value}"))}");
        output.WriteLine($"callbackID stable: {probe.CallbackIdStable}");
        output.WriteLine($"Attributes changed on untouched elements: {string.Join(", ", probe.ChangedAttributesOnUntouched.DefaultIfEmpty("(none)"))}");
        output.WriteLine($"Not excluded by ElementHash (report to Phill): {string.Join(", ", probe.UnexcludedChanges.DefaultIfEmpty("(none)"))}");
        foreach (var note in probe.Notes)
        {
            output.WriteLine(note);
        }
    }

    // --- capture-cloud --------------------------------------------------------------------------------------------

    [Fact]
    public async Task CaptureCloud()
    {
        RequireScenario("capture-cloud");
        using var gateway = Gateway();

        // Read-only throughout: no write, navigate or sync call is made on this notebook.
        var notebooks = XDocument.Parse(await gateway.GetHierarchyAsync("", CoreScope.Notebooks, Ct)).Root!;
        var notebook = Single(
            notebooks.Elements(One + "Notebook").Where(n => (string?)n.Attribute("name") == CloudNotebookName || (string?)n.Attribute("nickname") == CloudNotebookName),
            $"notebook named {CloudNotebookName}");
        if (!((string?)notebook.Attribute("path") ?? string.Empty).StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing: {CloudNotebookName} is not a cloud (https) notebook.");
        }

        var sections = XDocument.Parse(await gateway.GetHierarchyAsync((string)notebook.Attribute("ID")!, CoreScope.Sections, Ct)).Root!;
        var section = Single(
            sections.Descendants(One + "Section").Where(s => (string?)s.Attribute("name") == CloudSectionName && !InRecycleBin(s)),
            $"section named {CloudSectionName}");
        var sectionPages = XDocument.Parse(await gateway.GetHierarchyAsync((string)section.Attribute("ID")!, CoreScope.Pages, Ct)).Root!;
        // Exactly one page, so the capture never guesses which of Phill's pages is the throwaway one.
        var pageId = (string)Single(
            sectionPages.Elements(One + "Page").Where(p => (string?)p.Attribute("isInRecycleBin") != "true"),
            $"page in {CloudSectionName}").Attribute("ID")!;

        var basic = await gateway.GetPageContentAsync(pageId, PageInfo.Basic, Ct);
        var binary = await gateway.GetPageContentAsync(pageId, PageInfo.BinaryData, Ct);
        var cloud = new FixtureCloud
        {
            CapturedOn = Today(),
            BasicImageCount = ImageCount.Count(basic),
            BinaryImageCount = ImageCount.Count(binary),
        };
        if (cloud.BasicImageCount == 0)
        {
            throw new InvalidOperationException($"Refusing: the first page of {CloudSectionName} has no image. Add one in OneNote for the web.");
        }

        var manifest = LoadManifest();
        manifest.Entries.RemoveAll(e => e.Source is "cloud" or "hand-built");
        var recorder = new Recorder("cloud", notebookId: null);
        recorder.ClearSource();

        if (ImageCount.Missing(basic, binary) == 0)
        {
            // Desktop already holds the image binary: fall back to a hand-built fixture from spike 1's evidence
            // (piBinaryData drops each image with no local binary), built from the MCP Test image page.
            cloud.Source = "hand-built";
            cloud.Notes.Add($"{Today()}: desktop OneNote already held the cloud image binary (piBasic {cloud.BasicImageCount}, piBinaryData {cloud.BinaryImageCount}); fixture hand-built from spike 1 findings row 6.");
            BuildHandBuiltCloud(manifest, recorder, cloud);
        }
        else
        {
            cloud.Source = "cloud";
            var guidMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var mappedPageId = FixtureSanitiser.MapGuids(pageId, guidMap);
            cloud.PageId = mappedPageId;
            recorder.Add("GetPageContent", [mappedPageId, "Basic"], "page-Basic.xml", FixtureSanitiser.SanitiseCloud(basic, guidMap), "the cloud page, piBasic, sanitised");
            recorder.Add("GetPageContent", [mappedPageId, "BinaryData"], "page-BinaryData.xml", FixtureSanitiser.SanitiseCloud(binary, guidMap), "the cloud page, piBinaryData, sanitised: images without a local binary are dropped");

            var callbackId = CallbackId(basic);
            if (callbackId is not null)
            {
                var mappedCallback = FixtureSanitiser.MapGuids(callbackId, guidMap);
                await recorder.Record(
                    "GetBinaryPageContent",
                    [mappedPageId, mappedCallback],
                    "binary.b64.txt",
                    () => gateway.GetBinaryPageContentAsync(pageId, callbackId, Ct),
                    note: "the cloud image with no local binary (expected 0x8004200F)",
                    transform: _ => throw new InvalidOperationException("Refusing to store the cloud image's binary."));
            }
        }

        manifest.Entries.AddRange(recorder.Entries);
        manifest.Entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        manifest.Cloud = cloud;
        SaveManifest(manifest);

        output.WriteLine($"Cloud capture ({cloud.Source}): piBasic {cloud.BasicImageCount} images, piBinaryData {cloud.BinaryImageCount}.");
        output.WriteLine("Phill: review tests/OneNoteMcp.Tests/Fixtures/cloud/ and git diff before committing.");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(settingsDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    // --- Seeds ----------------------------------------------------------------------------------------------------

    private const string NestedTitle = "Fixture Nested";
    private const string TableTitle = "Fixture Table";
    private const string ImageTitle = "Fixture Image";
    private const string ProbeTitle = "Fixture Probe";
    private const string SubpageTitle = "Fixture Subpage";

    private sealed record Seed(string Title, Func<string, string> Body);

    /// <summary>Each seed is one partial <c>UpdatePageContent</c> on a new page: no objectIDs, only the page ID.</summary>
    private static IEnumerable<Seed> Seeds()
    {
        yield return new Seed(NestedTitle, _ => $"""
            <one:TagDef index="0" name="To Do" type="0" symbol="3" />
            <one:Meta name="{MetaName}" content="nested" />
            {Title(NestedTitle)}
            <one:Outline><one:OEChildren>
              <one:OE><one:Tag index="0" completed="false" /><one:T><![CDATA[Level 1, tagged To Do]]></one:T>
                <one:OEChildren>
                  <one:OE><one:T><![CDATA[Level 2 <b>bold</b> &amp; text]]></one:T>
                    <one:OEChildren><one:OE><one:T><![CDATA[Level 3]]></one:T></one:OE></one:OEChildren>
                  </one:OE>
                  <one:OE><one:Meta name="{MetaName}" content="oe" /><one:T><![CDATA[Level 2 sibling with Meta]]></one:T></one:OE>
                </one:OEChildren>
              </one:OE>
              <one:OE><one:T><![CDATA[Level 1 sibling]]></one:T></one:OE>
            </one:OEChildren></one:Outline>
            """);
        yield return new Seed(TableTitle, _ => $"""
            {Title(TableTitle)}
            <one:Outline><one:OEChildren><one:OE>
              <one:Table bordersVisible="true" hasHeaderRow="true">
                <one:Columns><one:Column index="0" width="100" /><one:Column index="1" width="120" /></one:Columns>
                <one:Row><one:Cell><one:OEChildren><one:OE><one:T><![CDATA[Header A]]></one:T></one:OE></one:OEChildren></one:Cell><one:Cell><one:OEChildren><one:OE><one:T><![CDATA[Header B]]></one:T></one:OE></one:OEChildren></one:Cell></one:Row>
                <one:Row><one:Cell><one:OEChildren><one:OE><one:T><![CDATA[A1]]></one:T></one:OE></one:OEChildren></one:Cell><one:Cell><one:OEChildren><one:OE><one:T><![CDATA[B1]]></one:T></one:OE></one:OEChildren></one:Cell></one:Row>
              </one:Table>
            </one:OE></one:OEChildren></one:Outline>
            """);
        yield return new Seed(ImageTitle, _ => $"""
            {Title(ImageTitle)}
            <one:Outline><one:OEChildren>
              <one:OE><one:T><![CDATA[A local PNG follows.]]></one:T></one:OE>
              <one:OE><one:Image format="png"><one:Size width="40" height="40" /><one:Data>{PngBase64}</one:Data></one:Image></one:OE>
            </one:OEChildren></one:Outline>
            """);
        yield return new Seed(ProbeTitle, _ => $"""
            {Title(ProbeTitle)}
            <one:Outline><one:OEChildren>
              <one:OE><one:T><![CDATA[Outline A, edited by each capture]]></one:T></one:OE>
              <one:OE><one:T><![CDATA[Outline A, untouched sibling]]></one:T></one:OE>
            </one:OEChildren></one:Outline>
            <one:Outline><one:Position x="36" y="300" z="1" /><one:OEChildren>
              <one:OE><one:T><![CDATA[Outline B, never edited]]></one:T></one:OE>
            </one:OEChildren></one:Outline>
            """);
        yield return new Seed(SubpageTitle, _ => $"""
            {Title(SubpageTitle)}
            <one:Outline><one:OEChildren><one:OE><one:T><![CDATA[A subpage (pageLevel 2).]]></one:T></one:OE></one:OEChildren></one:Outline>
            """);
    }

    private static string Title(string title) => $"<one:Title><one:OE><one:T><![CDATA[{title}]]></one:T></one:OE></one:Title>";

    private static async Task<string> EnsureSectionAsync(ComGateway gateway, string notebookId)
    {
        var tree = await gateway.GetHierarchyAsync(notebookId, CoreScope.Pages, Ct);
        return TestNotebookGuard.FindSectionId(tree, FixturesSectionName)
            ?? await TestNotebook.WriteAsync(
                gateway,
                notebookId,
                app =>
                {
                    app.OpenHierarchy(FixturesSectionName + ".one", notebookId, out var id, CreateFileType.cftSection);
                    return id;
                },
                "OpenHierarchy(Fixtures section)",
                Ct);
    }

    private static async Task<string> EnsureSeedAsync(ComGateway gateway, string sectionId, Seed seed, FixtureProbe probe)
    {
        if (await FindPageAsync(gateway, sectionId, seed.Title) is { } existing)
        {
            var seedErrors = SchemaValidation.Validate(SeedXml(existing, seed));
            probe.SeedResults[seed.Title] = $"existing page, seeded by an earlier capture (set ONENOTE_MCP_RESEED=1 to reseed); seed XML against the XSD: {(seedErrors.Count == 0 ? "valid" : string.Join(" / ", seedErrors))}";
            return existing;
        }

        var pageId = await TestNotebook.WriteAsync(
            gateway,
            sectionId,
            app =>
            {
                app.CreateNewPage(sectionId, out var id, Com.Interop.NewPageStyle.npsBlankPageWithTitle);
                return id;
            },
            $"CreateNewPage({seed.Title})",
            Ct);

        var xml = SeedXml(pageId, seed);
        try
        {
            await TestNotebook.WriteAsync(
                gateway,
                pageId,
                app =>
                {
                    app.UpdatePageContent(xml, DateTime.MinValue, XMLSchema.xs2013, force: false);
                    return true;
                },
                $"UpdatePageContent({seed.Title})",
                Ct);
            var errors = SchemaValidation.Validate(xml);
            probe.SeedResults[seed.Title] = $"OneNote accepted the partial UpdatePageContent ({Today()}); XSD: {(errors.Count == 0 ? "valid" : string.Join(" / ", errors))}";
        }
        catch (OneNoteException ex)
        {
            probe.SeedResults[seed.Title] = $"OneNote rejected the partial UpdatePageContent: {ex.Code} {ex.Message}";
            throw;
        }

        return pageId;
    }

    private static string SeedXml(string pageId, Seed seed) =>
        $"""<?xml version="1.0"?><one:Page xmlns:one="{OneNoteXml.Namespace2013}" ID="{pageId}">{seed.Body(pageId)}</one:Page>""";

    /// <summary>
    /// <c>ONENOTE_MCP_RESEED=1</c>: moves the Fixtures section to MCP Test's recycle bin (a soft delete, through the
    /// guard), so this capture seeds fresh pages and records whether OneNote accepts each partial seed.
    /// </summary>
    private static async Task RecycleSectionAsync(ComGateway gateway, string notebookId)
    {
        var tree = await gateway.GetHierarchyAsync(notebookId, CoreScope.Pages, Ct);
        if (TestNotebookGuard.FindSectionId(tree, FixturesSectionName) is not { } sectionId)
        {
            return;
        }

        await TestNotebook.WriteAsync(
            gateway,
            sectionId,
            app =>
            {
                app.DeleteHierarchy(sectionId, DateTime.MinValue, deletePermanently: false);
                return true;
            },
            "DeleteHierarchy(Fixtures section, to the recycle bin)",
            Ct);
    }

    /// <summary>Makes the subpage seed a subpage: <c>pageLevel="2"</c> through <c>UpdateHierarchy</c> on its own section.</summary>
    private static async Task EnsureSubpageAsync(ComGateway gateway, string sectionId, string pageId, FixtureProbe probe)
    {
        var section = XDocument.Parse(await gateway.GetHierarchyAsync(sectionId, CoreScope.Pages, Ct));
        var page = section.Root!.Elements(One + "Page").Single(p => (string?)p.Attribute("ID") == pageId);
        if ((string?)page.Attribute("pageLevel") == "2")
        {
            probe.SeedResults[SubpageTitle + " (level)"] = "already pageLevel 2";
            return;
        }

        page.SetAttributeValue("pageLevel", "2");
        var changes = section.Declaration + section.Root.ToString(SaveOptions.DisableFormatting);
        await TestNotebook.WriteAsync(
            gateway,
            sectionId,
            app =>
            {
                app.UpdateHierarchy(changes, XMLSchema.xs2013);
                return true;
            },
            "UpdateHierarchy(subpage level)",
            Ct);

        var level = (string?)XDocument.Parse(await gateway.GetHierarchyAsync(sectionId, CoreScope.Pages, Ct)).Root!
            .Elements(One + "Page").Single(p => (string?)p.Attribute("ID") == pageId).Attribute("pageLevel");
        probe.SeedResults[SubpageTitle + " (level)"] = $"UpdateHierarchy set pageLevel; read back: {level ?? "(none)"}";
    }

    private static async Task<string?> FindPageAsync(ComGateway gateway, string sectionId, string title) =>
        XDocument.Parse(await gateway.GetHierarchyAsync(sectionId, CoreScope.Pages, Ct)).Root!
            .Elements(One + "Page")
            .Where(p => (string?)p.Attribute("name") == title && (string?)p.Attribute("isInRecycleBin") != "true")
            .Select(p => (string?)p.Attribute("ID"))
            .FirstOrDefault();

    // --- Probe helpers --------------------------------------------------------------------------------------------

    private static (string OutlineA, string EditedOe, string OutlineB) ProbeTargets(string pageXml)
    {
        var outlines = XDocument.Parse(pageXml).Root!.Elements(One + "Outline").ToList();
        if (outlines.Count < 2)
        {
            throw new InvalidOperationException($"The probe page should have two Outlines; it has {outlines.Count}.");
        }

        var editedOe = outlines[0].Element(One + "OEChildren")!.Elements(One + "OE").First();
        return ((string)outlines[0].Attribute("objectID")!, (string)editedOe.Attribute("objectID")!, (string)outlines[1].Attribute("objectID")!);
    }

    /// <summary>
    /// Attributes that differ between the two reads anywhere on the page except inside the edited OE: the root
    /// <c>one:Page</c>, the edited OE's ancestors, and every element of the untouched content, ID-less ones
    /// (<c>one:T</c>, <c>one:Position</c>, <c>one:Size</c>) included. Elements are paired by position: the same name at
    /// the same index among same-named siblings under the paired parent. As <c>element@attribute</c>.
    /// </summary>
    internal static List<string> ChangedAttributesOnUntouched(string beforeXml, string afterXml, string editedObjectId)
    {
        var changed = new SortedSet<string>(StringComparer.Ordinal);
        Compare(XDocument.Parse(beforeXml).Root!, XDocument.Parse(afterXml).Root!, editedObjectId, changed);
        return [.. changed];
    }

    private static void Compare(XElement before, XElement after, string editedObjectId, SortedSet<string> changed)
    {
        if ((string?)after.Attribute("objectID") == editedObjectId)
        {
            return; // the edit itself: its changes are expected
        }

        foreach (var name in after.Attributes().Concat(before.Attributes()).Where(a => !a.IsNamespaceDeclaration).Select(a => a.Name).Distinct())
        {
            if ((string?)after.Attribute(name) != (string?)before.Attribute(name))
            {
                changed.Add($"{after.Name.LocalName}@{name.LocalName}");
            }
        }

        foreach (var group in after.Elements().GroupBy(e => e.Name))
        {
            var olds = before.Elements(group.Key).ToList();
            var index = 0;
            foreach (var child in group)
            {
                if (index < olds.Count)
                {
                    Compare(olds[index], child, editedObjectId, changed);
                }

                index++;
            }
        }
    }

    private static string? CallbackId(string pageXml) =>
        (string?)XDocument.Parse(pageXml).Root!.Descendants(One + "Image").Elements(One + "CallbackID").FirstOrDefault()?.Attribute("callbackID");

    // --- Cloud helpers --------------------------------------------------------------------------------------------

    private static bool InRecycleBin(XElement section) =>
        (string?)section.Attribute("isInRecycleBin") == "true"
        || section.Ancestors(One + "SectionGroup").Any(g => (string?)g.Attribute("isRecycleBin") == "true");

    private static XElement Single(IEnumerable<XElement> candidates, string what)
    {
        var list = candidates.ToList();
        return list.Count == 1
            ? list[0]
            : throw new InvalidOperationException($"Refusing: expected exactly one {what}, found {list.Count}.");
    }

    /// <summary>
    /// The fallback: the MCP Test image page's piBasic read as the "cloud" piBasic, and the same XML with every
    /// <c>one:Image</c> removed as its piBinaryData, which is what spike 1 saw OneNote return for a cloud image with
    /// no local binary.
    /// </summary>
    private static void BuildHandBuiltCloud(FixtureManifest manifest, Recorder recorder, FixtureCloud cloud)
    {
        var source = manifest.Entries.SingleOrDefault(e => e.Source == "mcp-test" && e.Method == "GetPageContent" && e.Note == ImageTitle && e.Args[1] == "Basic")
            ?? throw new InvalidOperationException("Run capture-fixtures first: the hand-built cloud fixture is built from its image page.");
        var basic = File.ReadAllText(Path.Combine(FixturesDir, source.File!), Utf8);
        var guidMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var cloudBasic = FixtureSanitiser.SanitiseCloud(basic, guidMap);
        var document = XDocument.Parse(cloudBasic, LoadOptions.PreserveWhitespace);
        foreach (var image in document.Descendants(One + "Image").ToList())
        {
            // Observed 2026-11-04 on the real cloud capture: OneNote drops the Image and keeps its OE, empty.
            image.Remove();
        }

        var cloudBinary = cloudBasic[..cloudBasic.IndexOf("<one:", StringComparison.Ordinal)] + document.Root!.ToString(SaveOptions.DisableFormatting);
        cloud.PageId = FixtureSanitiser.MapGuids(source.Args[0], guidMap);
        recorder.Add("GetPageContent", [cloud.PageId, "Basic"], "page-Basic.xml", cloudBasic, "hand-built from spike 1 evidence: piBasic with an image", "hand-built");
        recorder.Add("GetPageContent", [cloud.PageId, "BinaryData"], "page-BinaryData.xml", cloudBinary, "hand-built from spike 1 evidence: piBinaryData drops the image", "hand-built");
    }

    // --- Shared ---------------------------------------------------------------------------------------------------

    private static string Today() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Slug(string title) => title.Replace("Fixture ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static FixtureManifest LoadManifest()
    {
        var path = Path.Combine(FixturesDir, FixtureManifest.FileName);
        return File.Exists(path) ? FixtureManifest.Load(path) : new FixtureManifest();
    }

    private static void SaveManifest(FixtureManifest manifest)
    {
        // One line ending throughout: the serializer writes Environment.NewLine (CRLF here), so normalise to \n.
        var text = System.Text.Json.JsonSerializer.Serialize(manifest, FixtureManifest.JsonOptions).ReplaceLineEndings("\n") + "\n";
        FixtureSanitiser.EnsureClean(text, Environment.UserName, FixtureManifest.FileName);
        Directory.CreateDirectory(FixturesDir);
        File.WriteAllText(Path.Combine(FixturesDir, FixtureManifest.FileName), text, Utf8);
    }

    private ComGateway Gateway()
    {
        Directory.CreateDirectory(settingsDir);
        var path = Path.Combine(settingsDir, "settings.json");
        File.WriteAllText(path, """{ "schemaVersion": 1 }""");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
        return new ComGateway(new ProcessProbe(), new SettingsStore(path));
    }

    private static void RequireScenario(string name)
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("ONENOTE_MCP_SCENARIO") == name,
            $"Hitl scenario: set ONENOTE_MCP_SCENARIO={name}.");
    }

    /// <summary>Runs one read, sanitises its result, refuses anything still holding the user name, and records it.</summary>
    private sealed class Recorder(string source, string? notebookId)
    {
        public List<FixtureEntry> Entries { get; } = [];

        /// <summary>Deletes this source's earlier files, so a fixture that's no longer captured can't linger.</summary>
        public void ClearSource()
        {
            var directory = Path.Combine(FixturesDir, source);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        public async Task Record(
            string method,
            string[] args,
            string file,
            Func<Task<string>> read,
            bool keepOnlyNotebook = false,
            string? note = null,
            Func<string, string>? transform = null)
        {
            var entry = new FixtureEntry { Method = method, Args = [.. args], Source = source, Note = note };
            try
            {
                var text = await read();
                if (keepOnlyNotebook)
                {
                    text = FixtureSanitiser.KeepOnlyNotebook(text, notebookId!);
                }

                entry.File = WriteFile(file, transform is null ? text : transform(text));
            }
            catch (OneNoteException ex)
            {
                entry.Error = new FixtureError
                {
                    Code = ex.Code.ToString(),
                    HResult = ex.ComHResult is { } hr ? $"0x{unchecked((uint)hr):X8}" : null,
                    Message = FixtureSanitiser.Scrub(ex.Message, UserProfile),
                };
            }

            Entries.Add(entry);
        }

        public void Add(string method, string[] args, string file, string text, string note, string? entrySource = null) =>
            Entries.Add(new FixtureEntry { Method = method, Args = [.. args], Source = entrySource ?? source, Note = note, File = WriteFile(file, text) });

        /// <summary>Writes <paramref name="text"/>, scrubbed, under this source's folder; returns its path relative to <c>Fixtures/</c>.</summary>
        public string WriteFile(string file, string text)
        {
            var relative = source + "/" + file;
            var clean = FixtureSanitiser.Scrub(text, UserProfile);
            FixtureSanitiser.EnsureClean(clean, Environment.UserName, relative);
            var path = Path.Combine(FixturesDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, clean, Utf8);
            return relative;
        }
    }
}
