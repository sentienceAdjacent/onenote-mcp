using System.Globalization;
using System.Xml.Linq;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Integration.WriteProbes;
using OneNoteMcp.Xml;
using Xunit;
using CorePageInfo = OneNoteMcp.Core.PageInfo;

namespace OneNoteMcp.Integration;

/// <summary>
/// Story 2.11's attachment probe (Phill, 2026-11-12, plan Q2), opt-in: <c>ONENOTE_MCP_INTEGRATION=1</c>, MCP Test only,
/// behind <see cref="TestNotebook"/>'s guard. Unprobed until it runs: whether OneNote accepts a <c>one:InsertedFile</c>
/// sent with only <c>pathCache</c> (its own temporary copy). On a fresh page in MCP Test's <c>Seed</c> section it inserts a
/// small text file, lets <c>update_page_content</c> snapshot the page, restores that entry, and prints
/// <c>RESULT restore-attachment: kept|dropped|error &lt;hex&gt;</c>: kept when the restored page holds the attachment,
/// dropped when it doesn't (the restore's <c>binaryMissing</c> says whether it was never sent or OneNote refused it), error
/// when the restore failed. Paths are never printed. The pages stay in Seed; the temporary files are deleted. Never run by
/// the build.
/// </summary>
public sealed class RestoreAttachmentProbe(ITestOutputHelper output) : IDisposable
{
    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-restore-attachment", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task AnAttachmentSentWithPathCacheOnly_RecordsWhetherOneNoteKeepsIt()
    {
        var rig = await RestoreRig.StartAsync(tempDir, Ct);
        using var _ = rig.Gateway;
        var notebook = await TestNotebook.EnsureAsync(rig.Gateway, Ct);
        var pageId = await RestoreFromJournalIntegrationTests.FreshPageAsync(rig.Gateway, notebook.SectionId, "restore-attachment probe", output, Ct);

        var source = Path.Combine(tempDir, "restore-attachment-probe.txt");
        await File.WriteAllTextAsync(source, "restore-attachment probe " + Guid.NewGuid().ToString("N"), Ct);
        var withFile = ProbeXml.Page(pageId, ProbeXml.Outline(
            null,
            ProbeXml.Oe(null, "restore-attachment probe"),
            $"<one:OE><one:InsertedFile pathSource=\"{ProbeXml.Attr(source)}\" preferredName=\"restore-attachment-probe.txt\" /></one:OE>"));
        await TestNotebook.WriteAsync(
            rig.Gateway,
            pageId,
            app =>
            {
                app.UpdatePageContent(withFile, DateTime.MinValue, Com.Interop.XMLSchema.xs2013, false);
                return true;
            },
            "UpdatePageContent(restore-attachment probe file)",
            Ct);

        // What OneNote recorded for the file, as booleans only: no path is printed.
        var basic = await rig.Gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct);
        var file = XDocument.Parse(basic).Descendants(One + "InsertedFile").SingleOrDefault();
        var pathCache = (string?)file?.Attribute("pathCache");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"inserted={file is not null} pathCache={pathCache is not null} keptByQ2Rule={pathCache is not null && RestoreAttachments.Keeps(pathCache)}"));

        // update_page_content adds an Outline; its snapshot is the entry.
        var basis = PageBasis.From(basic);
        await TestNotebook.EnsureTestTargetAsync(rig.Gateway, pageId, "UpdatePageContent", Ct);
        var updated = await rig.Update.ExecuteAsync(
            new ToolCallContext("probe-2.11-update"),
            pageId,
            ProbeXml.Page(pageId, ProbeXml.Outline(null, ProbeXml.Oe(null, "later"))),
            new PageWriteBasis(basis.LastModifiedTime, basis.PageHash, basis.ElementHashes),
            null,
            Ct);

        await TestNotebook.EnsureTestTargetAsync(rig.Gateway, notebook.SectionId, "restore_from_journal", Ct);
        string result;
        try
        {
            var restored = await rig.Restore.ExecuteAsync(new ToolCallContext("probe-2.11-restore"), updated.Meta.JournalEntryId!, Ct);
            var page = XDocument.Parse(await rig.Gateway.GetPageContentAsync(restored.Meta.PageId!, CorePageInfo.Basic, Ct)).Root!;
            var kept = page.Descendants(One + "InsertedFile").Any();
            var warning = restored.Warnings.FirstOrDefault(w => w.Code == ToolWarnings.BinaryMissing);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"restored page {restored.Meta.PageId}; binaryMissing={warning is not null} refusedByOneNote={warning?.Message.Contains("refused", StringComparison.Ordinal) ?? false}"));
            result = kept ? "kept" : "dropped";
        }
        catch (OneNoteException ex)
        {
            output.WriteLine($"restore failed: {ex.Code}");
            result = "error " + (ex.ComHResult is { } hr ? ToolErrors.FormatHResult(hr) : ex.Code.ToString());
        }

        output.WriteLine($"RESULT restore-attachment: {result}");
    }
}
