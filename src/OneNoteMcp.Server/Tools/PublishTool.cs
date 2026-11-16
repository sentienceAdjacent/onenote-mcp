using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// <c>publish</c> (story 3.3): <c>Publish</c> of a page as PDF to a new file under an export root (AD-7 <c>ExternalWrite</c>,
/// AD-11). Thin (AD-5): the policy is in <see cref="PublishUseCase"/> and the write pipeline; the envelope comes from
/// <see cref="ToolRunner"/>, so the tool never throws. It raises no dialog of any kind, so it takes no prompt.
/// </summary>
[McpServerToolType]
public sealed class PublishTool(ToolRunner runner, PublishUseCase useCase)
{
    public const string Name = PublishUseCase.OperationName;

    [McpServerTool(
        Name = Name,
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope))]
    [McpMeta(ToolMetaKeys.MaxResultSizeChars, ToolMetaKeys.MaxResultSizeCharsValue)]
    [Description(
        "Writes a file on this PC: exports one OneNote page as a PDF. Call it only when the user asks for an export. OneNote itself is not changed. "
        + "Files go only under the export roots: the folders in exportRoots in the user's settings.json, by default Documents\\OneNote Exports (created on first use). Only the user can change them. "
        + "path is a file name ending in .pdf, such as \"Meeting notes.pdf\" or \"Project\\Meeting notes.pdf\", resolved under the first export root, or a full local path inside one. "
        + "A path outside every export root fails with ExportPathNotAllowed; a network, device or malformed path, or one not ending in .pdf, with InvalidArgument. "
        + "Nothing is ever overwritten: an existing file at the path fails with FileExists, so choose a new name. "
        + "Pages only in this release: a section, section group or notebook fails with NotSupported (export its pages one by one). A page in a locked section fails with SectionLocked. "
        + "A page in a password-protected section needs the user's read grant for that section, held when the call starts; without one it fails with ReadNotGranted (read the page with get_page_content first, which asks the user). "
        + "Such an export succeeds with the warning exportNotEncrypted: the PDF is not password-protected, so tell the user. "
        + "On success, tell the user where the file is: meta.outputPath. If the call fails with outcomeUnknown: true (for example Timeout), the file may still have been written: its path is in the message; check it before trying again under a new name. "
        + "Success: {ok:true, meta:{correlationId, objectId, outputPath}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> Publish(
        [Description("OneNote ID of the page to export.")] string objectId,
        [Description("The new file: a name ending in .pdf relative to the export root (for example \"Meeting notes.pdf\" or \"Project\\Meeting notes.pdf\"), or a full local path inside an export root. It must not exist yet.")] string path,
        [Description("The file format: pdf (the only format in this release, and the default).")] PublishFormatArgument format = PublishFormatArgument.Pdf,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, objectId ?? string.Empty, path ?? string.Empty, format.ToCore(), ct),
            cancellationToken);
}
