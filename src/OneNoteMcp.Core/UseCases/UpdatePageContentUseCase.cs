using System.Runtime.Versioning;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Writes;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>UpdatePageContent</c>: changes a page through the write pipeline (AD-7) as a <c>PageWrite</c>
/// (<see cref="UpdatePageContentWrite"/>), checked against the caller's basis (AD-17). Step 1 needs the write grant of
/// the page's notebook (AD-6); in a password-protected section that grant alone suffices and no read-grant dialog is
/// raised (AD-21). The result has no XML: <c>meta.basis</c> comes from the read-back (Phill, 2026-11-12, 2.9 plan Q2),
/// left out when the read-back failed or the section is protected with no read grant, and the warnings say what was
/// filled, skipped or not applied. Messages carry IDs only (AD-9).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdatePageContentUseCase(PageWriteServices services, WritePipeline pipeline) : IUseCase
{
    public OperationClass Class => OperationClass.PageWrite;

    public async Task<ToolSuccess> ExecuteAsync(
        ToolCallContext context,
        string pageId,
        string xml,
        PageWriteBasis? basis,
        IReadOnlyList<string>? removeObjectIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);

        // "No read grant" means none that would let a read pass at the call's start (AD-7).
        var write = new UpdatePageContentWrite(services, context.CorrelationId, pageId ?? string.Empty, xml ?? string.Empty, basis, removeObjectIds, services.ReadAccess.Now());
        var result = await pipeline.RunAsync(context, write, ct).ConfigureAwait(false);
        return new ToolSuccess(null, new ToolMeta(context.CorrelationId, Basis: result.Basis), result.Warnings);
    }
}
