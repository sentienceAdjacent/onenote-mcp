using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OneNoteMcp.Com;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Cli;
using OneNoteMcp.Server.Hooks;
using OneNoteMcp.Server.Logging;
using Serilog;

// The plugin's PreToolUse guard (AD-6) runs before any host is built: hook JSON on stdin, a deny decision on stdout or
// nothing. It never fails the hook: anything unexpected allows silently.
if (args.Length > 0 && args[0] == "hook")
{
    try
    {
        if (args.Length > 1 && args[1] == "guard-settings"
            && SettingsGuard.Decide(await Console.In.ReadToEndAsync(), new SettingsStore().Path) is { } reason)
        {
            Console.Out.Write(SettingsGuard.DenyOutput(reason));
        }
    }
#pragma warning disable CA1031 // A hook must never break the user's tool call.
    catch (Exception)
#pragma warning restore CA1031
    {
    }

    return 0;
}

// The grant CLI (AD-6, AD-21) runs before any host is built: no MCP transport, nothing on stdout but its own dialogue.
if (args.Length > 0 && args[0] == "grant")
{
    var store = new SettingsStore();
    using var gateway = new ComGateway(new ProcessProbe(), store);
    var command = new GrantCommand(
        Console.In,
        Console.Out,
        interactive: !Console.IsInputRedirected && !Console.IsOutputRedirected,
        ct => gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Sections, ct),
        new SettingsWriter(store, TimeProvider.System),
        TimeProvider.System);
    return await command.RunAsync(args[1..], CancellationToken.None);
}

// uninstall --purge (AD-19) runs before any host is built and starts no COM: it only removes the server's own files.
if (args.Length > 0 && args[0] == "uninstall")
{
    return new UninstallCommand(
        Console.In,
        Console.Out,
        interactive: !Console.IsInputRedirected && !Console.IsOutputRedirected,
        UninstallPaths.Resolve(Environment.GetEnvironmentVariable)).Run(args[1..]);
}

var builder = Host.CreateApplicationBuilder(args);

// stdout carries MCP JSON-RPC only; every log line goes to stderr and the rolling file log (AD-13).
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.AddSimpleConsole(o => o.SingleLine = true); // one stderr line per log record

var fileLog = FileLog.Start(new FileLogOptions(FileLog.ResolveDirectory(Environment.GetEnvironmentVariable)));
if (fileLog.Logger is { } fileLogger)
{
    builder.Logging.AddSerilog(fileLogger, dispose: true);
}

builder.Services
    .AddOneNoteMcp()
    .WithStdioServerTransport();

var host = builder.Build();

var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OneNoteMcp.Startup");
var settings = host.Services.GetRequiredService<SettingsStore>().Read();
StartupLog.Started(startupLogger, ServerSetup.Version, Environment.ProcessId, settings, fileLog);

// Integrity at startup (AD-3): levels and process IDs only. Every activation checks again before COM is called.
StartupLog.Integrity(startupLogger, host.Services.GetRequiredService<IProcessProbe>().CheckIntegrity());

await host.RunAsync();
return 0;
