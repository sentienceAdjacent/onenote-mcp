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
    private readonly Lock _gate = new();
    private readonly Queue<WorkItem> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Guarded by _gate.
    private bool _stopping;
    private Func<Exception>? _poison;

    // Touched only on the STA thread; HasApplication mirrors it for other threads.
    private IApplication? _application;
    private volatile bool _hasApplication;

    public StaThread(string name)
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public int ManagedThreadId => _thread.ManagedThreadId;

    /// <summary>Completes once the thread has released its COM reference and exited.</summary>
    public Task Exited => _exited.Task;

    public bool IsPoisoned
    {
        get
        {
            lock (_gate)
            {
                return _poison is not null;
            }
        }
    }

    /// <summary>Whether the worker holds a COM reference. Readable from any thread; may lag the STA by one item.</summary>
    public bool HasApplication => _hasApplication;

    /// <summary>The owned COM reference. Read and set only on this worker's STA thread.</summary>
    public IApplication? Application
    {
        get
        {
            AssertOnThread();
            return _application;
        }

        set
        {
            AssertOnThread();
            _application = value;
            _hasApplication = value is not null;
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
        lock (_gate)
        {
            if (_poison is not null)
            {
                item.TryAbandon(_poison());
                return item;
            }

            if (_stopping)
            {
                item.TryAbandon(new ObjectDisposedException(nameof(StaThread)));
                return item;
            }

            _queue.Enqueue(item);
        }

        _signal.Set();
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
        var app = _application;
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
        lock (_gate)
        {
            if (_poison is not null)
            {
                return;
            }

            _poison = failure;
            _stopping = true;
            drained = [.. _queue];
            _queue.Clear();
        }

        foreach (var item in drained)
        {
            item.TryAbandon(failure());
        }

        _signal.Set();
    }

    /// <summary>Drains work already queued, then stops the thread, releasing its COM reference.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
        }

        _signal.Set();
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private void AssertOnThread()
    {
        if (Thread.CurrentThread != _thread)
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
                lock (_gate)
                {
                    if (!_queue.TryDequeue(out item) && _stopping)
                    {
                        return;
                    }
                }

                if (item is null)
                {
                    _signal.WaitOne();
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
                _hasApplication = false;
            }

            _exited.TrySetResult();
        }
    }
}

/// <summary>A queued call. It runs at most once; until it starts it can be abandoned with a chosen failure.</summary>
internal abstract class WorkItem
{
    private const int Queued = 0;
    private const int Running = 1;
    private const int Finished = 2;

    private int _state;

    /// <summary>True once the STA thread has started running it (or it finished).</summary>
    public bool Started => Volatile.Read(ref _state) != Queued;

    /// <summary>Fails the item with <paramref name="failure"/> if it hasn't started. Returns false if it already started.</summary>
    public bool TryAbandon(Exception failure)
    {
        if (Interlocked.CompareExchange(ref _state, Finished, Queued) != Queued)
        {
            return false;
        }

        Fail(failure);
        return true;
    }

    internal void Run()
    {
        if (Interlocked.CompareExchange(ref _state, Running, Queued) != Queued)
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
    private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<T> Task => _tcs.Task;

    protected override void Execute()
    {
        if (ct.IsCancellationRequested)
        {
            _tcs.TrySetCanceled(ct);
            return;
        }

        try
        {
            _tcs.TrySetResult(work());
        }
#pragma warning disable CA1031 // Every failure is handed to the awaiting caller.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _tcs.TrySetException(ex);
        }
    }

    protected override void Fail(Exception failure)
    {
        if (failure is OperationCanceledException oce)
        {
            _tcs.TrySetCanceled(oce.CancellationToken);
        }
        else
        {
            _tcs.TrySetException(failure);
        }
    }
}
