using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Tools;

/// <summary><c>GetBinaryPageContent</c>. Thin (AD-5): the policy is in <see cref="GetBinaryPageContentUseCase"/>.</summary>
[McpServerToolType]
public sealed class GetBinaryPageContentTool(ToolRunner runner, GetBinaryPageContentUseCase useCase)
{
    public const string Name = "get_binary_page_content";

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
        "Read-only. Returns one binary object of a page, such as an image, base64-encoded in meta.binary exactly as OneNote returns it. "
        + "Take the callbackID from the object's one:CallbackID element in get_page_content's default XML (includeBinary false); the includeBinary XML has the binary inline in one:Data and no one:CallbackID. "
        + "When OneNote has no binary for it, the result is still ok, with no meta.binary and a warning with code binaryUnavailable: either the binary isn't stored on this PC (an image in a notebook stored online that hasn't been downloaded) or the callbackID is wrong, since OneNote reports both the same way. "
        + "A File Printout's one:XPSFile callbackID also gives binaryUnavailable: the printout document can't be read through the OneNote API. "
        + "This tool never fetches a missing image itself; navigate_to, called only when the user asks, shows the page so OneNote downloads it. "
        + ToolMetaKeys.LargeResultNote
        + "Success: {ok:true, meta:{correlationId, binary?}, warnings:[{code, message, userAction?}]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> GetBinaryPageContent(
        [Description("OneNote ID of the page.")] string pageId,
        [Description("The callbackID of the binary object, from its one:CallbackID element in get_page_content's default XML (includeBinary false).")] string callbackId,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, pageId ?? string.Empty, callbackId ?? string.Empty, ct),
            cancellationToken);
}
