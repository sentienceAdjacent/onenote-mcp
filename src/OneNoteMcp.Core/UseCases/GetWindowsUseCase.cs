using System.Globalization;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.AppState;
using OneNoteMcp.Core.Results;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>get_windows</c> (story 4.6; spine AD-7 <c>AppState</c>, AD-21): OneNote's open windows, in <c>meta.windows</c>, through
/// the <see cref="AppStatePipeline"/>: <c>ComBusy</c> at the start while an abandoned write is outstanding, no lock, and the
/// 120 s deadline. Reading the windows changes nothing (spike 4.12 W2), so the operation's <c>ChangesState</c> is false: it is
/// never <c>outcomeUnknown</c>, and its <c>Timeout</c>, <c>ComUnavailable</c> and <c>Cancelled</c> are retryable, as a read's
/// (AD-9). It isn't user-visible (AD-11), but the skill calls it only when the user asks or refers to what they're looking at.
/// <para>
/// Each window reports its handle, whether it is current, active, in full-page view or a quick note, its dock location and
/// the notebook, section group, section and page it shows; an empty ID is left out, never reported (W5). No window open: an
/// empty list (W6). A window on a page whose section is protected, with no read grant held at the call's start (a stored
/// grant or a live <c>once</c> window; <see cref="ReadAccess.HoldsReadGrant"/>, never prompted), reports the section and
/// leaves out the page's ID, with one <c>readNotGranted</c> warning giving the count (AD-21; 4.6 plan Q2). That check reads
/// each such section once with <c>GetHierarchy</c> at <c>self</c>; a page whose section can't be read or told apart is
/// left out the same way (fail closed).
/// </para>
/// </summary>
public sealed class GetWindowsUseCase(IOneNoteGateway gateway, AppStatePipeline pipeline, ReadAccess access) : IUseCase
{
    public OperationClass Class => OperationClass.AppState;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(access);

        // AD-21: the grant must be held when the call starts.
        var callStart = access.Now();
        Listed listed;
        try
        {
            listed = await pipeline.RunAsync(new ListWindows(gateway, access, callStart), ct).ConfigureAwait(false);
        }
        catch (OneNoteException ex) when (RetryableAsARead(ex))
        {
            // AD-9: get_windows' calls are reads, so these are retryable, unlike every other AppState call's.
            throw new OneNoteException(ex.Code, ex.Message, ex.ComHResult, ex, outcomeUnknown: false, ex.UserAction, retryable: true);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            throw new OneNoteException(ErrorCode.Cancelled, "The call was cancelled.", innerException: ex, retryable: true);
        }

        IReadOnlyList<ToolWarning> warnings = listed.Withheld == 0
            ? []
            : [new ToolWarning(ToolWarnings.ReadNotGranted, WithheldMessage(listed.Withheld), ReadAccess.WithheldUserAction)];
        return new ToolSuccess(null, new ToolMeta(context.CorrelationId, Windows: listed.Windows), warnings);
    }

    /// <summary>The <c>readNotGranted</c> warning's message: the count only, never an ID or a name (backlog 15).</summary>
    public static string WithheldMessage(int count) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{count} window(s) show a page whose ID was left out: its section is protected, and reading it needs the user's permission, or its section couldn't be checked.");

    /// <summary>OneNote's <c>DockLocation</c> as <c>meta.windows[].dockedLocation</c> shows it.</summary>
    public static string DockedLocationName(int value) => value switch
    {
        -1 => "default",
        0 => "none",
        1 => "left",
        2 => "right",
        3 => "top",
        4 => "bottom",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    private static bool RetryableAsARead(OneNoteException ex) =>
        ex.Code is ErrorCode.Timeout or ErrorCode.ComUnavailable or ErrorCode.Cancelled && !ex.OutcomeUnknown && ex.Retryable is null;

    private sealed record Listed(IReadOnlyList<WindowListing> Windows, int Withheld);

    private sealed class ListWindows(IOneNoteGateway gateway, ReadAccess access, DateTimeOffset callStart) : AppStateOperation<Listed>
    {
        public override bool ChangesState => false;

        public override void Validate()
        {
        }

        public override async Task<Listed> CallAsync(CancellationToken ct)
        {
            var windows = await gateway.GetWindowsAsync(ct).ConfigureAwait(false);
            var pages = new WindowPages(gateway, access, callStart);
            var listed = new List<WindowListing>(windows.Count);
            var withheld = 0;
            foreach (var window in windows)
            {
                var (listing, left) = await pages.ListAsync(window, ct).ConfigureAwait(false);
                listed.Add(listing);
                withheld += left ? 1 : 0;
            }

            return new Listed(listed, withheld);
        }
    }
}
