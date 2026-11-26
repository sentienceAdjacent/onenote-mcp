using OneNoteMcp.Core.AppState;
using OneNoteMcp.Core.Results;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>NavigateTo</c>: shows a notebook, section or page in OneNote, optionally selecting one object on the page. It is
/// user-visible, since it changes what OneNote shows and may bring OneNote forward, so it is an <c>AppState</c> call (AD-7) that
/// the skill makes only when the user explicitly asks (AD-11). Showing a page makes OneNote download its images that
/// aren't on this PC (story 1.21's probe, 2026-11-07), which is how a user gets a <c>binaryUnavailable</c> image.
/// Since story 4.3 it runs through the <see cref="AppStatePipeline"/>: <c>ComBusy</c> at the start while an abandoned write
/// is outstanding, the 120 s deadline, and <c>outcomeUnknown</c> on an ambiguous step-7 failure. An empty
/// <c>hierarchyId</c> is <see cref="ErrorCode.InvalidArgument"/> before COM.
/// Story 4.6: with a <c>window</c> handle, only that window moves (<c>Window.NavigateTo</c>); without one, OneNote's first
/// window, as before.
/// Returns no content: <c>ok: true</c> and no body. Errors map as for every other call.
/// </summary>
public sealed class NavigateToUseCase(IOneNoteGateway gateway, AppStatePipeline pipeline) : IUseCase
{
    /// <summary>
    /// The userAction for <c>0x80042014</c>. Spike 4.12 R1 found no failure from a navigate repeated within 2 s, so the
    /// "wait two seconds and try once more" OneMore's Defects page suggested (research note row 14) is gone (AD-9).
    /// </summary>
    public const string ObjectNotFoundUserAction = "Re-read the hierarchy and use a current ID.";

    public OperationClass Class => OperationClass.AppState;

    public Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string hierarchyId, string objectId, CancellationToken ct) =>
        ExecuteAsync(context, hierarchyId, objectId, window: null, ct);

    /// <param name="window">
    /// Story 4.6: a window's handle as <c>get_windows</c> returns it, or null for OneNote's first window. One no open window has
    /// is <see cref="ErrorCode.InvalidArgument"/> from the gateway, and nothing is shown.
    /// </param>
    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string hierarchyId, string objectId, int? window, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pipeline);

        try
        {
            await pipeline.RunAsync(new Navigate(gateway, hierarchyId ?? string.Empty, objectId ?? string.Empty, window), ct).ConfigureAwait(false);
        }
        catch (OneNoteException ex) when (ex.Code == ErrorCode.ObjectNotFound && ex.ComHResult == OneNoteHResults.ObjectDoesNotExist)
        {
            throw new OneNoteException(ex.Code, ex.Message, ex.ComHResult, ex, ex.OutcomeUnknown, ObjectNotFoundUserAction);
        }

        return new ToolSuccess(null, new ToolMeta(context.CorrelationId), []);
    }

    private sealed class Navigate(IOneNoteGateway gateway, string hierarchyId, string objectId, int? window) : AppStateOperation<bool>
    {
        public override void Validate()
        {
            if (hierarchyId.Length == 0)
            {
                throw new OneNoteException(ErrorCode.InvalidArgument, "hierarchyId is empty: pass the ID of a notebook, section group, section or page.");
            }
        }

        public override async Task<bool> CallAsync(CancellationToken ct)
        {
            if (window is { } handle)
            {
                await gateway.NavigateToAsync(hierarchyId, objectId, handle, ct).ConfigureAwait(false);
            }
            else
            {
                await gateway.NavigateToAsync(hierarchyId, objectId, ct).ConfigureAwait(false);
            }

            return true;
        }
    }
}
