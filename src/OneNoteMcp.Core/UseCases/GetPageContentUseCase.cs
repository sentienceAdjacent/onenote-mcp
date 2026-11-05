using OneNoteMcp.Core.Results;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>GetPageContent</c>. Always reads piBasic first and computes <c>meta.basis</c> from it (AD-17). With
/// <c>includeBinary</c> it then reads piBinaryData and returns that XML instead. Images in the piBasic read that the
/// piBinaryData read lacks have no local binary, so their count goes in <c>meta.binaryUnavailable</c>, with one
/// <c>binaryUnavailable</c> warning (AD-4). The XML is returned unchanged. Throws only <see cref="OneNoteException"/>.
/// </summary>
/// <remarks>
/// Basic is read first so that, if the page changes between the two reads, the basis is the older one and a write
/// made with it fails safe as <c>StaleWrite</c>. The basis never comes from piBinaryData, whose hashes differ for a
/// cloud page (see <see cref="PageBasis"/>), so it is the same whatever <c>includeBinary</c> is.
/// </remarks>
public sealed class GetPageContentUseCase(IOneNoteGateway gateway) : IUseCase
{
    public OperationClass Class => OperationClass.Read;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string pageId, bool includeBinary, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var id = pageId ?? string.Empty;
        var basic = await gateway.GetPageContentAsync(id, PageInfo.Basic, ct).ConfigureAwait(false);
        var basis = PageBasisResult.From(Readable(() => PageBasis.From(basic), id));
        var meta = new ToolMeta(context.CorrelationId, Basis: basis);
        if (!includeBinary)
        {
            return ToolSuccess.WithXml(basic, meta);
        }

        var binary = await gateway.GetPageContentAsync(id, PageInfo.BinaryData, ct).ConfigureAwait(false);
        var missing = Readable(() => ImageCount.Missing(basic, binary), id);
        if (missing == 0)
        {
            return ToolSuccess.WithXml(binary, meta);
        }

        // The page's ID and the count are the only details in the message, never content (AD-9).
        ToolWarning[] warnings =
        [
            new ToolWarning(
                ToolWarnings.BinaryUnavailable,
                $"{missing} image(s) on page {id} have no binary on this PC, so the returned XML leaves them out. They are in a notebook stored online and haven't been downloaded.",
                "Open the page in OneNote so it downloads the images, then read it again."),
        ];
        return new ToolSuccess(binary, meta with { BinaryUnavailable = missing }, warnings);
    }

    private static T Readable<T>(Func<T> read, string pageId)
    {
        try
        {
            return read();
        }
        catch (OneNoteXmlException ex)
        {
            // OneNoteXmlException messages carry only positions and element names, never content (AD-13).
            throw new OneNoteException(ErrorCode.InvalidXml, $"GetPageContent returned XML for page {pageId} that the server can't read: {ex.Message}", innerException: ex);
        }
    }
}
