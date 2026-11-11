using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using OneNoteMcp.Com;
using OneNoteMcp.Com.Interop;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Xml;
using Xunit;
using CoreScope = OneNoteMcp.Core.HierarchyScope;
using PageInfo = OneNoteMcp.Com.Interop.PageInfo;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Shared scaffolding for story 2.2's probe scenarios: the gateway, the recorder, guarded writes, reads, the step
/// filter and the hitl pause.
/// <para>
/// <b>Hitl pause.</b> A step that needs Phill writes <c>%LOCALAPPDATA%\OneNoteMcp-Test\probes\pause.txt</c> (the
/// instruction) and waits. The main session relays it, then deletes the file to continue, or overwrites it with
/// <c>skip</c> to skip that step. The wait ends after <c>ONENOTE_MCP_PROBE_PAUSE_SECONDS</c> (default 1800).
/// </para>
/// <para>
/// <b>Step filter.</b> <c>ONENOTE_MCP_PROBE_STEPS</c>, a comma-separated list of step names, runs only those steps
/// (for a re-run). Unset runs every step.
/// </para>
/// </summary>
internal sealed class ProbeSession : IDisposable
{
    public const string PauseFileName = "pause.txt";
    public const string ProbesSectionName = "Probes";

    public static readonly XNamespace One = OneNoteXml.Namespace2013;
    public static readonly XNamespace One2026 = "http://schemas.microsoft.com/office/onenote/2026/onenote";
    public static readonly XNamespace One2010 = "http://schemas.microsoft.com/office/onenote/2010/onenote";

    private readonly string settingsDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-probes", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper output;
    private readonly HashSet<string>? steps;
    private readonly List<string> errors = [];

    public ProbeSession(string scenario, ITestOutputHelper output)
    {
        this.output = output;
        Scenario = scenario;
        Directory.CreateDirectory(settingsDir);
        var settings = Path.Combine(settingsDir, "settings.json");
        // A day's idle release: any second activation is then a reconnect, which the breaker refuses.
        File.WriteAllText(settings, """{ "schemaVersion": 1, "allowSilentActivation": false, "timeouts": { "idleReleaseSeconds": 86400 } }""");
        Probe = new PinnedOneNoteProbe();
        Assert.True(Probe.HasOneNote, "OneNote desktop must already be running.");
        Gateway = new ComGateway(Probe, new SettingsStore(settings));
        Recorder = new ProbeRecorder(scenario, Echo);

        var filter = Environment.GetEnvironmentVariable("ONENOTE_MCP_PROBE_STEPS");
        if (!string.IsNullOrWhiteSpace(filter))
        {
            steps = new HashSet<string>(filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);
        }

        Echo($"Probe log: {Recorder.Path}");
    }

    public string Scenario { get; }

    public ComGateway Gateway { get; }

    public PinnedOneNoteProbe Probe { get; }

    public ProbeRecorder Recorder { get; }

    public IReadOnlyList<string> Errors => errors;

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Opt-in gate: <c>ONENOTE_MCP_INTEGRATION=1</c> and <c>ONENOTE_MCP_SCENARIO=<paramref name="scenario"/></c>.</summary>
    public static void RequireScenario(string scenario)
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("ONENOTE_MCP_SCENARIO") == scenario,
            $"Hitl probe scenario: set ONENOTE_MCP_SCENARIO={scenario}, with Phill present.");
    }

    /// <summary>
    /// Runs one step unless the step filter leaves it out. An exception is recorded and the next step runs, except
    /// when the crash breaker (<see cref="PinnedOneNoteProbe"/>) has tripped: then the scenario stops.
    /// </summary>
    public async Task StepAsync(string step, Func<Task> body)
    {
        if (steps is not null && !steps.Contains(step))
        {
            return;
        }

        StopIfTripped(step);
        Echo($"--- step {step}");
        try
        {
            await body();
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or ProbeStoppedException))
        {
            errors.Add($"{step}: {ex.GetType().Name}: {ex.Message}");
            Recorder.Row(step, "step-error", "step", null, null, new { type = ex.GetType().Name, message = ex.Message });
        }

        StopIfTripped(step);
    }

    private void StopIfTripped(string step)
    {
        if (Probe.Tripped is { } reason)
        {
            Recorder.Row(step, "crash-breaker", "stop", null, null, new { reason, hresults = Recorder.HResults.ToDictionary(p => p.Key, p => p.Value.ToArray()) });
            throw new ProbeStoppedException($"Stopped at {step}: {reason}. Check OneNote before re-running.");
        }
    }

    /// <summary>Last row of a scenario: every HRESULT seen, with its probes. Then fails if any step threw.</summary>
    public void Finish()
    {
        Recorder.Row("hresult-summary", "HRESULTs", "summary", null, null, Recorder.HResults.ToDictionary(p => p.Key, p => p.Value.ToArray()));
        Assert.True(errors.Count == 0, "Probe steps failed (see the log):\n" + string.Join('\n', errors));
    }

    // --- Calls ----------------------------------------------------------------------------------------------------

    /// <summary>A guarded write (<see cref="TestNotebook.WriteAsync{T}"/>), as an <see cref="Attempt"/>; never throws for a COM error or a refusal.</summary>
    public Task<Attempt> TryWriteAsync(string targetId, string method, Action<IApplication> call) =>
        TryAsync(() => TestNotebook.WriteAsync(
            Gateway,
            targetId,
            app =>
            {
                call(app);
                return true;
            },
            method,
            Ct));

    /// <summary>A read through the gateway's seam, as an <see cref="Attempt"/>.</summary>
    public Task<Attempt> TryReadAsync(string method, Action<IApplication> call) =>
        TryAsync(() => Gateway.InvokeAsync(
            app =>
            {
                call(app);
                return true;
            },
            Ct,
            retryOnDisconnect: true,
            method: method));

    public static async Task<Attempt> TryAsync(Func<Task> call)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await call();
            return new Attempt("ok", null, null, null, stopwatch.ElapsedMilliseconds, null);
        }
        catch (OneNoteException ex)
        {
            var hr = ex.ComHResult is { } h ? string.Create(CultureInfo.InvariantCulture, $"0x{h:x8}") : null;
            var name = ex.ComHResult is { } n ? HResultMap.Name(n) : null;
            return new Attempt("error", hr, name, ex.Code.ToString(), stopwatch.ElapsedMilliseconds, ex.Message);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Refusing", StringComparison.Ordinal))
        {
            return new Attempt("refused", null, null, null, stopwatch.ElapsedMilliseconds, ex.Message);
        }
    }

    public Task<Attempt> UpdateAsync(string pageId, string xml, DateTime expected, XMLSchema schema = XMLSchema.xs2013, string method = "UpdatePageContent") =>
        TryWriteAsync(pageId, method, app => app.UpdatePageContent(xml, expected, schema, force: false));

    public Task<Attempt> DeleteContentAsync(string pageId, string objectId, DateTime expected, string method = "DeletePageContent") =>
        TryWriteAsync(pageId, method, app => app.DeletePageContent(pageId, objectId, expected, force: false));

    /// <summary>An update that must succeed (probe setup). Throws on failure.</summary>
    public async Task MustUpdateAsync(string pageId, string body)
    {
        var attempt = await UpdateAsync(pageId, ProbeXml.Page(pageId, body), DateTime.MinValue, method: "UpdatePageContent(setup)");
        if (!attempt.Ok)
        {
            throw new InvalidOperationException($"Setup update failed: {attempt.Outcome} {attempt.HResult} {attempt.Message}");
        }
    }

    /// <summary>A new page in <paramref name="sectionId"/>, through the guard, with <paramref name="title"/>.</summary>
    public async Task<string> CreatePageAsync(string sectionId, string title)
    {
        var pageId = await TestNotebook.WriteAsync(
            Gateway,
            sectionId,
            app =>
            {
                app.CreateNewPage(sectionId, out var id, Com.Interop.NewPageStyle.npsBlankPageWithTitle);
                return id;
            },
            "CreateNewPage(probe)",
            Ct);
        await MustUpdateAsync(pageId, ProbeXml.Title(title));
        return pageId;
    }

    // --- Reads ----------------------------------------------------------------------------------------------------

    public async Task<string> PageXmlAsync(string pageId, XMLSchema schema = XMLSchema.xs2013, PageInfo info = PageInfo.piBasic) =>
        await Gateway.InvokeAsync(
            app =>
            {
                app.GetPageContent(pageId, out var xml, info, schema);
                return xml;
            },
            Ct,
            method: "GetPageContent(probe)");

    public async Task<XElement> PageAsync(string pageId, XMLSchema schema = XMLSchema.xs2013, PageInfo info = PageInfo.piBasic) =>
        XDocument.Parse(await PageXmlAsync(pageId, schema, info)).Root!;

    public async Task<XElement> NotebooksAsync() =>
        XDocument.Parse(await Gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, Ct)).Root!;

    public async Task<XElement> TreeAsync(string nodeId, CoreScope scope = CoreScope.Pages) =>
        XDocument.Parse(await Gateway.GetHierarchyAsync(nodeId, scope, Ct)).Root!;

    /// <summary>The page's element in a fresh pages-scope read of its section.</summary>
    public async Task<XElement> HierarchyPageAsync(string sectionId, string pageId) =>
        ProbeXml.ById(await TreeAsync(sectionId), pageId)
            ?? throw new InvalidOperationException($"Page {pageId} is not in section {sectionId}.");

    /// <summary>MCP Test's ID (it must exist: run the unattended suite first).</summary>
    public async Task<string> McpTestIdAsync()
    {
        var folder = TestNotebook.ResolveFolder(Environment.GetEnvironmentVariable);
        var notebooks = await Gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, Ct);
        var id = TestNotebookGuard.FindNotebookId(notebooks, folder);
        Assert.SkipWhen(id is null, $"MCP Test is not open at {folder}. Run the unattended suite once first.");
        return id!;
    }

    /// <summary>MCP Test Cloud's ID; skips when it isn't open (it's never created).</summary>
    public async Task<string> CloudIdAsync()
    {
        var id = await TestNotebook.FindCloudAsync(Gateway, Ct);
        Assert.SkipWhen(id is null, "MCP Test Cloud is not open in OneNote. Phill creates and opens it; the suite never does.");
        return id!;
    }

    /// <summary>
    /// The ID of the section named <paramref name="name"/> directly in the notebook. If it's missing, asks Phill to
    /// create it in OneNote (the probes never call <c>OpenHierarchy</c>).
    /// </summary>
    public async Task<string> SectionIdAsync(string notebookId, string notebookName, string name)
    {
        var id = TestNotebookGuard.FindSectionId((await TreeAsync(notebookId, CoreScope.Sections)).ToString(), name);
        if (id is null)
        {
            Assert.True(
                await PauseAsync($"create-section-{name}", $"In OneNote, create a section named \"{name}\" in the notebook \"{notebookName}\". Then continue."),
                $"Section {name} is needed.");
            id = TestNotebookGuard.FindSectionId((await TreeAsync(notebookId, CoreScope.Sections)).ToString(), name);
        }

        return id ?? throw new InvalidOperationException($"No section named {name} in {notebookName}.");
    }

    /// <summary>The non-recycle-bin page titled <paramref name="title"/> in the section, or null.</summary>
    public async Task<string?> PageIdByTitleAsync(string sectionId, string title) =>
        (await TreeAsync(sectionId)).Elements(One + "Page")
            .Where(p => (string?)p.Attribute("name") == title && (string?)p.Attribute("isInRecycleBin") != "true")
            .Select(p => (string?)p.Attribute("ID"))
            .FirstOrDefault();

    // --- Hitl -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes the instruction to the pause file and waits until the main session deletes it (true: done) or writes
    /// <c>skip</c> into it (false: skip the step).
    /// </summary>
    public async Task<bool> PauseAsync(string step, string instruction)
    {
        var file = Path.Combine(ProbeRecorder.ProbesDir, PauseFileName);
        var text = $"[{Scenario} / {step}]\n{instruction}\n\nWhen done, delete this file. To skip this step, replace this file's content with: skip\n";
        await File.WriteAllTextAsync(file, text, Ct);
        Recorder.Row(step, "hitl", "pause", null, null, new { instruction });
        Echo($"PAUSE ({file}): {instruction}");
        Console.WriteLine($"PAUSE [{Scenario} / {step}]: {instruction}");

        var limit = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable("ONENOTE_MCP_PROBE_PAUSE_SECONDS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 1800);
        var waited = Stopwatch.StartNew();
        while (true)
        {
            if (!File.Exists(file))
            {
                Recorder.Row(step, "hitl", "resume", null, null, new { waitedMs = waited.ElapsedMilliseconds });
                return true;
            }

            string content;
            try
            {
                content = await File.ReadAllTextAsync(file, Ct);
            }
            catch (IOException)
            {
                content = string.Empty;
            }

            if (content.Trim().Equals("skip", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
                Recorder.Row(step, "hitl", "skipped", null, null, new { waitedMs = waited.ElapsedMilliseconds });
                return false;
            }

            if (waited.Elapsed > limit)
            {
                File.Delete(file);
                throw new TimeoutException($"No answer to the pause for {step} within {limit.TotalSeconds} s.");
            }

            await Task.Delay(500, Ct);
        }
    }

    /// <summary>The crash breaker tripped; the scenario must not make another COM call.</summary>
    internal sealed class ProbeStoppedException(string message) : Exception(message);

    public void Echo(string line)
    {
        try
        {
            output.WriteLine(ProbeRecorder.Redact(line));
        }
        catch (InvalidOperationException)
        {
            // No test is running (the output helper is gone).
        }
    }

    public void Dispose()
    {
        Gateway.Dispose();
        Recorder.Dispose();
        try
        {
            Directory.Delete(settingsDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }
}
