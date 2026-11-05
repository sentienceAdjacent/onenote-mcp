using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OneNoteMcp.Com;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Logging;
using Serilog;

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
