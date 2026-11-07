using OneNoteMcp.Core.Results;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>GetBinaryPageContent</c>: one binary object of a page, OneNote's base64 string unchanged in <c>meta.binary</c>.
/// <c>0x8004200F</c> hrBinaryObjectDoesNotExist is a degraded read, never an error (AD-4, AD-9): <c>ok: true</c>, no
/// <c>meta.binary</c>, and the <c>binaryUnavailable</c> warning. OneNote returns that code both for a binary that isn't
/// on this PC and for a wrong callbackID, so the warning names both. Every other error passes through unchanged.
/// </summary>
public sealed class GetBinaryPageContentUseCase(IOneNoteGateway gateway) : IUseCase
{
    /// <summary>hrBinaryObjectDoesNotExist.</summary>
    public const int BinaryObjectDoesNotExist = unchecked((int)0x8004200F);

    public OperationClass Class => OperationClass.Read;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string pageId, string callbackId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var page = pageId ?? string.Empty;
        var callback = callbackId ?? string.Empty;
        try
        {
            var binary = await gateway.GetBinaryPageContentAsync(page, callback, ct).ConfigureAwait(false);
            return new ToolSuccess(null, new ToolMeta(context.CorrelationId, Binary: binary), []) { Source = new ResultSource(ResultKind.Text) };
        }
        catch (OneNoteException ex) when (ex.ComHResult == BinaryObjectDoesNotExist)
        {
            // Caught here, so ToolRunner never logs it as an unmapped Internal failure. IDs only in the message (AD-9).
            ToolWarning[] warnings =
            [
                new ToolWarning(
                    ToolWarnings.BinaryUnavailable,
                    $"OneNote has no binary for callbackID {callback} on page {page}. Either it isn't stored on this PC (an image in a notebook stored online that hasn't been downloaded) or the callbackID is wrong; OneNote reports both the same way.",
                    "Check the callbackID against a fresh get_page_content read. If it is current, open the page in OneNote so it downloads the image, then retry."),
            ];
            return new ToolSuccess(null, new ToolMeta(context.CorrelationId), warnings);
        }
    }
}
