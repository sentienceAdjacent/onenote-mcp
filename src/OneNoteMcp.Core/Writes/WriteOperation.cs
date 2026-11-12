namespace OneNoteMcp.Core.Writes;

/// <summary>
/// One write of one operation class (AD-7): the steps <see cref="WritePipeline"/> runs, in its order, under its lock and
/// deadline. A class supplies the target's access check (1), the current-state read (3), validation (4) and the COM call
/// (7), and names the pages it wrote. Steps 5 (optimistic check, AD-17) and 6 (snapshot, AD-8) are hooks that do
/// nothing unless a class needs them, so a later class adds them without reshaping the runner. Each instance serves one
/// call, so a step may keep what an earlier step read.
/// </summary>
/// <typeparam name="TResult">What the COM call returns, such as a new page's ID.</typeparam>
public abstract class WriteOperation<TResult>
{
    /// <summary>The AD-7 class this write belongs to.</summary>
    public abstract OperationClass Class { get; }

    /// <summary>Step 1: the access check (AD-6). Runs before the lock.</summary>
    public abstract Task AuthorizeAsync(CancellationToken ct);

    /// <summary>Step 3: reads the current state, under the lock.</summary>
    public abstract Task ReadAsync(CancellationToken ct);

    /// <summary>Step 4: validates the write against step 3's read. Throws to refuse it before COM.</summary>
    public abstract void Validate();

    /// <summary>Step 5: the optimistic check (AD-17). None by default.</summary>
    public virtual Task CheckBasisAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Step 6: the snapshot (AD-8). None by default.</summary>
    public virtual Task SnapshotAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Step 7: the COM call. A failure here can leave the outcome unknown; the runner decides (AD-9).</summary>
    public abstract Task<TResult> CallAsync(CancellationToken ct);

    /// <summary>The pages this write wrote or may have written, dropped from the search page-text cache whatever the outcome (AD-7, AD-12).</summary>
    public virtual IReadOnlyCollection<string> WrittenPageIds => [];
}
