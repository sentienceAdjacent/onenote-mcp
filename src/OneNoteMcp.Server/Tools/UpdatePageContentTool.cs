using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server.Grants;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// <c>UpdatePageContent</c>. Thin (AD-5): the policy is in <see cref="UpdatePageContentUseCase"/> and the write pipeline
/// (AD-7); the envelope comes from <see cref="ToolRunner"/>, so the tool never throws.
/// </summary>
[McpServerToolType]
public sealed class UpdatePageContentTool(ToolRunner runner, UpdatePageContentUseCase useCase)
{
    public const string Name = "update_page_content";

    [McpServerTool(
        Name = Name,
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope))]
    [McpMeta(ToolMetaKeys.MaxResultSizeChars, ToolMetaKeys.MaxResultSizeCharsValue)]
    [Description(
        "Writes to OneNote: changes a page with OneNote 2013 page XML. Call it only when the user asks to change a page. "
        + "Read the page first with get_page_content and pass its meta.basis unchanged as basis: a page changed since that read fails with StaleWrite (re-read, then redo the change). Without basis the write runs with the warning noBasis. "
        + "The XML's root is one:Page with the page's ID. OneNote merges it into the page: an Outline sent with its objectID replaces that Outline's whole OE list, so send every OE get_page_content shows under it (nested and table-cell OEs too, with their objectIDs) and name the OEs to delete in removeObjectIds; leaving one out fails with InvalidArgument. "
        + "An Outline or OE sent without an objectID is added. An OE can't move between Outlines: send it without its objectID where it should go and name it for removal where it was. Outlines you don't send are kept. "
        + "The page's notebook needs the user's write grant (a dialog may ask the user to type the notebook's name; only the user can grant, never run the grant command yourself). "
        + "Send images as inline one:Data (base64). A file path in the XML (an InsertedFile, or a path or pathSource) that isn't already on that same element must be a full local path, and the user confirms it in a dialog; otherwise the call fails with WriteNotGranted, or InvalidArgument for a network, device or relative path. No DOCTYPE (InvalidXml). For now one:HTMLBlock fails with InvalidArgument, and so does text whose HTML uses a tag or attribute beyond OneNote's own formatting (a, b, br, cite, code, del, em, font, i, ins, p, s, small, span, strike, strong, sub, sup, u; style, lang, dir, href on a, face/size/color on font), a comment, a link that isn't an absolute http, https or mailto URL, an in-page onenote:# link or an onenote: link to a OneDrive or SharePoint notebook, or a CSS function in style other than rgb()/hsl(). "
        + "In a password-protected section you have no read grant for, the basis may carry only lastModifiedTime and the XML only new content (no objectIDs, no removeObjectIds), else ReadNotGranted; the result then has no meta.basis and a readBackWithheld warning. "
        + "Every write first saves the page to the recovery journal. Afterwards the page is re-read: readBackDiffers names an attribute OneNote didn't apply or an OE it re-created under a new objectID, imageLost an image gone from a sent Outline, readBackFailed a re-read that failed. "
        + "meta.basis is the page's new basis for the next write. If the call fails with outcomeUnknown: true, re-read the page before trying again. "
        + "Success: {ok:true, meta:{correlationId, basis?}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> UpdatePageContent(
        [Description("OneNote ID of the page to change.")] string pageId,
        [Description("The change as OneNote 2013 page XML: a one:Page root whose ID is pageId, holding the Outlines (and title) to change or add.")] string xml,
        [Description("meta.basis from the get_page_content read the change is based on, unchanged.")] PageBasisArgument? basis = null,
        [Description("objectIDs of OEs to delete: each must be inside an Outline this call sends, and is left out of it. Its nested OEs go with it.")] string[]? removeObjectIds = null,
        CancellationToken cancellationToken = default,
        McpServer? server = null) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, pageId ?? string.Empty, xml ?? string.Empty, basis?.ToCore(), removeObjectIds, ct),
            cancellationToken,
            ElicitationGrantPrompt.For(server));
}
