using System.Runtime.InteropServices;
using OneNoteMcp.Com.Interop;
using OneNoteMcp.Core;
using CoreScope = OneNoteMcp.Core.HierarchyScope;
using InteropScope = OneNoteMcp.Com.Interop.HierarchyScope;

namespace OneNoteMcp.Com;

/// <summary>
/// The only real <see cref="IOneNoteGateway"/> adapter (AD-2): every COM call runs on one STA thread, which
/// owns the single COM reference. Failures map through <see cref="HResultMap"/> (AD-9). This is still the minimal
/// actor: timeouts, reconnect and idle release (AD-3) come in story 5. On a disconnect HRESULT the reference is
/// dropped (no retry) so the next call re-activates.
/// </summary>
public sealed class ComGateway : IOneNoteGateway, IDisposable
{
    private const int RpcServerUnavailable = unchecked((int)0x800706BA); // RPC_S_SERVER_UNAVAILABLE
    private const int RpcDisconnected = unchecked((int)0x80010108); // RPC_E_DISCONNECTED

    private readonly IProcessProbe _probe;
    private readonly Func<object> _createApplication;
    private readonly StaThread _sta = new("OneNote-STA");

    // Touched only on the STA thread.
    private IApplication? _app;

    public ComGateway(IProcessProbe probe)
        : this(probe, CreateApplication)
    {
    }

    /// <summary>Test seam: <paramref name="createApplication"/> replaces COM activation.</summary>
    internal ComGateway(IProcessProbe probe, Func<object> createApplication)
    {
        _probe = probe;
        _createApplication = createApplication;
    }

    public Task<string> GetHierarchyAsync(string startNodeId, CoreScope scope, CancellationToken ct)
    {
        var interopScope = MapScope(scope);
        return RunAsync(
            nameof(IApplication.GetHierarchy),
            app =>
            {
                app.GetHierarchy(startNodeId ?? string.Empty, interopScope, out var xml, XMLSchema.xs2013);
                return xml;
            },
            ct);
    }

    public void Dispose()
    {
        try
        {
            _sta.InvokeAsync(() =>
            {
                ReleaseApplication();
                return true;
            }).Wait(TimeSpan.FromSeconds(5));
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        _sta.Dispose();
    }

    internal static InteropScope MapScope(CoreScope scope) => scope switch
    {
        CoreScope.Self => InteropScope.hsSelf,
        CoreScope.Children => InteropScope.hsChildren,
        CoreScope.Notebooks => InteropScope.hsNotebooks,
        CoreScope.Sections => InteropScope.hsSections,
        CoreScope.Pages => InteropScope.hsPages,
        _ => throw new OneNoteException(ErrorCode.Internal, $"Unknown hierarchy scope {(int)scope}."),
    };

    private Task<T> RunAsync<T>(string method, Func<IApplication, T> call, CancellationToken ct) =>
        _sta.InvokeAsync(
            () =>
            {
                var app = EnsureApplication();
                try
                {
                    return call(app);
                }
                catch (Exception ex) when (ex is not OneNoteException)
                {
                    // A dead RCW (OneNote restarted) fails forever; drop it so the next call re-activates.
                    if (ex.HResult is RpcServerUnavailable or RpcDisconnected)
                    {
                        ReleaseApplication();
                    }

                    throw HResultMap.ToException(method, ex);
                }
            },
            ct);

    // Runs on the STA thread. Checks for ONENOTE.EXE before every call, so a closed OneNote is reported as
    // not running (and the stale reference dropped) instead of being reactivated silently.
    private IApplication EnsureApplication()
    {
        if (!_probe.IsOneNoteRunning())
        {
            ReleaseApplication();
            throw new OneNoteException(
                ErrorCode.OneNoteNotRunning,
                "OneNote is not running. Start OneNote desktop, then retry.");
        }

        if (_app is not null)
        {
            return _app;
        }

        try
        {
            _app = (IApplication)_createApplication();
        }
        catch (Exception ex) when (ex is not OneNoteException)
        {
            throw HResultMap.ToException("Connecting to OneNote", ex);
        }

        return _app;
    }

    private void ReleaseApplication()
    {
        if (_app is not null && Marshal.IsComObject(_app))
        {
            Marshal.FinalReleaseComObject(_app);
        }

        _app = null;
    }

    private static object CreateApplication()
    {
        var type = Type.GetTypeFromCLSID(OneNoteClsid.Application, throwOnError: true)!;
        return Activator.CreateInstance(type)!;
    }
}
