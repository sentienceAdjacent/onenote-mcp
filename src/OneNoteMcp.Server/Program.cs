using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OneNoteMcp.Server;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries MCP JSON-RPC only; every log line goes to stderr (AD-13).
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.AddSimpleConsole(o => o.SingleLine = true); // one stderr line per log record

builder.Services
    .AddOneNoteMcp()
    .WithStdioServerTransport();

await builder.Build().RunAsync();
