using System.Globalization;
using System.Runtime.Versioning;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Writes;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>set_page_level</c> (story 3.14; E10): a page's subpage level set to an absolute 1 to 3, its own subpages moving with it,
/// through the write pipeline as one <c>HierarchyWrite</c> (<see cref="SetPageLevelWrite"/>). Core builds the
/// <c>UpdateHierarchy</c> XML from IDs and levels; nothing a caller writes reaches OneNote. The result is
/// <c>meta.objectId</c> (the page), <c>meta.parentId</c> (its section) and <c>meta.journalEntryId</c> (the section's
/// <c>hierarchy</c> entry; absent when nothing changed), with no <c>meta.basis</c>. Messages carry IDs, counts and levels
/// only (AD-9).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SetPageLevelUseCase(HierarchyWriteServices services, WritePipeline pipeline) : IUseCase
{
    public OperationClass Class => OperationClass.HierarchyWrite;

    /// <summary>Sets page <paramref name="pageId"/> to <paramref name="level"/>, moving its subpages by the same amount.</summary>
    public async Task<ToolSuccess> SetLevelAsync(ToolCallContext context, string pageId, int level, PageWriteBasis? basis, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(pipeline);
        var write = new SetPageLevelWrite(services, context.CorrelationId, pageId ?? string.Empty, level, basis);
        var result = await pipeline.RunAsync(context, write, ct).ConfigureAwait(false);
        return new ToolSuccess(
            null,
            new ToolMeta(
                context.CorrelationId,
                ParentId: write.SectionId,
                ObjectId: write.PageId,
                JournalEntryId: write.JournalEntryId?.ToString("D", CultureInfo.InvariantCulture)),
            result.Warnings);
    }
}
