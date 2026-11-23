using System.Globalization;
using OneNoteMcp.Core.AppState;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// One <c>quick_filing</c> log line (story 4.9; AD-13): the outcome, the chosen ID, timings and the lengths of the title and
/// description, never their text. <see cref="Outcome"/> is <c>picked</c>, <c>cancelled</c> (Cancel or Esc), <c>leftOpen</c>
/// (the 300 s wait ended), <c>clientCancelled</c> (the client cancelled during the wait), <c>busy</c> (another call is
/// waiting), <c>lateAnswer</c> (an answer that arrived after its call's wait ended, dropped), or an error code.
/// <see cref="RunMs"/> is the pipeline's time (step 7's <c>Run</c> included), <see cref="WaitMs"/> the wait for the user.
/// </summary>
public sealed record QuickFilingLogEntry(
    string CorrelationId,
    string Outcome,
    int TitleLength,
    int DescriptionLength,
    string? ChosenId = null,
    long? RunMs = null,
    long? WaitMs = null,
    string? PressedButton = null);

/// <summary>
/// <c>quick_filing</c> (story 4.9; AD-7 <c>AppState</c> <c>quick_filing</c>, epic 4 A8): shows OneNote's QuickFiling picker so the
/// user can point at a section, and returns their choice. User-visible (AD-11). Two phases:
/// <list type="number">
/// <item>The <see cref="AppStatePipeline"/>, with no lock, grant, basis or snapshot: step 1 checks the title (at most
/// <see cref="MaxTitleLength"/> characters) and the description (at most <see cref="MaxDescriptionLength"/>), refusing
/// control characters, with <see cref="ErrorCode.InvalidArgument"/>; step 7 is the gateway's one STA call ending in
/// <c>Run</c>, under the 120 s deadline. A step-7 <see cref="ErrorCode.Timeout"/>, <see cref="ErrorCode.ComUnavailable"/>,
/// <see cref="ErrorCode.Cancelled"/> or <c>0x800706BE</c> is <c>outcomeUnknown</c>: a picker may be on screen.</item>
/// <item>The wait for the user, outside the deadline: a fixed <see cref="DefaultWaitLimit"/> (a test may override it) and the
/// client's cancel. The picker is always released when the wait ends. A pick reads the section once
/// (<c>GetHierarchy(id, self)</c>) and returns its ID in <c>meta.objectId</c> and its <c>one:Section</c> element as
/// <c>xml</c>, or the ID only when that read fails (4.9 plan OQ1 a). Cancel or Esc is ok with
/// <see cref="ToolWarnings.PickerCancelled"/>; the limit is ok with <see cref="ToolWarnings.PickerLeftOpen"/>; a client cancel
/// is <see cref="ErrorCode.Cancelled"/>, retryable and never <c>outcomeUnknown</c>, since <c>Run</c> had already returned.
/// An answer that arrives after the wait ended is dropped and logged.</item>
/// </list>
/// One waiting call per process (AD-7): while one call holds the slot, from its start until its wait ends, another is
/// <see cref="ErrorCode.ComBusy"/>, retryable, before COM, even though OneNote would show two pickers (spike 4.13 F4).
/// </summary>
public sealed class QuickFilingUseCase : IUseCase
{
    /// <summary>The fixed wait for the user (AD-7 <c>AppState</c>): 300 s, with no setting.</summary>
    public static readonly TimeSpan DefaultWaitLimit = TimeSpan.FromSeconds(300);

    /// <summary>The picker's title when the caller gives none.</summary>
    public const string DefaultTitle = "Choose a section";

    /// <summary>The longest title accepted (ours, to keep a runaway string off the user's screen; OneNote's own limit is unprobed).</summary>
    public const int MaxTitleLength = 255;

    /// <summary>The longest description accepted (as <see cref="MaxTitleLength"/>).</summary>
    public const int MaxDescriptionLength = 1024;

    /// <summary>The <see cref="ToolWarnings.PickerCancelled"/> message.</summary>
    public const string CancelledMessage = "The user closed the picker without choosing a section (Cancel or Esc). Nothing was chosen.";

    /// <summary>The <see cref="ToolWarnings.PickerLeftOpen"/> userAction.</summary>
    public const string LeftOpenUserAction =
        "Ask the user to close the picker in OneNote (Cancel) if it is still open, then call quick_filing again if they still want to choose a section.";

    /// <summary>The ComBusy userAction while another call waits.</summary>
    public const string BusyUserAction =
        "Ask the user to answer or cancel the picker that is already open, then retry; the earlier call stops waiting after 300 s on its own.";

    private readonly IOneNoteGateway gateway;
    private readonly AppStatePipeline pipeline;
    private readonly TimeProvider clock;
    private readonly Action<QuickFilingLogEntry>? log;

    // 1 while a call holds the one waiting slot (AD-7 AppState quick_filing).
    private int waiting;

    /// <param name="gateway">The gateway that shows the picker and reads the chosen section.</param>
    /// <param name="pipeline">The <c>AppState</c> pipeline.</param>
    /// <param name="clock">The clock the wait runs on.</param>
    /// <param name="waitLimit">The wait for the user, a test's override; when null, <see cref="DefaultWaitLimit"/>.</param>
    /// <param name="log">Receives one entry per call, and one per late answer (AD-13).</param>
    public QuickFilingUseCase(IOneNoteGateway gateway, AppStatePipeline pipeline, TimeProvider? clock = null, TimeSpan? waitLimit = null, Action<QuickFilingLogEntry>? log = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(pipeline);
        this.gateway = gateway;
        this.pipeline = pipeline;
        this.clock = clock ?? TimeProvider.System;
        WaitLimit = waitLimit ?? DefaultWaitLimit;
        this.log = log;
    }

    /// <summary>The wait for the user every call runs under.</summary>
    public TimeSpan WaitLimit { get; }

    /// <summary>The <see cref="ToolWarnings.PickerLeftOpen"/> message.</summary>
    public string LeftOpenMessage => string.Create(
        CultureInfo.InvariantCulture,
        $"The user didn't choose a section within {WaitLimit.TotalSeconds:0.###} s, so nothing was chosen. The picker may still be on screen, and a choice made in it now is ignored.");

    public OperationClass Class => OperationClass.AppState;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string? title, string? description, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var shownTitle = string.IsNullOrWhiteSpace(title) ? DefaultTitle : title;
        var shownDescription = description ?? string.Empty;
        void Log(string outcome, string? chosen = null, long? runMs = null, long? waitMs = null, uint? pressed = null) =>
            log?.Invoke(new QuickFilingLogEntry(
                context.CorrelationId,
                outcome,
                shownTitle.Length,
                shownDescription.Length,
                chosen,
                runMs,
                waitMs,
                pressed is { } p ? "0x" + p.ToString("X8", CultureInfo.InvariantCulture) : null));

        // One waiting call per process, before COM (AD-7 AppState quick_filing).
        if (Interlocked.CompareExchange(ref waiting, 1, 0) != 0)
        {
            Log("busy");
            throw new OneNoteException(
                ErrorCode.ComBusy,
                "Another quick_filing call is still waiting for the user to answer its picker, so nothing was shown.",
                userAction: BusyUserAction,
                retryable: true);
        }

        try
        {
            // Phase 1: the pipeline, step 7 ending at Run.
            var start = clock.GetTimestamp();
            QuickFilingPicker picker;
            try
            {
                picker = await pipeline.RunAsync(new Show(gateway, shownTitle, shownDescription), ct, context.CorrelationId).ConfigureAwait(false);
            }
            catch (OneNoteException ex)
            {
                Log(ex.Code.ToString(), runMs: Ms(start));
                throw;
            }
            catch (OperationCanceledException)
            {
                Log(nameof(ErrorCode.Cancelled), runMs: Ms(start));
                throw;
            }

            var runMs = Ms(start);

            // Phase 2: the wait for the user, outside the deadline; the picker is released however it ends.
            var waitStart = clock.GetTimestamp();
            QuickFilingAnswer answer;
            try
            {
                answer = await picker.Answer.WaitAsync(WaitLimit, clock, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                WatchLateAnswer(picker, context.CorrelationId, shownTitle.Length, shownDescription.Length);
                Log("leftOpen", runMs: runMs, waitMs: Ms(waitStart));
                return new ToolSuccess(null, new ToolMeta(context.CorrelationId), [new ToolWarning(ToolWarnings.PickerLeftOpen, LeftOpenMessage, LeftOpenUserAction)]);
            }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
            {
                WatchLateAnswer(picker, context.CorrelationId, shownTitle.Length, shownDescription.Length);
                Log("clientCancelled", runMs: runMs, waitMs: Ms(waitStart));

                // Run had already returned: nothing more was sent, so this isn't outcomeUnknown (AD-9).
                throw new OneNoteException(
                    ErrorCode.Cancelled,
                    "The call was cancelled while waiting for the user to choose. The picker may still be on screen, and a choice made in it now is ignored.",
                    innerException: ex,
                    retryable: true);
            }
            catch (OneNoteException ex)
            {
                // A getter failed in the callback: its mapped error (AD-9), not outcomeUnknown.
                Log(ex.Code.ToString(), runMs: runMs, waitMs: Ms(waitStart));
                throw;
            }
            finally
            {
                picker.Release();
            }

            var waitMs = Ms(waitStart);
            if (!answer.Picked)
            {
                Log("cancelled", runMs: runMs, waitMs: waitMs, pressed: answer.PressedButton);
                return new ToolSuccess(null, new ToolMeta(context.CorrelationId), [new ToolWarning(ToolWarnings.PickerCancelled, CancelledMessage)]);
            }

            Log("picked", answer.SelectedItem, runMs, waitMs, answer.PressedButton);
            var xml = await ReadChosenAsync(answer.SelectedItem, ct).ConfigureAwait(false);
            return new ToolSuccess(xml, new ToolMeta(context.CorrelationId, ObjectId: answer.SelectedItem), []);
        }
        finally
        {
            Volatile.Write(ref waiting, 0);
        }
    }

    /// <summary>
    /// The chosen section's own element, from one <c>GetHierarchy(id, self)</c>; null when the read fails or isn't a section,
    /// notebook or section group (the call stays ok with the ID only; 4.9 plan OQ1 a). A section's own element carries no
    /// pages, so it needs no read grant even when protected (AD-21).
    /// </summary>
    private async Task<string?> ReadChosenAsync(string id, CancellationToken ct)
    {
        try
        {
            var xml = await gateway.GetHierarchyAsync(id, HierarchyScope.Self, ct).ConfigureAwait(false);
            return HierarchyNode.Root(xml).Kind is HierarchyKind.Section or HierarchyKind.SectionGroup or HierarchyKind.Notebook ? xml : null;
        }
#pragma warning disable CA1031 // A failed read leaves the pick standing: ok with the ID only.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>Logs an answer that arrives after its call stopped waiting; it is never acted on.</summary>
    private void WatchLateAnswer(QuickFilingPicker picker, string correlationId, int titleLength, int descriptionLength)
    {
        if (log is not { } sink || picker.Answer.IsCompleted)
        {
            return;
        }

        _ = picker.Answer.ContinueWith(
            t =>
            {
                var chosen = t.IsCompletedSuccessfully && t.Result.Picked ? t.Result.SelectedItem : null;
                sink(new QuickFilingLogEntry(correlationId, "lateAnswer", titleLength, descriptionLength, chosen));
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private long Ms(long start) => (long)clock.GetElapsedTime(start).TotalMilliseconds;

    private sealed class Show(IOneNoteGateway gateway, string title, string description) : AppStateOperation<QuickFilingPicker>
    {
        public override void Validate()
        {
            Check("title", title, MaxTitleLength);
            Check("description", description, MaxDescriptionLength);
        }

        public override Task<QuickFilingPicker> CallAsync(CancellationToken ct) => gateway.ShowQuickFilingAsync(title, description, ct);

        private static void Check(string name, string value, int max)
        {
            if (value.Length > max)
            {
                throw new OneNoteException(
                    ErrorCode.InvalidArgument,
                    string.Create(CultureInfo.InvariantCulture, $"{name} is {value.Length} characters long; quick_filing takes at most {max}. Nothing was shown."),
                    retryable: false);
            }

            if (value.Any(char.IsControl))
            {
                throw new OneNoteException(
                    ErrorCode.InvalidArgument,
                    $"{name} contains a control character (a line break, tab or similar); pass plain text on one line. Nothing was shown.",
                    retryable: false);
            }
        }
    }
}
