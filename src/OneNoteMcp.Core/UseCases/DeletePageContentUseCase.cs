using System.Runtime.Versioning;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Writes;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>DeletePageContent</c>: deletes one Outline from a page through the write pipeline (AD-7) as a <c>PageWrite</c>
/// (<see cref="DeletePageContentWrite"/>), checked against the caller's basis (AD-17): its date and the deleted Outline's
/// hash. Step 1 needs the write grant of the page's notebook (AD-6). In a password-protected section, it also needs a
/// read grant for the section held at the call's start, and never raises the read-grant dialog (Phill, 2026-11-12, 2.10
/// plan Q1). One Outline per call (Q2). The result has no XML: <c>meta.basis</c> comes from the read-back (Q3), left out
/// when the read-back failed. Messages carry IDs only (AD-9).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeletePageContentUseCase(PageWriteServices services, WritePipeline pipeline) : IUseCase
{
    public OperationClass Class => OperationClass.PageWrite;

    public async Task<ToolSuccess> ExecuteAsync(
        ToolCallContext context,
        string pageId,
        string objectId,
        PageWriteBasis? basis,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);

        // An empty objectId names nothing to delete: refused before anything is read or locked.
        if (string.IsNullOrEmpty(objectId))
        {
            throw new OneNoteException(
                ErrorCode.InvalidArgument,
                "The argument objectId is empty, so nothing was read or deleted.",
                userAction: "Pass objectId: the objectID of the one:Outline to delete, from get_page_content.",
                retryable: false);
        }

        // "No read grant" means none that would let a read pass at the call's start (AD-7).
        var write = new DeletePageContentWrite(services, context.CorrelationId, pageId ?? string.Empty, objectId, basis, services.ReadAccess.Now());
        var result = await pipeline.RunAsync(context, write, ct).ConfigureAwait(false);
        return new ToolSuccess(null, new ToolMeta(context.CorrelationId, Basis: result.Basis), result.Warnings);
    }
}
