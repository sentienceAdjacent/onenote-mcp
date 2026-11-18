using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// <c>open_package</c> (story 3.8): unpacks a OneNote package into a new folder under an export root and opens it as a
/// separate notebook (AD-5 promoted task, AD-7 <c>ExternalWrite</c>). Thin (AD-5): the policy is in
/// <see cref="OpenPackageUseCase"/> and the write pipeline; the envelope comes from <see cref="ToolRunner"/>, so the tool
/// never throws. It raises no dialog and gives no grant.
/// </summary>
[McpServerToolType]
public sealed class OpenPackageTool(ToolRunner runner, OpenPackageUseCase useCase)
{
    public const string Name = OpenPackageUseCase.OperationName;

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
        "Opens a OneNote package (.onepkg, for example one publish made of a notebook) in OneNote as a separate copy: it unpacks the package into a new folder on this PC and opens that folder as a notebook. "
        + "Call it only when the user asks to open or import a package. Nothing in the user's own notebooks changes, and the original stays open if it is. "
        + "package is the full local path (C:\\…) of an existing .onepkg file, in any folder; a relative, network or device path, or another kind of file, fails with InvalidArgument. "
        + "destination is the new folder, a name such as \"Opened trip\" (under the first export root) or a full local path inside an export root; leave it out for a new folder named after the package with the date and time. "
        + "A destination outside every export root fails with ExportPathNotAllowed, and one that already exists with FileExists: nothing is opened into an existing folder. "
        + "Every section of the opened copy counts as password-protected, because it lies under an export folder: reading its pages needs the user's read grant (get_page_content asks). The copy gets no write grant. "
        + "Its pages may list a few seconds after it opens; if none is listed within 5 s the result warns pagesNotListed, which is expected when every section is empty, locked or protected. "
        + "On success, tell the user where the copy is (meta.outputPath) and that it is open in OneNote as a separate notebook (meta.objectId), which they close in OneNote when done. "
        + "If a call fails after the folder was made, the message names the folder and says whether the package was unpacked; another try needs a new destination. "
        + "Success: {ok:true, meta:{correlationId, objectId, outputPath}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> OpenPackage(
        [Description("Full local path (C:\\…) of the .onepkg file to open.")] string package,
        [Description("Optional new folder for the copy: a name under the first export root, or a full local path inside an export root. It must not exist yet. Leave it out for a folder named after the package with the date and time.")] string? destination = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, package ?? string.Empty, destination, ct),
            cancellationToken);
}
