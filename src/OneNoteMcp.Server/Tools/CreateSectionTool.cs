using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server.Grants;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// Story 5.3's <c>OpenHierarchy</c> creates, one tool per kind (Phill, 2026-11-13, 5.3 plan Q1). Thin (AD-5): the policy is
/// in <see cref="CreateHierarchyUseCase"/> and the write pipeline (AD-7); the envelope comes from <see cref="ToolRunner"/>,
/// so no tool throws.
/// </summary>
[McpServerToolType]
public sealed class CreateSectionTool(ToolRunner runner, CreateHierarchyUseCase useCase)
{
    public const string Name = "create_section";

    [McpServerTool(
        Name = Name,
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope))]
    [McpMeta(ToolMetaKeys.MaxResultSizeChars, ToolMetaKeys.MaxResultSizeCharsValue)]
    [Description(
        "Writes to OneNote: adds an empty section to a notebook or section group and returns its ID in meta.objectId (and the parent's in meta.parentId). Call it only when the user asks for a new section. "
        + "name is the section's name as OneNote shows it (.one is added unless it ends with it): one name, no path separators, no '..', no < > : \" | ? *, no leading or trailing space or dot. "
        + "If a section of that name (ignoring case) is already there, nothing is created: its ID is returned with the warning alreadyExisted. A section group of that name there fails with InvalidArgument. "
        + "The notebook needs the user's write grant. Without one, the user may be shown a dialog asking them to type the notebook's name to grant it; if they don't, or the client can't show it, the call fails with WriteNotGranted, and only the user can grant it (never run the grant command yourself). "
        + "A parent that isn't a notebook or section group, or is in the recycle bin, fails with ObjectNotFound. "
        + "If the call fails with outcomeUnknown: true, the section may or may not exist: re-read the hierarchy before trying again. "
        + "Success: {ok:true, meta:{correlationId, parentId, objectId}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> CreateSection(
        [Description("OneNote ID of the notebook or section group to add the section to.")] string parentId,
        [Description("The new section's name, such as Ideas.")] string name,
        CancellationToken cancellationToken = default,
        McpServer? server = null) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.CreateSectionAsync(context, parentId ?? string.Empty, name ?? string.Empty, ct),
            cancellationToken,
            ElicitationGrantPrompt.For(server));
}

