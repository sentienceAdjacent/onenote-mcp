using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>GetPageContent</c>. Always reads piBasic first and computes <c>meta.basis</c> from it (AD-17). With
/// <c>includeBinary</c> it then reads piBinaryData and returns that XML instead. Images in the piBasic read that the
/// piBinaryData read lacks have no local binary, so their count goes in <c>meta.binaryUnavailable</c>, with one
/// <c>binaryUnavailable</c> warning (AD-4). The XML is returned unchanged. Throws only <see cref="OneNoteException"/>.
/// Before any page read, the AD-21 check resolves the page's section, and a page in a password-protected section needs
/// a read grant, asked for through the dialog when the client supports it; without one it is <c>ReadNotGranted</c>.
/// </summary>
/// <remarks>
/// Basic is read first so that, if the page changes between the two reads, the basis is the older one and a write
/// made with it fails safe as <c>StaleWrite</c>. The basis never comes from piBinaryData, whose hashes differ for a
/// cloud page (see <see cref="PageBasis"/>), so it is the same whatever <c>includeBinary</c> is.
/// <para>Every <c>pathCache</c> in the XML it returns goes into <paramref name="issued"/>, so a write that sends it back
/// unchanged isn't asked about OneNote's own temp copy again (Phill, 2026-11-12, after the live sitting's B3).</para>
/// </remarks>
public sealed class GetPageContentUseCase(IOneNoteGateway gateway, ReadAccess access, IssuedPathCaches? issued = null) : IUseCase
{
    public GetPageContentUseCase(IOneNoteGateway gateway)
        : this(gateway, ReadAccess.WithoutStoredGrants(gateway))
    {
    }

    public OperationClass Class => OperationClass.Read;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string pageId, bool includeBinary, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var id = pageId ?? string.Empty;
        var granted = await access.CheckObjectAsync(context, id, ct).ConfigureAwait(false);
        var success = await ReadAsync(context, id, includeBinary, ct).ConfigureAwait(false);
        return success with { Warnings = [.. granted.Warnings, .. success.Warnings], Grants = granted.ResultGrants };
    }

    private async Task<ToolSuccess> ReadAsync(ToolCallContext context, string id, bool includeBinary, CancellationToken ct)
    {
        var basic = await gateway.GetPageContentAsync(id, PageInfo.Basic, ct).ConfigureAwait(false);
        var basis = PageBasisResult.From(Readable(() => PageBasis.From(basic), id));
        var meta = new ToolMeta(context.CorrelationId, Basis: basis);

        // Kept with an oversized result so part hashes always come from piBasic (AD-17, AD-20).
        var source = new ResultSource(ResultKind.PageXml, PiBasicXml: basic);
        if (!includeBinary)
        {
            issued?.Remember(id, basic);
            return ToolSuccess.WithXml(basic, meta) with { Source = source };
        }

        var binary = await gateway.GetPageContentAsync(id, PageInfo.BinaryData, ct).ConfigureAwait(false);
        issued?.Remember(id, binary);
        var missing = Readable(() => ImageCount.Missing(basic, binary), id);
        if (missing == 0)
        {
            return ToolSuccess.WithXml(binary, meta) with { Source = source };
        }

        // The page's ID and the count are the only details in the message, never content (AD-9).
        ToolWarning[] warnings =
        [
            new ToolWarning(
                ToolWarnings.BinaryUnavailable,
                $"{missing} image(s) on page {id} have no binary on this PC, so the returned XML leaves them out. They are in a notebook stored online and haven't been downloaded.",
                "The user can turn on File > Options > Sync > \"Download all files and images\" in OneNote so images are kept on this PC. "
                + "Or, only if the user asks for it, show the page in OneNote with navigate_to (user-visible: it takes focus), which makes OneNote download the page's images, then read the page again."),
        ];
        return new ToolSuccess(binary, meta with { BinaryUnavailable = missing }, warnings) { Source = source };
    }

    private static T Readable<T>(Func<T> read, string pageId) => XmlReads.Readable(read, $"GetPageContent for page {pageId}");
}
