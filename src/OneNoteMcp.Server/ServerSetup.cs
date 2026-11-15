using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Com;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.LargeResults;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Server.Journal;
using OneNoteMcp.Server.Logging;
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
        services.AddSingleton<IssuedPathCaches>();
        services.AddSingleton(sp => new GetPageContentUseCase(sp.GetRequiredService<IOneNoteGateway>(), sp.GetRequiredService<ReadAccess>(), sp.GetRequiredService<IssuedPathCaches>()));
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

        // Writes (AD-7): the machine-wide write lock (AD-18), logging each hold with IDs and timings only (AD-13); the
        // write-grant check, re-reading settings.json at every check (AD-10); and the step runner.
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("OneNoteMcp.Server.WriteLock");
            return new WriteLock(WriteLock.DefaultName, held => WriteLockLog.Held(logger, held));
        });
        services.AddSingleton(sp => new WriteAccess(sp.GetRequiredService<IOneNoteGateway>(), sp.GetRequiredService<SettingsStore>().Read));
        // Step 1's write-grant dialog (AD-6): it re-reads the open notebooks through the gateway and stores the grant.
        services.AddSingleton(sp => new WriteGrantDialog(sp.GetRequiredService<IOneNoteGateway>(), sp.GetRequiredService<SettingsWriter>(), TimeProvider.System));
        services.AddSingleton(sp => new WritePipeline(
            sp.GetRequiredService<WriteLock>(),
            sp.GetRequiredService<IOneNoteGateway>(),
            sp.GetRequiredService<PageTextCache>(),
            sp.GetRequiredService<SettingsStore>().Read,
            TimeProvider.System,
            dialog: sp.GetRequiredService<WriteGrantDialog>()));
        services.AddSingleton<CreatePageUseCase>();

        // The recovery journal (AD-8): the store re-reads journal{} from settings.json at every write and logs each entry
        // with IDs and sizes only (AD-13); step 6's page snapshot writes through it; the pruner re-reads journal{} at
        // every prune. Pruning itself runs only where AddJournalPruning registers it (the server's own host).
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("OneNoteMcp.Server.Journal");
            return new JournalStore(sp.GetRequiredService<SettingsStore>().Read, written => JournalLog.Written(logger, written));
        });
        services.AddSingleton(sp => new PageSnapshot(sp.GetRequiredService<IOneNoteGateway>(), sp.GetRequiredService<JournalStore>(), TimeProvider.System));
        services.AddSingleton(sp => new JournalPruner(sp.GetRequiredService<SettingsStore>().Read, TimeProvider.System));

        // PageWrite (AD-7): the write grant, the non-prompting read-grant check that decides what messages may name
        // (AD-21), and step 6's snapshot.
        services.AddSingleton(sp => new PageWriteServices(
            sp.GetRequiredService<IOneNoteGateway>(),
            sp.GetRequiredService<WriteAccess>(),
            sp.GetRequiredService<ReadAccess>(),
            sp.GetRequiredService<PageSnapshot>(),
            sp.GetRequiredService<IssuedPathCaches>()));
        services.AddSingleton<UpdatePageContentUseCase>();
        services.AddSingleton<DeletePageContentUseCase>();

        // restore_from_journal (AD-8): a Create that reads one entry from the journal folder settings.json names, re-read
        // at every call.
        services.AddSingleton(sp => new RestoreFromJournalUseCase(
            sp.GetRequiredService<IOneNoteGateway>(),
            sp.GetRequiredService<WriteAccess>(),
            sp.GetRequiredService<WritePipeline>(),
            sp.GetRequiredService<SettingsStore>().Read));

        var builder = services
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
            .WithTools<ListGrantsTool>(EnvelopeJson.Options)
            .WithTools<CreatePageTool>(EnvelopeJson.Options)
            .WithTools<UpdatePageContentTool>(EnvelopeJson.Options)
            .WithTools<DeletePageContentTool>(EnvelopeJson.Options)
            .WithTools<RestoreFromJournalTool>(EnvelopeJson.Options);

        // Story 2.14 (AD-9): every input schema says up front that undeclared names are refused, and every call's
        // arguments are checked against its tool's schema before the SDK binds them.
        CloseInputSchemas(builder.Services);
        return builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => (request, cancellationToken) =>
        {
            // The SDK matches the tool before filters run. No match (an unknown tool) stays the SDK's JSON-RPC error.
            if (request.MatchedPrimitive is McpServerTool tool
                && ArgumentCheck.Check(tool.ProtocolTool.Name, tool.ProtocolTool.InputSchema, request.Params?.Arguments) is { } message)
            {
                var provider = request.Services ?? request.Server.Services
                    ?? throw new InvalidOperationException("The MCP request has no service provider.");
                var refused = provider.GetRequiredService<ToolRunner>().Refuse(
                    tool.ProtocolTool.Name,
                    new OneNoteException(ErrorCode.InvalidArgument, message, retryable: false));
                return ValueTask.FromResult(refused);
            }

            return next(request, cancellationToken);
        }));
    }

    /// <summary>
    /// The journal prune at startup and then daily (AD-8), as a hosted service. <c>Program</c> adds it to the server's
    /// host; test hosts built with <see cref="AddOneNoteMcp"/> leave it out, so they never prune a real journal folder.
    /// </summary>
    public static IServiceCollection AddJournalPruning(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddHostedService(sp => new JournalPruneService(
            sp.GetRequiredService<JournalPruner>(),
            TimeProvider.System,
            sp.GetRequiredService<ILogger<JournalPruneService>>()));
    }

    /// <summary>
    /// Adds <c>"additionalProperties": false</c> to the input schema of every registered tool, so <c>tools/list</c>, the
    /// server's tool collection and <c>GetServices&lt;McpServerTool&gt;()</c> all show it (story 2.14, AD-9).
    /// </summary>
    private static void CloseInputSchemas(IServiceCollection services)
    {
        for (var i = 0; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType != typeof(McpServerTool) || descriptor.IsKeyedService)
            {
                continue;
            }

            if (descriptor.ImplementationInstance is McpServerTool instance)
            {
                CloseInputSchema(instance);
                continue;
            }

            Func<IServiceProvider, object> create = descriptor.ImplementationFactory
                ?? (descriptor.ImplementationType is { } type
                    ? sp => ActivatorUtilities.CreateInstance(sp, type)
                    : throw new InvalidOperationException("An McpServerTool registration has no instance, factory or type."));
            services[i] = new ServiceDescriptor(typeof(McpServerTool), sp => CloseInputSchema((McpServerTool)create(sp)), descriptor.Lifetime);
        }
    }

    private static McpServerTool CloseInputSchema(McpServerTool tool)
    {
        var schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
        Close(schema);
        tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(schema);
        return tool;
    }

    /// <summary>
    /// Closes <paramref name="schema"/> and every object argument inside it that declares its members (story 2.9:
    /// <c>update_page_content</c>'s <c>basis</c>), so a misnamed member is refused rather than ignored. A dictionary
    /// (<c>additionalProperties</c> as a schema, no <c>properties</c>) stays open.
    /// </summary>
    private static void Close(JsonObject schema)
    {
        schema["additionalProperties"] = false;
        if (schema["properties"] is not JsonObject properties)
        {
            return;
        }

        foreach (var (_, property) in properties)
        {
            if (property is JsonObject nested && nested["properties"] is JsonObject && nested["additionalProperties"] is null)
            {
                Close(nested);
            }
        }
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
