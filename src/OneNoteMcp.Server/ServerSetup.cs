using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Com;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.LargeResults;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server.Tools;

namespace OneNoteMcp.Server;

/// <summary>
/// The one service registration, shared by <c>Program</c> and the tool-contract test so both see the same server.
/// The transport is added by the caller.
/// </summary>
public static class ServerSetup
{
    public const string ServerName = "OneNoteMcp";

    /// <summary>
    /// <c>&lt;Version&gt;</c> from <c>Directory.Build.props</c>: the release tag (AD-19) and <c>serverInfo.version</c>.
    /// Its major.minor is the tool contract version (AD-14).
    /// </summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>
    /// The server's logging (AD-13), shared by <c>Program</c> and the logging test: stdout carries MCP JSON-RPC only,
    /// so every record goes to stderr as one line, and to the rolling file log when <paramref name="fileLogger"/> is set
    /// (it is disposed with the logger factory).
    /// </summary>
    public static ILoggingBuilder AddOneNoteMcpLogging(this ILoggingBuilder logging, Serilog.Core.Logger? fileLogger)
    {
        ArgumentNullException.ThrowIfNull(logging);

        logging.ClearProviders();
        logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        logging.AddSimpleConsole(o => o.SingleLine = true); // one stderr line per log record
        if (fileLogger is not null)
        {
            Serilog.SerilogLoggingBuilderExtensions.AddSerilog(logging, fileLogger, dispose: true);
        }

        return logging;
    }

    public static IMcpServerBuilder AddOneNoteMcp(this IServiceCollection services)
    {
        // Read side of settings.json (AD-10): every Read() re-reads the file, so one instance serves the process.
        services.AddSingleton(_ => new SettingsStore());
        services.AddSingleton<IProcessProbe, ProcessProbe>();
        // The gateway re-reads timeouts and allowSilentActivation from settings.json at every activation (AD-3).
        services.AddSingleton<IOneNoteGateway>(sp => new ComGateway(sp.GetRequiredService<IProcessProbe>(), sp.GetRequiredService<SettingsStore>()));
        // The result store (AD-20) is in this process's memory only, and re-reads results{} from settings.json when used.
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsStore>();
            return new ResultStore(TimeProvider.System, () => settings.Read().Results);
        });
        services.AddSingleton<ITextBlockMeasure>(EnvelopeMeasure.Instance);
        // AD-21: one access check, re-reading settings.json at every check (AD-10); the writer stores the user's grants.
        services.AddSingleton(sp => new SettingsWriter(sp.GetRequiredService<SettingsStore>(), TimeProvider.System));
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsStore>();
            return new ReadAccess(sp.GetRequiredService<IOneNoteGateway>(), settings.Read, sp.GetRequiredService<SettingsWriter>(), TimeProvider.System);
        });
        services.AddSingleton(sp => sp.GetRequiredService<ReadAccess>().Results);
        services.AddSingleton<ResultDelivery>();
        services.AddSingleton(sp => new ToolRunner(sp.GetRequiredService<ILogger<ToolRunner>>(), sp.GetRequiredService<ResultDelivery>()));
        services.AddSingleton<GetHierarchyUseCase>();
        services.AddSingleton<GetHierarchyParentUseCase>();
        services.AddSingleton<GetSpecialLocationUseCase>();
        services.AddSingleton<GetHyperlinkToObjectUseCase>();
        services.AddSingleton<GetWebHyperlinkToObjectUseCase>();
        services.AddSingleton<GetPageContentUseCase>();
        services.AddSingleton<GetBinaryPageContentUseCase>();
        // find_pages' page-text cache (AD-12) is in this process's memory only, and re-reads search{} once per call.
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsStore>();
            return new PageTextCache(() => settings.Read().Search);
        });
        services.AddSingleton(sp => new FindPagesUseCase(sp.GetRequiredService<IOneNoteGateway>(), sp.GetRequiredService<PageTextCache>(), sp.GetRequiredService<ReadAccess>()));
        services.AddSingleton<FindMetaUseCase>();
        services.AddSingleton<ReadResultUseCase>();
        services.AddSingleton<NavigateToUseCase>();
        services.AddSingleton(sp => new ListGrantsUseCase(sp.GetRequiredService<IOneNoteGateway>(), sp.GetRequiredService<SettingsStore>().Read, TimeProvider.System, sp.GetRequiredService<ReadAccess>()));

        return services
            .AddMcpServer(o => o.ServerInfo = new Implementation { Name = ServerName, Version = Version })
            .WithTools<GetHierarchyTool>(EnvelopeJson.Options)
            .WithTools<GetHierarchyParentTool>(EnvelopeJson.Options)
            .WithTools<GetSpecialLocationTool>(EnvelopeJson.Options)
            .WithTools<GetHyperlinkToObjectTool>(EnvelopeJson.Options)
            .WithTools<GetWebHyperlinkToObjectTool>(EnvelopeJson.Options)
            .WithTools<GetPageContentTool>(EnvelopeJson.Options)
            .WithTools<GetBinaryPageContentTool>(EnvelopeJson.Options)
            .WithTools<FindPagesTool>(EnvelopeJson.Options)
            .WithTools<FindMetaTool>(EnvelopeJson.Options)
            .WithTools<ReadResultTool>(EnvelopeJson.Options)
            .WithTools<NavigateToTool>(EnvelopeJson.Options)
            .WithTools<ListGrantsTool>(EnvelopeJson.Options);
    }

    private static string ReadVersion()
    {
        var assembly = typeof(ServerSetup).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            // Drop the "+<commit>" source-revision suffix the SDK appends.
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
