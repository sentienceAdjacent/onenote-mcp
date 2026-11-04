using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OneNoteMcp.Com;
using OneNoteMcp.Core;
using OneNoteMcp.Server.Tools;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries MCP JSON-RPC only; every log line goes to stderr (AD-13).
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<IProcessProbe, ProcessProbe>();
builder.Services.AddSingleton<IOneNoteGateway, ComGateway>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<GetHierarchyTool>();

await builder.Build().RunAsync();
