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
    public Task NavigateToAsync(string hierarchyId, string objectId, CancellationToken ct) =>
        NavigateToAsync(hierarchyId, objectId, window: null, ct);

    public Task NavigateToAsync(string hierarchyId, string objectId, int? window, CancellationToken ct) =>
        RunAsync(
            window is null ? nameof(IApplication.NavigateTo) : WindowNavigateToMethod,
            app =>
            {
                if (window is { } handle)
                {
                    // Story 4.6: the handle check and the call in one STA delegate, so a window closed in between is
                    // InvalidArgument rather than a stale wrapper.
                    var (id, objectInPage) = (hierarchyId ?? string.Empty, objectId ?? string.Empty);
                    NavigateInWindow(app, handle, w => w.NavigateTo(id, objectInPage));
                }
                else
                {
                    // fNewWindow is always false: never open a window the user didn't ask for (AD-11).
                    app.NavigateTo(hierarchyId ?? string.Empty, objectId ?? string.Empty, fNewWindow: false);
                }

                return true;
            },
            // AppState, not a read: a call that reached COM and then lost the connection isn't repeated (AD-3).
            CallKind.AppState,
            ct);

    public Task NavigateToUrlAsync(string url, CancellationToken ct) =>
        NavigateToUrlAsync(url, window: null, ct);

    public Task NavigateToUrlAsync(string url, int? window, CancellationToken ct)
    {
        var method = window is null ? nameof(IApplication.NavigateToUrl) : WindowNavigateToUrlMethod;
        if (string.IsNullOrEmpty(url))
        {
            return Task.FromException(new OneNoteException(ErrorCode.InvalidArgument, $"{method} wasn't sent: no link was given.", retryable: false));
        }

        return RunAsync(
            method,
            app =>
            {
                if (window is { } handle)
                {
                    // Story 4.7: the handle check and the call in one STA delegate, as navigate_to's (story 4.6).
                    NavigateInWindow(app, handle, w => w.NavigateToUrl(url));
                }
                else
                {
                    // fNewWindow is always false: never open a window the user didn't ask for (AD-11; epic 4 Q7 a).
                    app.NavigateToUrl(url, fNewWindow: false);
                }

                return true;
            },
            // AppState (story 4.7): never repeated after a disconnect, and never an abandoned write (AD-3, AD-7).
            CallKind.AppState,
            ct);
    }

    public Task<IReadOnlyList<OneNoteWindow>> GetWindowsAsync(CancellationToken ct) =>
        RunAsync(
            nameof(IApplication.Windows),
            ReadWindows,
            // A read for AD-3 (spine AD-7 AppState): retried once after a disconnect; it changes nothing (spike 4.12 W2).
            CallKind.Read,
            ct);

    /// <summary>The method name failures of <c>navigate_to</c>'s window path carry.</summary>
    internal const string WindowNavigateToMethod = "Window.NavigateTo";

    /// <summary>The method name failures of <c>navigate_to_url</c>'s window path carry (story 4.7).</summary>
    internal const string WindowNavigateToUrlMethod = "Window.NavigateToUrl";

    /// <summary>
    /// Test seam (story 4.6): releases one wrapper the window calls obtained, on the STA thread. By default
    /// <see cref="Marshal.ReleaseComObject"/> for a COM object, nothing for a managed test double.
    /// </summary>
    internal Action<object> ReleaseWrapper { get; set; } = ReleaseComWrapper;

    private static void ReleaseComWrapper(object wrapper)
    {
        if (Marshal.IsComObject(wrapper))
        {
            Marshal.ReleaseComObject(wrapper);
        }
    }

    /// <summary>Releases every wrapper in <paramref name="wrappers"/>, once per time it was obtained (OneMore, research row 10).</summary>
    private void ReleaseAll(List<object> wrappers)
    {
        foreach (var wrapper in wrappers)
        {
            ReleaseWrapper(wrapper);
        }
    }

    /// <summary>
    /// Story 4.6, on the STA thread: <c>Windows</c>, <c>Count</c>, <c>CurrentWindow</c>, then <c>Item(0)</c> …
    /// <c>Item(Count - 1)</c> and each window's getters, never <c>Item(Count)</c> (spike 4.12 W1). A window is current when its
    /// handle is <c>CurrentWindow</c>'s. Every wrapper is released in the <c>finally</c>.
    /// </summary>
    private IReadOnlyList<OneNoteWindow> ReadWindows(IApplication app)
    {
        var wrappers = new List<object>();
        try
        {
            if (app.Windows is not { } raw)
            {
                return [];
            }

            // Before the cast, so a failed cast still releases it.
            wrappers.Add(raw);
            var windows = (IWindows)raw;
            var count = windows.Count;
            ulong? currentHandle = null;
            if (windows.CurrentWindow is { } current)
            {
                wrappers.Add(current);
                currentHandle = current.WindowHandle;
            }

            var listed = new List<OneNoteWindow>();
            for (uint i = 0; i < count; i++)
            {
                if (windows.GetItem(i) is not { } window)
                {
                    continue;
                }

                wrappers.Add(window);
                var handle = window.WindowHandle;
                listed.Add(new OneNoteWindow(
                    handle,
                    handle == currentHandle,
                    window.GetActive(),
                    window.CurrentNotebookId ?? string.Empty,
                    window.CurrentSectionGroupId ?? string.Empty,
                    window.CurrentSectionId ?? string.Empty,
                    window.CurrentPageId ?? string.Empty,
                    window.GetFullPageView(),
                    window.GetDockedLocation(),
                    window.GetSideNote()));
            }

            return listed;
        }
        finally
        {
            ReleaseAll(wrappers);
        }
    }

    /// <summary>
    /// Story 4.6, on the STA thread: walks <c>Item(0)</c> … <c>Item(Count - 1)</c> for the window whose handle is
    /// <paramref name="handle"/> and makes the per-window call <paramref name="navigate"/> on it (<c>NavigateTo</c>, or story
    /// 4.7's <c>NavigateToUrl</c>). None has it: <see cref="ErrorCode.InvalidArgument"/>, and nothing is shown. Every wrapper
    /// is released in the <c>finally</c>.
    /// </summary>
    private void NavigateInWindow(IApplication app, int handle, Action<IWindow> navigate)
    {
        var wrappers = new List<object>();
        try
        {
            if (app.Windows is { } raw)
            {
                wrappers.Add(raw);
                var windows = (IWindows)raw;
                var count = windows.Count;
                for (uint i = 0; i < count; i++)
                {
                    if (windows.GetItem(i) is not { } window)
                    {
                        continue;
                    }

                    wrappers.Add(window);
                    if (OneNoteWindow.HandleOf(window.WindowHandle) == handle)
                    {
                        navigate(window);
                        return;
                    }
                }
            }

            throw new OneNoteException(
                ErrorCode.InvalidArgument,
                OneNoteWindow.UnknownHandleMessage(handle),
                userAction: OneNoteWindow.UnknownHandleUserAction,
                retryable: false);
        }
        finally
        {
            ReleaseAll(wrappers);
        }
    }
}
