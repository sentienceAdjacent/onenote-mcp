using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>GetBinaryPageContent</c>: one binary object of a page, OneNote's base64 string unchanged in <c>meta.binary</c>.
/// Two failures are degraded reads, never errors (AD-4, AD-9): <c>ok: true</c>, no <c>meta.binary</c>, and the
/// <c>binaryUnavailable</c> warning.
/// <list type="bullet">
/// <item><c>0x8004200F</c> hrBinaryObjectDoesNotExist. OneNote returns it both for a binary that isn't on this PC and for a
/// wrong callbackID, so the warning names both.</item>
/// <item><c>0x80042014</c> hrObjectDoesNotExist for a File Printout's <c>one:XPSFile</c> (spike 1.15, quirk 2). Only on that
/// error path, the server reads the page's piBasic once to confirm the callbackID is an <c>XPSFile</c>'s. Any other
/// <c>ObjectNotFound</c>, or a confirming read that fails, stays the original error, except a confirming read that times
/// out or is cancelled: that <c>Timeout</c> or <c>Cancelled</c> is the result.</item>
/// </list>
/// Every other error passes through unchanged. The use case never calls <c>NavigateTo</c> (AD-11). Before the binary
/// read, the AD-21 check resolves the page's section, as for <see cref="GetPageContentUseCase"/>.
/// </summary>
public sealed class GetBinaryPageContentUseCase(IOneNoteGateway gateway, ReadAccess access) : IUseCase
{
    public GetBinaryPageContentUseCase(IOneNoteGateway gateway)
        : this(gateway, ReadAccess.WithoutStoredGrants(gateway))
    {
    }

    /// <summary>The <c>binaryUnavailable</c> userAction for a binary that isn't on this PC (story 1.21).</summary>
    public const string NotOnThisPcUserAction =
        "Check the callbackID against a fresh get_page_content read. If it is current, the image isn't on this PC. "
        + "The user can turn on File > Options > Sync > \"Download all files and images\" in OneNote so images are kept on this PC. "
        + "Or, only if the user asks for it, show the page in OneNote with navigate_to (user-visible: it takes focus), which makes OneNote download the page's images, then retry.";

    public OperationClass Class => OperationClass.Read;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string pageId, string callbackId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var page = pageId ?? string.Empty;
        var callback = callbackId ?? string.Empty;
        var granted = await access.CheckObjectAsync(context, page, ct).ConfigureAwait(false);
        var success = await ReadAsync(context, page, callback, ct).ConfigureAwait(false);
        return success with { Warnings = [.. granted.Warnings, .. success.Warnings], Grants = granted.ResultGrants };
    }

    private async Task<ToolSuccess> ReadAsync(ToolCallContext context, string page, string callback, CancellationToken ct)
    {
        try
        {
            var binary = await gateway.GetBinaryPageContentAsync(page, callback, ct).ConfigureAwait(false);
            return new ToolSuccess(null, new ToolMeta(context.CorrelationId, Binary: binary), []) { Source = new ResultSource(ResultKind.Text) };
        }
        catch (OneNoteException ex) when (ex.ComHResult == OneNoteHResults.BinaryObjectDoesNotExist)
        {
            // Caught here, so ToolRunner never logs it as an unmapped Internal failure. IDs only in the message (AD-9).
            return Unavailable(
                context,
                $"OneNote has no binary for callbackID {callback} on page {page}. Either it isn't stored on this PC (an image in a notebook stored online that hasn't been downloaded) or the callbackID is wrong; OneNote reports both the same way.",
                NotOnThisPcUserAction);
        }
        catch (OneNoteException ex) when (ex.ComHResult == OneNoteHResults.ObjectDoesNotExist)
        {
            if (!await IsXpsFileAsync(page, callback, ct).ConfigureAwait(false))
            {
                throw;
            }

            return Unavailable(
                context,
                $"callbackID {callback} on page {page} is a File Printout's one:XPSFile, the printout document itself. File printouts can't be read through the OneNote API.",
                "Don't retry. Tell the user the printout document can't be read here. The printout's page images (one:Image with isPrintOut=\"true\") have their own callbackIDs and are read like any other image.");
        }
    }

    private static ToolSuccess Unavailable(ToolCallContext context, string message, string userAction) =>
        new(null, new ToolMeta(context.CorrelationId), [new ToolWarning(ToolWarnings.BinaryUnavailable, message, userAction)]);

    /// <summary>
    /// One piBasic read of the page. A failed read or unreadable XML means "not confirmed", so the caller rethrows the
    /// original error. A read that times out or is cancelled propagates: the original error would hide it.
    /// </summary>
    private async Task<bool> IsXpsFileAsync(string pageId, string callbackId, CancellationToken ct)
    {
        string basic;
        try
        {
            basic = await gateway.GetPageContentAsync(pageId, PageInfo.Basic, ct).ConfigureAwait(false);
        }
        catch (OneNoteException ex) when (ex.Code is not (ErrorCode.Timeout or ErrorCode.Cancelled))
        {
            return false;
        }

        try
        {
            return XpsFiles.IsXpsFileCallbackId(basic, callbackId);
        }
        catch (OneNoteXmlException)
        {
            return false;
        }
    }
}
