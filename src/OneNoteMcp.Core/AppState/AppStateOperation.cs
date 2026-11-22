namespace OneNoteMcp.Core.AppState;

/// <summary>
/// One <c>AppState</c> call (AD-7 <c>AppState</c> pipeline): the steps <see cref="AppStatePipeline"/> runs, in its order and
/// under its deadline. A tool supplies its argument checks (step 1), its own reads and checks (steps 3 and 4, none by
/// default) and the COM call (step 7). There is no grant, no lock, no basis and no snapshot: nothing here changes page
/// content, and the journal can't undo an open, a close, a sync or a navigation. Each instance serves one call, so a step
/// may keep what an earlier step read.
/// </summary>
/// <typeparam name="TResult">What the COM call returns.</typeparam>
public abstract class AppStateOperation<TResult>
{
    /// <summary>Step 1: the tool's argument checks, before anything is read or sent. Throws to refuse the call before COM.</summary>
    public abstract void Validate();

    /// <summary>Steps 3 and 4: the tool's own reads and checks. None by default.</summary>
    public virtual Task ReadAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Step 7: the COM call. A failure here can leave the outcome unknown; the runner decides (AD-9).</summary>
    public abstract Task<TResult> CallAsync(CancellationToken ct);

    /// <summary>
    /// True when the call changes OneNote's state, which every <c>AppState</c> call does except <c>get_windows</c>' reads
    /// (spike 4.12 W2): then a step-7 timeout, disconnect or cancel is <c>outcomeUnknown</c>. True by default.
    /// </summary>
    public virtual bool ChangesState => true;
}
