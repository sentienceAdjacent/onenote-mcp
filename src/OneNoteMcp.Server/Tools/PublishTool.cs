using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// <c>publish</c> (stories 3.3 and 3.13): <c>Publish</c> of a page or a section, in eight formats, to a new file under an export root (AD-7 <c>ExternalWrite</c>,
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
        "Writes a file on this PC: exports one OneNote page or one section. Call it only when the user asks for an export. OneNote itself is not changed. "
        + "format and the extension path must end in: pdf (.pdf), xps (.xps), docx (.docx, Word), mhtml (.mht or .mhtml, a single-file web page), html (.htm or .html; its images may go into a folder named <file name>_files beside it), "
        + "emf (.emf; a page only: a section's EMF holds only its first page, so a section as emf fails with NotSupported; export each page as EMF, or the section as PDF), one (.one, OneNote's section file) or onepkg (.onepkg, a OneNote package). "
        + "Files go only under the export roots: the folders in exportRoots in the user's settings.json, by default Documents\\OneNote Exports (created on first use). Only the user can change them. "
        + "path is a file name such as \"Meeting notes.pdf\" or \"Project\\Meeting notes.docx\", resolved under the first export root, or a full local path inside one. "
        + "A path outside every export root fails with ExportPathNotAllowed; a network, device or malformed path, or one whose extension doesn't fit format, with InvalidArgument. "
        + "Nothing is ever overwritten: an existing file at the path (for html, also an existing <file name>_files folder) fails with FileExists, so choose a new name. "
        + "Pages and sections only: a section group fails with NotSupported (export its sections one by one), and so does a notebook (notebooks export as a package in a later release). A locked section, or a page in one, fails with SectionLocked. "
        + "A password-protected section, or a page in one, needs the user's read grant for that section, held when the call starts; without one it fails with ReadNotGranted (read a page of it with get_page_content first, which asks the user). "
        + "Such an export succeeds with the warning exportNotEncrypted: the file is not password-protected, so tell the user. "
        + "On success, tell the user where the file is: meta.outputPath. If the call fails with outcomeUnknown: true (for example Timeout), the file may still have been written: its path is in the message; check it before trying again under a new name. "
        + "Success: {ok:true, meta:{correlationId, objectId, outputPath}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> Publish(
        [Description("OneNote ID of the page or section to export.")] string objectId,
        [Description("The new file: a name ending in the format's extension, relative to the export root (for example \"Meeting notes.pdf\" or \"Project\\Meeting notes.docx\"), or a full local path inside an export root. It must not exist yet.")] string path,
        [Description("The file format: pdf (the default), xps, docx, mhtml, html, emf, one or onepkg.")] PublishFormatArgument format = PublishFormatArgument.Pdf,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, objectId ?? string.Empty, path ?? string.Empty, format.ToCore(), ct),
            cancellationToken);
}
