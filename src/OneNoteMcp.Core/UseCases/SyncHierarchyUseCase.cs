using OneNoteMcp.Core.AppState;
using OneNoteMcp.Core.Results;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>sync_hierarchy</c> (story 4.3; AD-7 <c>AppState</c>): asks OneNote to sync a notebook, section group, section or page,
/// through the <see cref="AppStatePipeline"/>. It isn't user-visible (spike 4.1 S1: no focus or view change), but the skill
/// calls it only when the user asks, never on its own after a write (AD-11). No pre-read: any hierarchy ID goes to
/// <c>SyncHierarchy</c>, and an unknown or closed notebook's old ID comes back as <c>0x80042014</c>,
/// <see cref="ErrorCode.ObjectNotFound"/> (spike 4.1 S2). An empty ID is <see cref="ErrorCode.InvalidArgument"/> before COM.
/// The result has no body; every <c>ok</c> carries the warning <see cref="ToolWarnings.SyncRequested"/>, since OneNote
/// accepting the request says nothing about when or whether the sync finishes (4.3 plan Q1).
/// </summary>
public sealed class SyncHierarchyUseCase(IOneNoteGateway gateway, AppStatePipeline pipeline) : IUseCase
{
    /// <summary>The warning's message.</summary>
    public const string RequestedMessage =
        "OneNote accepted the sync request. It returns before any sync is done and doesn't say when or whether the sync finishes.";

    public OperationClass Class => OperationClass.AppState;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string hierarchyId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pipeline);

        await pipeline.RunAsync(new Sync(gateway, hierarchyId ?? string.Empty), ct).ConfigureAwait(false);
        return new ToolSuccess(null, new ToolMeta(context.CorrelationId), [new ToolWarning(ToolWarnings.SyncRequested, RequestedMessage)]);
    }

    private sealed class Sync(IOneNoteGateway gateway, string hierarchyId) : AppStateOperation<bool>
    {
        public override void Validate() => AppStateChecks.RequireHierarchyId(hierarchyId);

        public override async Task<bool> CallAsync(CancellationToken ct)
        {
            await gateway.SyncHierarchyAsync(hierarchyId, ct).ConfigureAwait(false);
            return true;
        }
    }
}
