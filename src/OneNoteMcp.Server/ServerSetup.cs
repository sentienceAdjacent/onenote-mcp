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
using OneNoteMcp.Core.AppState;
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
        // Backlog 13: the OneDrive folders, read from the registry and environment at most every 30 s, for the protected-by-path
        // rule (reads, writes, snapshots) and the export-root guard (which also refuses a root too close to a OneDrive folder).
        services.AddSingleton(_ => OneDriveAccounts.ForThisUser());
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
        // Entry 15 (Phill, 2026-11-17, Q1): one export-root guard, its verdict cached, shared by reads (a warning) and writes (refused).
        services.AddSingleton(sp => new ExportRootGuard(sp.GetRequiredService<IOneNoteGateway>(), TimeProvider.System, oneDrive: sp.GetRequiredService<OneDriveAccounts>().Current));
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsStore>();
            return new ReadAccess(sp.GetRequiredService<IOneNoteGateway>(), settings.Read, sp.GetRequiredService<SettingsWriter>(), TimeProvider.System, sp.GetRequiredService<ExportRootGuard>(), sp.GetRequiredService<OneDriveAccounts>().Current);
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
        // AD-7's AppState pipeline (stories 4.3, 4.4): no grant, the 120 s deadline, outcomeUnknown on an ambiguous step 7,
        // and the write lock (AD-18) for close_notebook only, waiting up to timeouts.callSeconds.
        services.AddSingleton(sp => new AppStatePipeline(
            sp.GetRequiredService<IOneNoteGateway>(),
            TimeProvider.System,
            writeLock: sp.GetRequiredService<WriteLock>(),
            lockWait: () => sp.GetRequiredService<SettingsStore>().Read().Timeouts.Call));
        services.AddSingleton<NavigateToUseCase>();
        services.AddSingleton<SyncHierarchyUseCase>();
        services.AddSingleton<CloseNotebookUseCase>();
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
            dialog: sp.GetRequiredService<WriteGrantDialog>(),
            guard: sp.GetRequiredService<ExportRootGuard>()));
        // Story 3.14: with parentPageId, create_page checks a protected section's read grant and takes a hierarchy entry.
        services.AddSingleton(sp => new CreatePageUseCase(
            sp.GetRequiredService<IOneNoteGateway>(),
            sp.GetRequiredService<WriteAccess>(),
            sp.GetRequiredService<WritePipeline>(),
            sp.GetRequiredService<ReadAccess>(),
            sp.GetRequiredService<HierarchySnapshot>()));

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
            sp.GetRequiredService<SettingsStore>().Read,
            oneDrive: sp.GetRequiredService<OneDriveAccounts>().Current));

        // Story 5.3: section, section-group and notebook creates (AD-7 Create). A notebook create writes its automatic
        // grant (AD-6 grantedVia: created) through the settings writer, inside the write lock.
        services.AddSingleton(sp => new CreateHierarchyUseCase(
            sp.GetRequiredService<IOneNoteGateway>(),
            sp.GetRequiredService<WriteAccess>(),
            sp.GetRequiredService<WritePipeline>(),
            sp.GetRequiredService<SettingsStore>().Read,
            sp.GetRequiredService<SettingsWriter>(),
            TimeProvider.System));

        // Story 5.4: UpdateHierarchy renames and reorders (AD-7 HierarchyWrite), each with a hierarchy journal entry (AD-8).
        services.AddSingleton(sp => new HierarchySnapshot(sp.GetRequiredService<IOneNoteGateway>(), sp.GetRequiredService<JournalStore>(), TimeProvider.System));
        services.AddSingleton(sp => new HierarchyWriteServices(
            sp.GetRequiredService<IOneNoteGateway>(),
            sp.GetRequiredService<WriteAccess>(),
            sp.GetRequiredService<ReadAccess>(),
            sp.GetRequiredService<HierarchySnapshot>()));
        services.AddSingleton<UpdateHierarchyUseCase>();

        // Story 3.4: a page move (AD-7 HierarchyWrite), one UpdateHierarchy with a hierarchy entry per section (AD-8).
        services.AddSingleton<MoveHierarchyObjectUseCase>();

        // Story 3.5: a restore out of the recycle bin (AD-7 HierarchyWrite, AD-8), one UpdateHierarchy of the target with a
        // hierarchy entry; it reads the soft-delete entries from the journal folder settings.json names, at every call.
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("OneNoteMcp.Server.Journal");
            return new RestoreFromRecycleBinUseCase(
                sp.GetRequiredService<HierarchyWriteServices>(),
                sp.GetRequiredService<WritePipeline>(),
                sp.GetRequiredService<SettingsStore>().Read,
                restoredFromNotSaved: (entryId, why) => JournalLog.RestoredFromNotSaved(logger, entryId, why));
        });

        // Story 3.14: a page's subpage level (AD-7 HierarchyWrite), one UpdateHierarchy of its section with a hierarchy entry.
        services.AddSingleton<SetPageLevelUseCase>();

        // Story 3.6: a merge (AD-7 HierarchyWrite), one MergeSections with a hierarchy entry of the destination (AD-8).
        services.AddSingleton<MergeSectionsUseCase>();

        // Story 5.5: soft delete (AD-7 SoftDelete). A page takes a page snapshot as a page write does; a section or section
        // group takes a hierarchy snapshot (AD-8).
        services.AddSingleton(sp => new SoftDeleteUseCase(
            sp.GetRequiredService<PageWriteServices>(),
            sp.GetRequiredService<HierarchySnapshot>(),
            sp.GetRequiredService<WritePipeline>()));
        // Story 5.6: permanent deletes (AD-7 PermanentDelete, 600 s deadline). A section is backed up with Publish into the
        // journal's backups folder, waited for on the system clock; an empty section group gets a hierarchy entry (5.5's snapshot).
        services.AddSingleton(sp => new SectionBackup(
            sp.GetRequiredService<IOneNoteGateway>(),
            sp.GetRequiredService<JournalStore>(),
            sp.GetRequiredService<SettingsStore>().Read,
            TimeProvider.System));
        services.AddSingleton(sp => new PermanentDeleteServices(
            sp.GetRequiredService<IOneNoteGateway>(),
            sp.GetRequiredService<WriteAccess>(),
            sp.GetRequiredService<ReadAccess>(),
            sp.GetRequiredService<PageSnapshot>(),
            sp.GetRequiredService<HierarchySnapshot>(),
            sp.GetRequiredService<SectionBackup>()));
        services.AddSingleton<PermanentDeleteUseCase>();

        // Story 3.3: publish (AD-7 ExternalWrite, 600 s deadline): a page as PDF under an export root, re-reading
        // exportRoots and timeouts.publishSeconds from settings.json at every call; the landing wait runs on the system clock.
        services.AddSingleton(sp => new PublishUseCase(
            sp.GetRequiredService<IOneNoteGateway>(),
            sp.GetRequiredService<ReadAccess>(),
            sp.GetRequiredService<SettingsStore>().Read,
            sp.GetRequiredService<WritePipeline>(),
            TimeProvider.System));

        // Story 3.8: open_package (AD-7 ExternalWrite): unpack a package into the opened-copies folder, outside OneDrive
        // (Phill's Q3 (a)), open it, wait for its pages.
        services.AddSingleton(sp => new OpenPackageUseCase(
            sp.GetRequiredService<IOneNoteGateway>(),
            sp.GetRequiredService<SettingsStore>().Read,
            sp.GetRequiredService<WritePipeline>(),
            TimeProvider.System,
            oneDrive: sp.GetRequiredService<OneDriveAccounts>().Current));

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
            .WithTools<SyncHierarchyTool>(EnvelopeJson.Options)
            .WithTools<CloseNotebookTool>(EnvelopeJson.Options)
            .WithTools<ListGrantsTool>(EnvelopeJson.Options)
            .WithTools<CreatePageTool>(EnvelopeJson.Options)
            .WithTools<UpdatePageContentTool>(EnvelopeJson.Options)
            .WithTools<DeletePageContentTool>(EnvelopeJson.Options)
            .WithTools<RestoreFromJournalTool>(EnvelopeJson.Options)
            .WithTools<CreateSectionTool>(EnvelopeJson.Options)
            .WithTools<CreateSectionGroupTool>(EnvelopeJson.Options)
            .WithTools<CreateNotebookTool>(EnvelopeJson.Options)
            .WithTools<RenameHierarchyObjectTool>(EnvelopeJson.Options)
            .WithTools<ReorderHierarchyChildrenTool>(EnvelopeJson.Options)
            .WithTools<MoveHierarchyObjectTool>(EnvelopeJson.Options)
            .WithTools<RestoreFromRecycleBinTool>(EnvelopeJson.Options)
            .WithTools<SetPageLevelTool>(EnvelopeJson.Options)
            .WithTools<MergeSectionsTool>(EnvelopeJson.Options)
            .WithTools<DeleteHierarchyObjectTool>(EnvelopeJson.Options)
            .WithTools<PermanentlyDeleteHierarchyObjectTool>(EnvelopeJson.Options)
            .WithTools<PublishTool>(EnvelopeJson.Options)
            .WithTools<OpenPackageTool>(EnvelopeJson.Options);

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
    /// Story 3.8 (Phill's cleanup answer (b)): one sweep of the opened-copies folder when the server starts. <c>Program</c>
    /// adds it to the server's host; test hosts leave it out, so they never delete a real copy.
    /// </summary>
    public static IServiceCollection AddOpenedCopiesSweep(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddHostedService(sp => new OpenedCopiesSweepService(
            sp.GetRequiredService<IOneNoteGateway>(),
            sp.GetRequiredService<SettingsStore>(),
            TimeProvider.System,
            sp.GetRequiredService<ILogger<OpenedCopiesSweepService>>()));
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
