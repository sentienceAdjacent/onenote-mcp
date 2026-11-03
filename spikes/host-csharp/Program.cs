// Spike host (ticket 1): minimal stdio MCP server in C# with one tool, get_hierarchy.
// Every COM call runs on one dedicated STA thread (StaComThread). Not a production shape.
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using OneNote = Microsoft.Office.Interop.OneNote;

var builder = Host.CreateApplicationBuilder(args);
// stdout carries MCP JSON-RPC; all logging goes to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<StaComThread>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<OneNoteTools>();
await builder.Build().RunAsync();

/// <summary>One STA thread that owns the OneNote COM object; work is queued to it.</summary>
public sealed class StaComThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private OneNote.Application? _app;

    public StaComThread()
    {
        _thread = new Thread(() =>
        {
            foreach (var work in _queue.GetConsumingEnumerable()) work();
        }) { IsBackground = true, Name = "OneNote-STA" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public int ManagedThreadId => _thread.ManagedThreadId;

    public Task<T> Run<T>(Func<OneNote.Application, T> call)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(call(_app ??= new OneNote.Application())); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}

[McpServerToolType]
public sealed class OneNoteTools(StaComThread sta)
{
    [McpServerTool(Name = "get_hierarchy"), Description("Spike: return OneNote GetHierarchy XML. Read-only.")]
    public async Task<string> GetHierarchy(
        [Description("notebooks | sections | pages")] string scope = "notebooks",
        [Description("Start node ID; empty for all notebooks")] string startId = "",
        [Description("Test only: hold the STA thread this many ms after the call, to expose queueing")] int holdMs = 0)
    {
        var hs = scope switch
        {
            "sections" => OneNote.HierarchyScope.hsSections,
            "pages" => OneNote.HierarchyScope.hsPages,
            _ => OneNote.HierarchyScope.hsNotebooks,
        };
        var queued = Stopwatch.StartNew();
        try
        {
            var (xml, callMs, waitMs, apt) = await sta.Run(app =>
            {
                var waited = queued.ElapsedMilliseconds;
                var sw = Stopwatch.StartNew();
                app.GetHierarchy(startId, hs, out string x, OneNote.XMLSchema.xs2013);
                var ms = sw.ElapsedMilliseconds;
                if (holdMs > 0) Thread.Sleep(holdMs);
                return (x, ms, waited, Thread.CurrentThread.GetApartmentState());
            });
            return $"<!-- host=csharp thread={sta.ManagedThreadId} apartment={apt} queueWaitMs={waitMs} callMs={callMs} -->\n{xml}";
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            return $"error: COM 0x{ex.HResult:x8} {ex.Message}";
        }
    }
}
