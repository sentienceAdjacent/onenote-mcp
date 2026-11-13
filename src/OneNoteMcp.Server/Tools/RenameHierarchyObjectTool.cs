using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server.Grants;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// Story 5.4's <c>UpdateHierarchy</c> rename (Phill, 2026-11-13, 5.4 plan Q1). Thin (AD-5): the policy is in
/// <see cref="UpdateHierarchyUseCase"/> and the write pipeline (AD-7); the envelope comes from <see cref="ToolRunner"/>,
/// so no tool throws.
/// </summary>
[McpServerToolType]
public sealed class RenameHierarchyObjectTool(ToolRunner runner, UpdateHierarchyUseCase useCase)
{
    public const string Name = "rename_hierarchy_object";

    [McpServerTool(
        Name = Name,
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope))]
    [McpMeta(ToolMetaKeys.MaxResultSizeChars, ToolMetaKeys.MaxResultSizeCharsValue)]
    [Description(
        "Writes to OneNote: renames a section, section group or notebook (for a notebook, the name OneNote shows, its nickname; its folder keeps its name). Call it only when the user asks to rename one. A page's name is its title: change that with update_page_content. "
        + "name is the new name as OneNote shows it (a section's trailing .one is dropped): one name, no path separators, no '..', no < > : \" | ? *, no control, invisible or text-direction characters, no leading or trailing space or dot, at most 128 characters, not a reserved name such as CON. A name that another section or section group under the same parent already uses (ignoring case, either kind), or that another open notebook uses, fails with InvalidArgument; changing only the case of the node's own name is fine. "
        + "Read the node first with get_hierarchy (scope self) and pass its lastModifiedTime as basis: {lastModifiedTime}; a node changed since fails with StaleWrite. The check is weak: a rename doesn't change the renamed node's own date, and straight after another hierarchy write a node's date can move a second late and give a spurious StaleWrite (re-read, then retry). Without basis the write runs with the warning noBasis. "
        + "The notebook needs the user's write grant (a dialog may ask the user to type the notebook's name; only the user can grant, never run the grant command yourself); a password-protected section needs only that. Renaming a protected section, or a section group in a local notebook that holds one, ends that section's read grant: the user is asked again on the next read. "
        + "Refused before anything is written: the recycle bin or anything in it (ObjectNotFound), a locked section (SectionLocked: the user unlocks it in OneNote), and a section group in a notebook stored on OneDrive (NotSupported: the user renames it in OneNote). "
        + "The same exact name changes nothing: ok with the warning unchanged. Otherwise the node's hierarchy is first saved to the recovery journal (meta.journalEntryId; restore_from_journal can't restore it, the entry is for the user), and afterwards it is re-read: readBackDiffers means OneNote kept another name, readBackFailed that the re-read failed. "
        + "If the call fails with outcomeUnknown: true, re-read the node before trying again. "
        + "Success: {ok:true, meta:{correlationId, objectId, journalEntryId?}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> RenameHierarchyObject(
        [Description("OneNote ID of the section, section group or notebook to rename.")] string objectId,
        [Description("The new name as OneNote shows it, such as Plans.")] string name,
        [Description("{lastModifiedTime} of the node, unchanged from its get_hierarchy read (the lastModifiedTime attribute of its element). Nothing else.")] PageBasisArgument? basis = null,
        CancellationToken cancellationToken = default,
        McpServer? server = null) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.RenameAsync(context, objectId ?? string.Empty, name ?? string.Empty, basis?.ToCore(), ct),
            cancellationToken,
            ElicitationGrantPrompt.For(server));
}
