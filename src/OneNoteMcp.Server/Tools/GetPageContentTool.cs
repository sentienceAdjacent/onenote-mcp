using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Tools;

/// <summary><c>GetPageContent</c>. Thin (AD-5): the policy is in <see cref="GetPageContentUseCase"/>.</summary>
[McpServerToolType]
public sealed class GetPageContentTool(ToolRunner runner, GetPageContentUseCase useCase)
{
    public const string Name = "get_page_content";

    [McpServerTool(
        Name = Name,
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope))]
    [McpMeta(ToolMetaKeys.MaxResultSizeChars, ToolMetaKeys.MaxResultSizeCharsValue)]
    [Description(
        "Read-only. Returns a page's XML in the OneNote 2013 schema, unchanged. Binary content (image and attachment data) is left out unless includeBinary is true. "
        + "meta.basis {lastModifiedTime, pageHash, elementHashes} records the version of the page that was read, and is the same whatever includeBinary is; pass it back unchanged on a later write to this page, so a write against a page that changed since is refused. "
        + "With includeBinary, images whose binary isn't on this PC (a notebook stored online that hasn't downloaded them) are missing from the XML: their count is in meta.binaryUnavailable, with a warning with code binaryUnavailable. "
        + "Image binaries can also be read one at a time with get_binary_page_content and the callbackID from the image's one:CallbackID element, which appears only in the default XML (includeBinary false); with includeBinary the binary is inline in one:Data instead. "
        + ToolMetaKeys.LargeResultNote
        + "Success: {ok:true, xml, meta:{correlationId, basis, binaryUnavailable?}, warnings:[{code, message, userAction?}]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> GetPageContent(
        [Description("OneNote ID of the page.")] string pageId,
        [Description("True to include binary content (base64 one:Data) in the XML. Defaults to false.")] bool includeBinary = false,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, pageId ?? string.Empty, includeBinary, ct),
            cancellationToken);
}
