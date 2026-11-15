using System.Runtime.InteropServices;
using OneNoteMcp.Com.Interop;

namespace OneNoteMcp.Com;

/// <summary>
/// One gateway worker (AD-2, AD-3): a dedicated STA thread that runs queued work in order and owns at most one
/// COM reference (<see cref="Application"/>). The thread waits with <see cref="WaitHandle.WaitOne()"/>, which the CLR
/// implements as a pumping wait on an STA thread, so COM messages are pumped while the queue is idle.
/// <para>
/// <see cref="Poison"/> abandons the worker after a hung call: queued work fails at once, nothing new is accepted, and
/// when the running call eventually returns the thread releases its COM reference and exits (<see cref="Exited"/>).
/// </para>
/// </summary>
internal sealed class StaThread : IDisposable
{
    private readonly Lock gate = new();
    private readonly Queue<WorkItem> queue = new();
    private readonly AutoResetEvent signal = new(false);
    private readonly Thread thread;
    private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Guarded by gate.
    private bool stopping;
    private Func<Exception>? poison;

    // Touched only on the STA thread; HasApplication mirrors it for other threads.
    private IApplication? application;
    private volatile bool hasApplication;

    public StaThread(string name)
    {
        thread = new Thread(Loop) { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public int ManagedThreadId => thread.ManagedThreadId;

    /// <summary>Completes once the thread has released its COM reference and exited.</summary>
    public Task Exited => exited.Task;

    public bool IsPoisoned
    {
        get
        {
            lock (gate)
            {
                return poison is not null;
            }
        }
    }

    /// <summary>Whether the worker holds a COM reference. Readable from any thread; may lag the STA by one item.</summary>
    public bool HasApplication => hasApplication;

    /// <summary>The owned COM reference. Read and set only on this worker's STA thread.</summary>
    public IApplication? Application
    {
        get
        {
            AssertOnThread();
            return application;
        }

        set
        {
            AssertOnThread();
            application = value;
            hasApplication = value is not null;
        }
    }

    /// <summary>Queues <paramref name="work"/> on the STA thread. Exceptions it throws fault the task.</summary>
    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken ct = default) => Enqueue(work, ct).Task;

    /// <summary>
    /// Queues <paramref name="work"/> and returns the item, so the caller can tell whether it started and abandon it
    /// if it hasn't. On a stopped or poisoned worker the item is already failed.
    /// </summary>
    public WorkItem<T> Enqueue<T>(Func<T> work, CancellationToken ct = default)
    {
        var item = new WorkItem<T>(work, ct);
        lock (gate)
        {
            if (poison is not null)
            {
                item.TryAbandon(poison());
                return item;
            }

            if (stopping)
            {
                item.TryAbandon(new ObjectDisposedException(nameof(StaThread)));
                return item;
            }

            queue.Enqueue(item);
        }

        signal.Set();
        return item;
    }

    /// <summary>Queues fire-and-forget housekeeping (such as <see cref="ReleaseApplication"/>). Dropped on a stopped worker.</summary>
    public void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var task = Enqueue(
            () =>
            {
                work();
                return true;
            }).Task;
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>Releases the owned COM reference. Runs on the STA thread.</summary>
    public void ReleaseApplication()
    {
        AssertOnThread();
        var app = application;
        Application = null;
        if (app is not null && Marshal.IsComObject(app))
        {
            Marshal.FinalReleaseComObject(app);
        }
    }

    /// <summary>
    /// Abandons the worker: every queued item fails with <paramref name="failure"/>, new items fail the same way, and the
    /// thread exits (releasing its COM reference) as soon as the running item, if any, returns. Idempotent.
    /// </summary>
    public void Poison(Func<Exception> failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        List<WorkItem> drained;
        lock (gate)
        {
            if (poison is not null)
            {
                return;
            }

            poison = failure;
            stopping = true;
            drained = [.. queue];
            queue.Clear();
        }

        foreach (var item in drained)
        {
            item.TryAbandon(failure());
        }

        signal.Set();
    }

    /// <summary>Drains work already queued, then stops the thread, releasing its COM reference.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (stopping)
            {
                return;
            }

            stopping = true;
        }

        signal.Set();
        if (Thread.CurrentThread != thread)
        {
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private void AssertOnThread()
    {
        if (Thread.CurrentThread != thread)
        {
            throw new InvalidOperationException("The COM reference is touched only on its own STA thread (AD-2).");
        }
    }

    private void Loop()
    {
        try
        {
            while (true)
            {
                WorkItem? item;
                lock (gate)
                {
                    if (!queue.TryDequeue(out item) && stopping)
                    {
                        return;
                    }
                }

                if (item is null)
                {
                    signal.WaitOne();
                    continue;
                }

                item.Run();
            }
        }
        finally
        {
            try
            {
                ReleaseApplication();
            }
#pragma warning disable CA1031 // The thread is exiting; a failed release must not stop Exited from completing.
            catch (Exception)
#pragma warning restore CA1031
            {
                hasApplication = false;
            }

            exited.TrySetResult();
        }
    }
}

/// <summary>
/// A queued call. It runs at most once; until it starts it can be abandoned with a chosen failure. It leaves
/// <c>Queued</c> exactly once, either to <c>Running</c> (the STA thread took it) or to <c>Abandoned</c> (it never ran).
/// </summary>
internal abstract class WorkItem
{
    private const int Queued = 0;
    private const int Running = 1;
    private const int Abandoned = 2;

    private int state;

    /// <summary>True once the STA thread has started running it (it may have returned since). False if it was abandoned.</summary>
    public bool Started => Volatile.Read(ref state) == Running;

    /// <summary>True once someone abandoned it before it started: it never ran. Its task may complete a moment later.</summary>
    public bool WasAbandoned => Volatile.Read(ref state) == Abandoned;

    /// <summary>
    /// Fails the item with <paramref name="failure"/> if it hasn't started. Returns false if it already started, or if
    /// someone else abandoned it first (<see cref="WasAbandoned"/> tells the two apart).
    /// </summary>
    public bool TryAbandon(Exception failure)
    {
        if (Interlocked.CompareExchange(ref state, Abandoned, Queued) != Queued)
        {
            return false;
        }

        Fail(failure);
        return true;
    }

    internal void Run()
    {
        if (Interlocked.CompareExchange(ref state, Running, Queued) != Queued)
        {
            return;
        }

        Execute();
    }

    protected abstract void Execute();

    protected abstract void Fail(Exception failure);
}

internal sealed class WorkItem<T>(Func<T> work, CancellationToken ct) : WorkItem
{
    private readonly TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<T> Task => tcs.Task;

    protected override void Execute()
    {
        if (ct.IsCancellationRequested)
        {
            tcs.TrySetCanceled(ct);
            return;
        }

        try
        {
            tcs.TrySetResult(work());
        }
#pragma warning disable CA1031 // Every failure is handed to the awaiting caller.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            tcs.TrySetException(ex);
        }
    }

    protected override void Fail(Exception failure)
    {
        if (failure is OperationCanceledException oce)
        {
            tcs.TrySetCanceled(oce.CancellationToken);
        }
        else
        {
            tcs.TrySetException(failure);
        }
    }
}
