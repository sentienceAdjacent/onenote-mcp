using System.Globalization;
using System.Runtime.Versioning;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Writes;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>merge_sections</c> (story 3.6; AD-5, AD-7 Merges, AD-14): one section merged into another through the write pipeline
/// as one <c>HierarchyWrite</c> (<see cref="MergeSectionsWrite"/>), whose COM call is one <c>MergeSections</c>. OneNote
/// leaves the source and its pages untouched and puts copies under new IDs at the end of the destination, subpages kept
/// (spike 3.1 Implication 4). The result is <c>meta.objectId</c> (the source, untouched), <c>meta.parentId</c> (the
/// destination), <c>meta.journalEntryId</c> (the destination's <c>hierarchy</c> entry) and <c>meta.pageIds</c> (the
/// copies' new IDs in destination order; left out for a password-protected destination, 3.6 Q6, and when the read-back
/// failed), with no <c>meta.basis</c>. Messages carry IDs and counts only (AD-9).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MergeSectionsUseCase(HierarchyWriteServices services, WritePipeline pipeline, Func<TimeSpan, CancellationToken, Task>? settle = null) : IUseCase
{
    public OperationClass Class => OperationClass.HierarchyWrite;

    /// <summary>Merges the section <paramref name="sourceId"/> into the section <paramref name="destinationId"/>.</summary>
    public async Task<ToolSuccess> MergeAsync(ToolCallContext context, string sourceId, string destinationId, PageWriteBasis? basis, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(pipeline);
        var write = new MergeSectionsWrite(services, context.CorrelationId, sourceId ?? string.Empty, destinationId ?? string.Empty, basis, settle ?? DelayAsync);
        var result = await pipeline.RunAsync(context, write, ct).ConfigureAwait(false);
        return new ToolSuccess(
            null,
            new ToolMeta(
                context.CorrelationId,
                ParentId: write.DestinationId,
                ObjectId: write.SourceId,
                JournalEntryId: write.JournalEntryId?.ToString("D", CultureInfo.InvariantCulture),
                PageIds: result.PageIds),
            result.Warnings);
    }

    private static Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}
