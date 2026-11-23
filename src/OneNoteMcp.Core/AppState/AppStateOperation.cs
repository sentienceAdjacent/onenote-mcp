namespace OneNoteMcp.Core.AppState;

/// <summary>
/// One <c>AppState</c> call (AD-7 <c>AppState</c> pipeline): the steps <see cref="AppStatePipeline"/> runs, in its order and
/// under its deadline. A tool supplies its argument checks (step 1), its own reads and checks (steps 3 and 4, none by
/// default) and the COM call (step 7). There is no grant, no basis and no snapshot: nothing here changes page content, and
/// the journal can't undo an open, a close, a sync or a navigation. Only <c>close_notebook</c> and <c>open_notebook</c> take
/// the write lock (<see cref="TakesWriteLock"/>; AD-18). Each instance serves one call, so a step may keep what an earlier
/// step read.
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

    /// <summary>
    /// True for the two <c>AppState</c> tools that take the write lock (AD-7 step 2, AD-18; Phill, 2026-11-22, OQ1 a):
    /// <c>close_notebook</c> and <c>open_notebook</c>. Then the abandoned-write check runs right after step 2, not at the
    /// start, and step 8 keeps the lock while a call abandoned in OneNote may still be running. False by default.
    /// </summary>
    public virtual bool TakesWriteLock => false;

    /// <summary>
    /// True when step 7 makes more than one call and the operation itself says, with <see cref="MarkStateChangeSent"/>,
    /// when the one that changes OneNote's state is sent (story 4.4: <c>close_notebook</c>'s sync and settle come first).
    /// A step-7 failure, the deadline or a cancel before that mark is then never <c>outcomeUnknown</c>. False by default:
    /// the whole of step 7 counts as sent.
    /// </summary>
    public virtual bool MarksStateChange => false;

    /// <summary>True once <see cref="MarkStateChangeSent"/> has run.</summary>
    public bool StateChangeSent { get; private set; }

    /// <summary>
    /// True when a step-7 failure now may have changed OneNote's state (AD-9): the call changes state, and either the
    /// operation doesn't mark its send or has marked it.
    /// </summary>
    public bool OutcomeMayBeUnknown => ChangesState && (!MarksStateChange || StateChangeSent);

    /// <summary>Called by step 7 right before it sends the call that changes OneNote's state (see <see cref="MarksStateChange"/>).</summary>
    protected void MarkStateChangeSent() => StateChangeSent = true;
}
