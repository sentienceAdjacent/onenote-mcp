using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server.Grants;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// Story 5.4's <c>UpdateHierarchy</c> reorder (Phill, 2026-11-13, 5.4 plan Q1). Thin (AD-5): the policy is in
/// <see cref="UpdateHierarchyUseCase"/> and the write pipeline (AD-7); the envelope comes from <see cref="ToolRunner"/>,
/// so no tool throws.
/// </summary>
[McpServerToolType]
public sealed class ReorderHierarchyChildrenTool(ToolRunner runner, UpdateHierarchyUseCase useCase)
{
    public const string Name = "reorder_hierarchy_children";

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
        "Writes to OneNote: puts the children of a notebook, section group or section in a new order: its sections and section groups, or a section's pages. Call it only when the user asks to reorder or sort them. The list of open notebooks can't be reordered. "
        + "Read the parent first with get_hierarchy (scope children) and pass childIds: the ID of every child it shows, each exactly once, in the new order. Leave out the recycle bin. A missing, extra or repeated ID, or the ID of something under another parent, fails with InvalidArgument: nothing moves to another parent here. Sections come before section groups. "
        + "Pages keep their subpage level, and a page moves with its subpages: every subpage must stay after its own parent page, with only that parent's subpages between them, else InvalidArgument. "
        + "Pass the parent's lastModifiedTime from that read as basis: {lastModifiedTime}; a parent changed since fails with StaleWrite. The check is weak (a parent's date doesn't always move when its children change), and straight after another hierarchy write a date can move a second late and give a spurious StaleWrite (re-read, then retry). Without basis the write runs with the warning noBasis. "
        + "The notebook needs the user's write grant (a dialog may ask the user to type the notebook's name; only the user can grant, never run the grant command yourself); in a password-protected section that is enough, and without its read grant messages give counts instead of page IDs. "
        + "Refused before anything is written: the recycle bin or anything in it (ObjectNotFound), a page or anything else that isn't a notebook, section group or section as the parent (InvalidArgument), a locked section (SectionLocked: the user unlocks it in OneNote). "
        + "The same order changes nothing: ok with the warning unchanged. Otherwise the parent's hierarchy is first saved to the recovery journal (meta.journalEntryId; restore_from_journal can't restore it, the entry is for the user), and afterwards it is re-read: readBackDiffers means OneNote kept another order or subpage level, readBackFailed that the re-read failed. "
        + "If the call fails with outcomeUnknown: true, re-read the parent before trying again. "
        + "Success: {ok:true, meta:{correlationId, objectId, journalEntryId?}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> ReorderHierarchyChildren(
        [Description("OneNote ID of the notebook, section group or section whose children to reorder.")] string parentId,
        [Description("The ID of every live child of parentId, each once, in the new order.")] string[] childIds,
        [Description("{lastModifiedTime} of the parent, unchanged from its get_hierarchy read (the lastModifiedTime attribute of its element). Nothing else.")] PageBasisArgument? basis = null,
        CancellationToken cancellationToken = default,
        McpServer? server = null) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ReorderAsync(context, parentId ?? string.Empty, childIds, basis?.ToCore(), ct),
            cancellationToken,
            ElicitationGrantPrompt.For(server));
}
