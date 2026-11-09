using OneNoteMcp.Core.Results;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>NavigateTo</c>: shows a notebook, section or page in OneNote, optionally selecting one object on the page. It is
/// user-visible, since it takes keyboard focus and changes what OneNote shows, so it is an <c>AppState</c> call (AD-7) that
/// the skill makes only when the user explicitly asks (AD-11). Showing a page makes OneNote download its images that
/// aren't on this PC (story 1.21's probe, 2026-11-07), which is how a user gets a <c>binaryUnavailable</c> image.
/// Returns no content: <c>ok: true</c> and no body. Errors map as for every other call.
/// </summary>
public sealed class NavigateToUseCase(IOneNoteGateway gateway) : IUseCase
{
    /// <summary>
    /// The userAction for <c>0x80042014</c>. OneMore's Defects page (research note row 14) reports a navigate repeated
    /// within about 2 s failing with this code; applied to <c>NavigateTo</c> by inference, not measured here.
    /// </summary>
    public const string ObjectNotFoundUserAction =
        "If navigate_to was just called, wait two seconds and try once more. Otherwise re-read the hierarchy and use a current ID.";

    public OperationClass Class => OperationClass.AppState;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string hierarchyId, string objectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await gateway.NavigateToAsync(hierarchyId ?? string.Empty, objectId ?? string.Empty, ct).ConfigureAwait(false);
        }
        catch (OneNoteException ex) when (ex.Code == ErrorCode.ObjectNotFound && ex.ComHResult == OneNoteHResults.ObjectDoesNotExist)
        {
            throw new OneNoteException(ex.Code, ex.Message, ex.ComHResult, ex, ex.OutcomeUnknown, ObjectNotFoundUserAction);
        }

        return new ToolSuccess(null, new ToolMeta(context.CorrelationId), []);
    }
}
