using System.Globalization;
using System.Runtime.InteropServices;
using OneNoteMcp.Com.Interop;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Xml;
using CoreNewPageStyle = OneNoteMcp.Core.NewPageStyle;
using CorePageInfo = OneNoteMcp.Core.PageInfo;
using CoreScope = OneNoteMcp.Core.HierarchyScope;
using CoreSpecialLocation = OneNoteMcp.Core.SpecialLocation;
using InteropNewPageStyle = OneNoteMcp.Com.Interop.NewPageStyle;
using InteropPageInfo = OneNoteMcp.Com.Interop.PageInfo;
using InteropScope = OneNoteMcp.Com.Interop.HierarchyScope;
using InteropSpecialLocation = OneNoteMcp.Com.Interop.SpecialLocation;

namespace OneNoteMcp.Com;

public sealed partial class ComGateway
{
    /// <summary>The QuickFiling picker's one button (story 4.9): it takes a section and is the default.</summary>
    internal const string QuickFilingButton = "Choose";

    /// <summary>
    /// Story 4.9: one STA call shows the picker (<c>QuickFiling</c>, the cast to <see cref="IQuickFilingDialog"/>, <c>Title</c>,
    /// <c>Description</c>, <c>TreeDepth = heSections</c>, one default <c>AddButton</c> for sections, then <c>Run</c>), never
    /// <c>ParentWindowHandle</c> or any other member. The gateway holds the picker from just before <c>Run</c> until the
    /// answer arrives or the caller lets go (<see cref="QuickFilingPicker.Release"/>), whichever is first; while held, the
    /// idle release keeps the COM reference (AD-3). A call that fails, times out or is cancelled lets go itself.
    /// </summary>
    public async Task<QuickFilingPicker> ShowQuickFilingAsync(string title, string description, CancellationToken ct)
    {
        var hold = new PickerHold(this);
        try
        {
            await RunAsync(
                nameof(IApplication.QuickFiling),
                app =>
                {
                    ShowPicker(app, hold, title ?? string.Empty, description ?? string.Empty);
                    return true;
                },
                // AppState (story 4.9): never repeated after a disconnect, and never an abandoned write (AD-3, AD-7).
                CallKind.AppState,
                ct).ConfigureAwait(false);
        }
        catch
        {
            // Nobody will wait on this picker: let go (a picker Run already showed stays on screen; its answer is ignored).
            hold.Release();
            throw;
        }

        return hold.Picker;
    }

    /// <summary>Picker holds not yet ended (story 4.9): while above 0, the idle release keeps the COM reference (AD-3).</summary>
    internal int PickerHolds
    {
        get
        {
            lock (gate)
            {
                return pickerHolds;
            }
        }
    }

    /// <summary>Story 4.9, on the STA thread: see <see cref="ShowQuickFilingAsync"/>. A wrapper not handed to the hold is released here.</summary>
    private void ShowPicker(IApplication app, PickerHold hold, string title, string description)
    {
        var raw = app.QuickFiling() ?? throw new OneNoteException(ErrorCode.Internal, "QuickFiling returned no picker, so nothing was shown.");
        var handedOver = false;
        try
        {
            var dialog = (IQuickFilingDialog)raw;
            dialog.Title = title;
            dialog.Description = description;
            dialog.TreeDepth = HierarchyElement.heSections;
            dialog.AddButton(QuickFilingButton, HierarchyElement.heSections, HierarchyElement.heNone, fDefault: true);
            if (!hold.TryBegin(CurrentWorker(), raw))
            {
                // The caller already stopped waiting (its deadline or cancel): show nothing nobody would wait for.
                throw new OperationCanceledException("The quick_filing call stopped waiting before Run; nothing was shown.");
            }

            handedOver = true;
            dialog.Run(hold.Callback);
        }
        finally
        {
            if (!handedOver)
            {
                ReleaseWrapper(raw);
            }
        }
    }

    /// <summary>The live worker when this is its STA thread; null otherwise (it was poisoned or replaced).</summary>
    private StaThread? CurrentWorker()
    {
        lock (gate)
        {
            return worker is { } live && live.ManagedThreadId == Environment.CurrentManagedThreadId ? live : null;
        }
    }

    private void BeginPickerHold()
    {
        lock (gate)
        {
            pickerHolds++;
            if (!disposed)
            {
                idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
    }

    private void EndPickerHold()
    {
        lock (gate)
        {
            pickerHolds--;
        }
    }

    /// <summary>After a picker's hold ends or its caller lets go: restarts the idle timer when nothing else holds the reference (AD-3).</summary>
    private void RestartIdleTimer()
    {
        lock (gate)
        {
            if (inFlight == 0 && pickerHolds == 0 && !disposed)
            {
                idleTimer.Change(settings.Timeouts.IdleRelease, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>
    /// Releases a picker's dialog wrapper on the STA thread that obtained it, if that worker is still the live one; otherwise
    /// drops it, since a wrapper is touched only on its own STA thread (AD-2).
    /// </summary>
    private void ReleaseDialog(StaThread? owner, object dialog)
    {
        lock (gate)
        {
            if (!disposed && owner is not null && ReferenceEquals(worker, owner) && !owner.IsPoisoned)
            {
                owner.Post(() => ReleaseWrapper(dialog));
            }
        }
    }

    /// <summary>
    /// Story 4.9: one QuickFiling picker's hold (AD-3) and its callback. It holds from <see cref="TryBegin"/>, just before
    /// <c>Run</c>, until the answer arrives or <see cref="Release"/>, whichever is first. <see cref="Release"/> is idempotent:
    /// it ends the hold, restarts the idle timer and releases the dialog wrapper on its STA if that worker is still live.
    /// The answer's continuation runs on the thread pool, never on OneNote's calling thread, so the callback itself queues
    /// no STA work and takes no lock of the gateway's.
    /// </summary>
    private sealed class PickerHold
    {
        private readonly ComGateway owner;
        private readonly Lock sync = new();

        // Guarded by sync.
        private StaThread? sta;
        private object? dialog;
        private bool holding;
        private bool released;

        public PickerHold(ComGateway owner)
        {
            this.owner = owner;
            Callback = new QuickFilingCallback();
            Picker = new QuickFilingPicker(Callback.Answer, Release);
            _ = Callback.Answer.ContinueWith(
                t =>
                {
                    // Observe a failed answer nobody waits for any more, then end the hold.
                    _ = t.Exception;
                    EndHold();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public QuickFilingCallback Callback { get; }

        public QuickFilingPicker Picker { get; }

        /// <summary>On the STA, just before <c>Run</c>: starts the hold. False when the caller already let go.</summary>
        public bool TryBegin(StaThread? worker, object wrapper)
        {
            lock (sync)
            {
                if (released)
                {
                    return false;
                }

                sta = worker;
                dialog = wrapper;
                holding = true;
                owner.BeginPickerHold();
                return true;
            }
        }

        public void Release()
        {
            StaThread? worker;
            object? wrapper;
            lock (sync)
            {
                if (released)
                {
                    return;
                }

                released = true;
                EndHoldLocked();
                worker = sta;
                wrapper = dialog;
                sta = null;
                dialog = null;
            }

            owner.RestartIdleTimer();
            if (wrapper is not null)
            {
                owner.ReleaseDialog(worker, wrapper);
            }
        }

        private void EndHold()
        {
            bool ended;
            lock (sync)
            {
                ended = EndHoldLocked();
            }

            if (ended)
            {
                owner.RestartIdleTimer();
            }
        }

        private bool EndHoldLocked()
        {
            if (!holding)
            {
                return false;
            }

            holding = false;
            owner.EndPickerHold();
            return true;
        }
    }
}
