using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using ModelContextProtocol.Client;
using OneNoteMcp.Com;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Settings;
using StressHarness;

// Spike 1.15. Read-only, content-free: see README.md. Nothing printed or written here may carry notebook content, a
// notebook name, a path or an exception message; only IDs, numbers, codes, HRESULTs and exception type names.
try
{
    return await MainAsync(args);
}
catch (RefusalException ex)
{
    Console.Error.WriteLine("refused: " + ex.Message);
    if (ex.ExitCode == Exit.Usage)
    {
        Console.Error.WriteLine(Options.Usage);
    }

    return ex.ExitCode;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"crashed: {ex.GetType().Name} (0x{ex.HResult:X8})");
    return Exit.Crashed;
}

static async Task<int> MainAsync(string[] args)
{
    var options = Options.Parse(args);

    if (options.ProbeNavigate)
    {
        return ProbeNavigate.Run(options.NotebookId!, options.ProbePage);
    }

    if (options.ProbeSync)
    {
        return ProbeSync.Run(options.NotebookId!, options.ProbePage);
    }

    var contract = ContractToolNames();
    if (options.FakeServerChars is { } chars)
    {
        return await FakeServer.RunAsync(chars, options.FakeVariant, options.FakeFiller, contract);
    }

    // --out must resolve outside the repo, checked before anything starts. The diag modes write no files.
    var outDir = options.IsDiag ? "" : Path.GetFullPath(options.Out!);
    var roots = RepoPaths.Roots();
    if (!options.IsDiag && roots.Any(r => RepoPaths.IsInside(outDir, r)))
    {
        throw new RefusalException(Exit.Usage, "--out resolves inside the repo; choose a folder outside it");
    }

    var exe = options.Exe ?? (roots.Count > 0 ? Path.Combine(roots[0], "dist", "publish", "OneNoteMcp.exe") : null);
    if (exe is null || !File.Exists(exe))
    {
        throw new RefusalException(Exit.Usage, "the server exe was not found; run scripts/pack-plugin.ps1 or pass --exe");
    }

    exe = Path.GetFullPath(exe);

    var serverStderrLines = 0;

    // Story 1.20 (AD-21): the server under test reads its own temporary settings, so no read grant the user stored
    // applies and every password-protected section is withheld, which the run expects. Deleted with settingsDir.
    var settingsDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-stress", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(settingsDir);
    var serverSettings = Path.Combine(settingsDir, "server-settings.json");
    File.WriteAllText(serverSettings, $"{{\"schemaVersion\": 1, \"results\": {{\"inlineMaxChars\": {Math.Clamp(options.InlineMax, 20_000, 380_000)}}}}}");
    var transport = new StdioClientTransport(new StdioClientTransportOptions
    {
        Name = "onenote",
        Command = exe,
        Arguments = [.. options.ExeArgs],
        EnvironmentVariables = new Dictionary<string, string?> { ["ONENOTE_MCP_SETTINGS_PATH"] = serverSettings },
        // The server's own log is content-free, but the harness keeps nothing of it beyond a line count.
        StandardErrorLines = _ => Interlocked.Increment(ref serverStderrLines),
    });

    await using var client = await McpClient.CreateAsync(transport);

    // Startup check, before any read: exactly the contract tools (fourteen since story 2.9 added update_page_content), each
    // readOnlyHint: true except navigate_to, the one user-visible tool, and the write tools, none of which the harness
    // ever calls (ToolClient refuses them).
    var listed = await client.ListToolsAsync();
    var names = listed.Select(t => t.Name).ToList();
    var unexpected = names.Count(n => !contract.Contains(n, StringComparer.Ordinal));
    var missing = contract.Count(n => !names.Contains(n, StringComparer.Ordinal));
    var notReadOnly = listed.Count(t => !ToolClient.MayWrite(t.Name) && t.ProtocolTool.Annotations?.ReadOnlyHint != true);
    if (unexpected > 0 || missing > 0 || notReadOnly > 0 || names.Count != contract.Count)
    {
        Console.Error.WriteLine($"refused: tools/list is not exactly the {contract.Count} contract tools, each with readOnlyHint but {ToolClient.NavigateTo} and the write tools (listed {names.Count}, unexpected {unexpected}, missing {missing}, not read-only {notReadOnly})");
        return Exit.UnexpectedTools;
    }

    var probe = new ProcessProbe();
    if (!probe.IsOneNoteRunning())
    {
        // Never start OneNote: COM activation with it closed would start a windowless instance.
        throw new RefusalException(Exit.Precondition, "OneNote is not running; start OneNote desktop first");
    }

    try
    {
        if (options.IsDiag)
        {
            using var diagGateway = new ComGateway(probe, new SettingsStore(Path.Combine(settingsDir, "settings.json")));
            return options.DiagPage is { } page
                ? await Diag.PageAsync(diagGateway, new ToolClient(client), page)
                : await Diag.CallbacksAsync(diagGateway, await ResolveNotebookAsync(diagGateway, options));
        }

        return await RunAsync(options, client, exe, probe, settingsDir, outDir, () => serverStderrLines);
    }
    finally
    {
        try
        {
            if (Directory.Exists(settingsDir))
            {
                Directory.Delete(settingsDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

static async Task<int> RunAsync(Options options, McpClient client, string exe, ProcessProbe probe, string settingsDir, string outDir, Func<int> serverStderrLines)
{
    using var gateway = new ComGateway(probe, new SettingsStore(Path.Combine(settingsDir, "settings.json")));

    var notebookId = await ResolveNotebookAsync(gateway, options);
    Directory.CreateDirectory(outDir);
    using var recorder = new Recorder(outDir) { InlineMax = options.InlineMax };
    recorder.Build["serverVersion"] = client.ServerInfo?.Version ?? "";
    using (var exeStream = File.OpenRead(exe))
    {
        recorder.Build["serverExeSha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(exeStream));
    }

    recorder.Build["serverExeLastWriteUtc"] = File.GetLastWriteTimeUtc(exe).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    var com = typeof(ComGateway).Assembly;
    recorder.Build["oracleComVersion"] =
        com.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
        ?? com.GetName().Version?.ToString() ?? "";
    var run = new Run(new ToolClient(client), gateway, recorder, options, notebookId);
    var sw = Stopwatch.StartNew();
    string? abortedBy = null;
    try
    {
        await run.ExecuteAsync();
    }
    catch (RefusalException ex)
    {
        abortedBy = run.Phase + ":refused";
        Console.Error.WriteLine("aborted: " + ex.Message);
    }
    catch (Exception ex)
    {
        abortedBy = $"{run.Phase}:{ex.GetType().Name}";
        Console.Error.WriteLine($"aborted in phase {run.Phase}: {ex.GetType().Name} (0x{ex.HResult:X8})");
    }

    sw.Stop();
    recorder.NotebookCounts["serverStderrLines"] = serverStderrLines();
    recorder.WriteSummary(notebookId, options, sw.ElapsedMilliseconds, abortedBy);
    var mismatches = recorder.Records.Count(r => r.Match == Match.Mismatch);
    var changed = recorder.Records.Count(r => r.Match == Match.ChangedDuringRun);
    var unstable = recorder.Records.Count(r => r.Match == Match.UnstableRead);
    var withTool = recorder.Records.Where(r => r.ToolResult is not null).ToList();
    var maxText = withTool.Select(r => r.ToolResult!.MaxTextChars).DefaultIfEmpty(0).Max();
    var overInline = withTool.Count(r => r.ToolResult!.MaxTextChars > options.InlineMax);
    var stored = withTool.Count(r => r.ToolResult!.Stored);
    Console.Error.WriteLine($"done: {recorder.Records.Count} comparisons, {mismatches} mismatches, {changed} changedDuringRun, {unstable} unstableRead, {sw.ElapsedMilliseconds} ms");
    Console.Error.WriteLine($"large results: {stored} stored and reassembled through read_result, max text block {maxText} chars, {overInline} over inlineMaxChars {options.InlineMax}");

    // AD-20: a text block over the threshold is a failure, like a mismatch.
    return abortedBy is not null ? Exit.Crashed : mismatches > 0 || overInline > 0 ? Exit.Mismatches : Exit.Ok;
}

static async Task<string> ResolveNotebookAsync(ComGateway gateway, Options options)
{
    string xml;
    try
    {
        xml = await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, CancellationToken.None);
    }
    catch (OneNoteException ex)
    {
        throw new RefusalException(Exit.Precondition, $"the notebook list could not be read ({ex.Code})");
    }

    var notebooks = XmlFacts.Parse(xml).Descendants(XmlFacts.One + "Notebook").ToList();
    List<XElement> matches = options.NotebookId is { } id
        ? [.. notebooks.Where(n => (string?)n.Attribute("ID") == id)]
        : [.. notebooks.Where(n =>
            string.Equals((string?)n.Attribute("nickname"), options.NotebookName, StringComparison.OrdinalIgnoreCase)
            || string.Equals((string?)n.Attribute("name"), options.NotebookName, StringComparison.OrdinalIgnoreCase))];
    if (matches.Count != 1)
    {
        // Never echo the name, and never list the notebooks.
        throw new RefusalException(Exit.Usage, "no single notebook matched");
    }

    return (string)matches[0].Attribute("ID")!;
}

static IReadOnlyList<string> ContractToolNames()
{
    using var stream = typeof(Options).Assembly.GetManifestResourceStream("tool-contract.json")
        ?? throw new InvalidOperationException("tool-contract.json is not embedded");
    using var doc = JsonDocument.Parse(stream);
    return [.. doc.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!)];
}

