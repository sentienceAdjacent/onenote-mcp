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

    /// <summary>
    /// Story 5.1's per-call timeout, for every call of its scenarios, <c>DeleteHierarchy</c> and <c>Publish</c>
    /// included. Run 1 (2026-11-11, <c>write-probes-hierarchy-local-20261111-033107</c>) had a call block about 3 minutes
    /// behind OneNote's "OneNote is syncing changes" dialog; with 60 s, such a call becomes a <c>Timeout</c> row and the
    /// scenario stops (<see cref="StopOnTimeoutAsync"/>). The probes' backups are small, so 60 s is ample for
    /// <c>Publish</c> here (AD-3's 300 s is the product's limit, not the probes').
    /// </summary>
    public const int ProbeCallSeconds = 60;

    /// <summary>
    /// The per-call limit for the cloud scenarios. Run write-probes-hierarchy-cloud-20261111-165147: each section create
    /// on MCP Test Cloud waited on a OneDrive round-trip ("OneNote is syncing changes" flashed and closed by itself),
    /// taking 6 s, 8 s, 16 s, then 60 s; the 60 s one timed out yet the section was created that same second.
    /// </summary>
    public const int CloudProbeCallSeconds = 180;

    /// <summary>
    /// The cloud-jam repro's R5 limit: shorter than any cloud hierarchy create seen so far (3.4 s at the fastest), so the
    /// create times out mid-call, as s3 did at 60 s in run write-probes-hierarchy-cloud-20261111-165147.
    /// </summary>
    public const int TimeoutReproCallSeconds = 2;

    public static readonly XNamespace One = OneNoteXml.Namespace2013;
    public static readonly XNamespace One2026 = "http://schemas.microsoft.com/office/onenote/2026/onenote";
    public static readonly XNamespace One2010 = "http://schemas.microsoft.com/office/onenote/2010/onenote";

    private readonly string settingsDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-probes", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper output;
    private readonly HashSet<string>? steps;
    private readonly List<string> errors = [];

    /// <param name="callSeconds">
    /// The gateway's per-call timeout. Null keeps the 30 s default (story 2.2's scenarios). Story 5.1's scenarios pass
    /// <see cref="ProbeCallSeconds"/>, which also turns on <see cref="StopOnBlockedCalls"/>.
    /// </param>
    public ProbeSession(string scenario, ITestOutputHelper output, int? callSeconds = null)
    {
        this.output = output;
        Scenario = scenario;
        Directory.CreateDirectory(settingsDir);
        var settings = Path.Combine(settingsDir, "settings.json");
        // A day's idle release: any second activation is then a reconnect, which the breaker refuses.
        var call = callSeconds is { } c ? string.Create(CultureInfo.InvariantCulture, $"\"callSeconds\": {c}, ") : string.Empty;
        File.WriteAllText(settings, $$"""{ "schemaVersion": 1, "allowSilentActivation": false, "timeouts": { {{call}}"idleReleaseSeconds": 86400 } }""");
        Probe = new PinnedOneNoteProbe();
        Assert.True(Probe.HasOneNote, "OneNote desktop must already be running.");
        Gateway = new ComGateway(Probe, new SettingsStore(settings));
        Recorder = new ProbeRecorder(scenario, Echo);

        var filter = Environment.GetEnvironmentVariable("ONENOTE_MCP_PROBE_STEPS");
        if (!string.IsNullOrWhiteSpace(filter))
        {
            steps = new HashSet<string>(filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);
        }

        CallSeconds = callSeconds;
        StopOnBlockedCalls = callSeconds is ProbeCallSeconds or CloudProbeCallSeconds or TimeoutReproCallSeconds;
        Echo($"Probe log: {Recorder.Path}");
    }

    public string Scenario { get; }

    /// <summary>The gateway's per-call timeout this session was built with (null: the 30 s default).</summary>
    public int? CallSeconds { get; }

    /// <summary>
    /// Story 5.1: a call that blocked (a <c>Timeout</c>, or a <c>ComBusy</c> after retrying to the deadline) stops the
    /// scenario cleanly (<see cref="StopOnTimeoutAsync"/>) instead of letting the next step call OneNote again.
    /// </summary>
    public bool StopOnBlockedCalls { get; }

    /// <summary>The step running now (for rows written from inside a call, such as a timeout or a shown check).</summary>
    public string CurrentStep { get; private set; } = "setup";

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
        CurrentStep = step;
        try
        {
            await body();
        }
        catch (OneNoteException ex) when (StopOnBlockedCalls && ex.Code is ErrorCode.Timeout or ErrorCode.ComBusy)
        {
            // A read or setup call blocked (story 5.1): stop cleanly rather than let the next step call OneNote again.
            await StopOnTimeoutAsync("call", new Attempt("error", null, null, ex.Code.ToString(), 0, ex.Message));
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

    // --- Story 5.1: timeouts and pages on screen ------------------------------------------------------------------

    /// <summary>
    /// A call blocked: it ran past <see cref="ProbeCallSeconds"/> (<c>Timeout</c>; the gateway has then abandoned its STA
    /// thread, so the next call would need a second activation), or kept being refused until the deadline
    /// (<c>ComBusy</c>). The scenario stops here on purpose. First the breaker is tripped, so no further COM call is
    /// made and no second OneNote can start, even if the pause below times out. Then a <c>call-timeout</c> row (the
    /// step, the method, the attempt), a pause so Phill can clear OneNote's dialog and say what he saw, and
    /// <see cref="ProbeStoppedException"/>.
    /// </summary>
    public async Task StopOnTimeoutAsync(string method, Attempt attempt)
    {
        Probe.Trip($"a call blocked at {CurrentStep} ({method})");
        Recorder.Row($"{CurrentStep}-call-timeout", "Failure modes", method, new { step = CurrentStep }, attempt, new
        {
            timeoutSeconds = CallSeconds,
            note = "the call blocked past the probe timeout; in run write-probes-hierarchy-local-20261111-033107 a call blocked about 3 minutes behind OneNote's 'OneNote is syncing changes' dialog",
        });
        await PauseAsync(
            $"{CurrentStep}-timeout",
            "A probe call blocked and the scenario is stopping. In OneNote, if a \"OneNote is syncing changes\" dialog (or any other dialog) is open, press Cancel, "
            + "then check OneNote responds. Tell the team lead what dialog you saw, if any. Don't close OneNote. Then continue.");
        throw new ProbeStoppedException($"Stopped at {CurrentStep}: {method} blocked ({attempt.Code}, {CallSeconds} s timeout). See the call-timeout row.");
    }

    /// <summary>
    /// True for a call that blocked: <c>Timeout</c>, or <c>ComBusy</c> that took close to the whole timeout (the gateway
    /// retries a busy OneNote until just before the deadline).
    /// </summary>
    internal static bool Blocked(Attempt attempt, int callSeconds = ProbeCallSeconds) =>
        attempt.Code == nameof(ErrorCode.Timeout)
        || (attempt.Code == nameof(ErrorCode.ComBusy) && attempt.Ms >= (callSeconds - 10) * 1000L);

    /// <summary>Stops the scenario (<see cref="StopOnTimeoutAsync"/>) when <paramref name="attempt"/> blocked; else returns it.</summary>
    public async Task<Attempt> CheckTimeoutAsync(string method, Attempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (StopOnBlockedCalls && Blocked(attempt, CallSeconds ?? ProbeCallSeconds))
        {
            await StopOnTimeoutAsync(method, attempt);
        }

        return attempt;
    }

    /// <summary>
    /// Scenario setup outside any step (finding the notebook, the run group): a blocked call there stops the scenario
    /// the same way.
    /// </summary>
    public async Task<T> SetupAsync<T>(string step, Func<Task<T>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        CurrentStep = step;
        try
        {
            return await body();
        }
        catch (OneNoteException ex) when (StopOnBlockedCalls && ex.Code is ErrorCode.Timeout or ErrorCode.ComBusy)
        {
            await StopOnTimeoutAsync("call", new Attempt("error", null, null, ex.Code.ToString(), 0, ex.Message));
            throw; // not reached: StopOnTimeoutAsync throws
        }
    }

    /// <summary>A probe call through <see cref="TryAsync"/>, stopping the scenario if it timed out.</summary>
    public async Task<Attempt> TryGuardedAsync(string method, Func<Task> call) => await CheckTimeoutAsync(method, await TryAsync(call));

    /// <summary>
    /// Off while a modal probe holds a dialog open: its calls must run with the section on screen, and Phill can't
    /// click elsewhere until the dialog closes.
    /// </summary>
    public bool ShownCheckOff { get; set; }

    /// <summary>
    /// Before a hierarchy-level create, update, delete or publish: nothing in the whole notebook that holds
    /// <paramref name="targetId"/> may be on screen (<c>isCurrentlyViewed</c>). If something is, Phill clicks a page in
    /// the other test notebook at a pause. Run 1 (2026-11-11 03:31) met "OneNote is syncing changes" creating a section
    /// beside a shown page; the retry (15:09) met it creating the run group at MCP Test's root while MCP Test > Seed was
    /// shown, so checking only the probe area wasn't enough. Returns false when something is still shown (the caller then
    /// doesn't make the call).
    /// </summary>
    public async Task<bool> EnsureNothingShownAsync(string targetId, string method, bool pageTargetsExempt = false)
    {
        if (ShownCheckOff)
        {
            return true;
        }

        var (_, root, target) = await ProbeAreaAsync(targetId);
        if (root is null || (pageTargetsExempt && target?.Name.LocalName == "Page"))
        {
            return true; // page-level deletes keep their shown page (the date probes need it)
        }

        var shown = Shown(root);
        var areaName = (string?)root.Attribute("name");
        var targetName = (string?)target?.Attribute("name");
        if (shown.Count == 0)
        {
            // Evidence for the findings doc that the call ran with nothing of the area on screen.
            Recorder.Row($"{CurrentStep}-shown-check", "Setup", "GetHierarchy", new { method, target = targetName, area = areaName }, null, new { shown });
            return true;
        }

        var safe = SafePage(cloud: (string?)root.Attribute("name") == TestNotebook.CloudNotebookName);
        var step = $"{CurrentStep}-show-elsewhere";
        await PauseAsync(
            step,
            $"In OneNote, click the page {safe}, so that nothing of the notebook \"{areaName}\" is on screen. Don't type. Then continue. "
            + "(The probe is about to change that notebook; OneNote raised \"OneNote is syncing changes\" twice when a page of it was shown.)");
        var (_, after, _) = await ProbeAreaAsync(targetId);
        var stillShown = after is null ? [] : Shown(after);
        Recorder.Row($"{CurrentStep}-shown-check", "Setup", "GetHierarchy", new { method, target = targetName, area = areaName }, null, new { shownBefore = shown, shownAfter = stillShown, paused = true });
        return stillShown.Count == 0;
    }

    private async Task<(XElement? Area, XElement? Root, XElement? Target)> ProbeAreaAsync(string targetId)
    {
        var notebooks = (await NotebooksAsync()).ToString();
        var folder = TestNotebook.ResolveFolder(Environment.GetEnvironmentVariable);
        var roots = TestNotebookGuard.TestNotebookIds(notebooks, folder).Concat(TestNotebookGuard.ThrowawayNotebookIds(notebooks, ThrowawayRoot, Throwaways)).ToList();
        foreach (var rootId in roots)
        {
            var root = await TreeAsync(rootId);
            var (area, target) = FindProbeArea(root, targetId);
            if (target is not null)
            {
                return (area, root, target);
            }
        }

        return (null, null, null);
    }

    /// <summary>
    /// The probe area around <paramref name="targetId"/> in one notebook tree (pure, unit-tested): the run's
    /// <c>probe run …</c> section group; else the whole notebook if it's a throwaway; else the nearest probe-named
    /// section or group (a recycled one, say); else none. Target is null when the ID isn't in this tree.
    /// </summary>
    internal static (XElement? Area, XElement? Target) FindProbeArea(XElement root, string targetId)
    {
        ArgumentNullException.ThrowIfNull(root);
        var e = ProbeXml.ById(root, targetId);
        if (e is null)
        {
            return (null, null);
        }

        var run = e.AncestorsAndSelf(root.Name.Namespace + "SectionGroup").FirstOrDefault(g => ((string?)g.Attribute("name"))?.StartsWith("probe run ", StringComparison.Ordinal) == true);
        if (run is not null)
        {
            return (run, e);
        }

        var rootName = (string?)root.Attribute("name");
        if (rootName?.StartsWith(TestNotebook.ThrowawayNamePrefix, StringComparison.Ordinal) == true || rootName == TestNotebook.CloudThrowawayName)
        {
            return (root, e);
        }

        var probeNode = e.AncestorsAndSelf().FirstOrDefault(a => a.Name.LocalName is "Section" or "SectionGroup" && ((string?)a.Attribute("name"))?.StartsWith(ProbeNamePrefix, StringComparison.Ordinal) == true);
        return (probeNode, e);
    }

    /// <summary>
    /// The page Phill clicks to take a notebook off screen: always in the <em>other</em> test notebook, since a hierarchy
    /// change anywhere in the notebook on screen can raise "OneNote is syncing changes" (5.1 runs, 2026-11-11).
    /// </summary>
    internal static string SafePage(bool cloud) => cloud ? "MCP Test > Seed (its first page)" : "MCP Test Cloud > Probes > Text Page";

    /// <summary>The names of the nodes in <paramref name="area"/> that OneNote marks <c>isCurrentlyViewed</c>.</summary>
    internal static List<string?> Shown(XElement area) =>
        area.DescendantsAndSelf().Where(e => (string?)e.Attribute("isCurrentlyViewed") == "true").Select(e => (string?)e.Attribute("name")).ToList();

    // --- Story 5.1: hierarchy writes, deletes and backups ---------------------------------------------------------

    /// <summary>
    /// The objects this run made (sections, section groups, pages, throwaway notebooks), and soft-deleted copies of
    /// them found by their stamped names. <c>DeleteHierarchy</c> and <c>UpdateHierarchy</c> refuse any other ID.
    /// </summary>
    public HashSet<string> Created { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The throwaway notebooks this run made (local) or adopted (the cloud one Phill made), each with the exact names
    /// the guard accepts for it: the name it was created with, plus any rename the probe itself sends
    /// (<see cref="AllowThrowawayName"/>).
    /// </summary>
    public Dictionary<string, IReadOnlySet<string>> Throwaways { get; } = new(StringComparer.Ordinal);

    /// <summary>Lets the guard accept <paramref name="name"/> for throwaway <paramref name="id"/>: call it before the probe renames it.</summary>
    public void AllowThrowawayName(string id, string name) =>
        Throwaways[id] = new HashSet<string>(Throwaways.TryGetValue(id, out var names) ? names : [], StringComparer.Ordinal) { name };

    /// <summary><c>%LOCALAPPDATA%\OneNoteMcp-Test\probes\publish</c>: the only place <c>Publish</c> writes.</summary>
    public static string PublishDir { get; } = Path.Combine(ProbeRecorder.ProbesDir, "publish");

    /// <summary>Where local throwaway notebooks are created: beside MCP Test's folder.</summary>
    public static string ThrowawayRoot => TestNotebook.ThrowawayRoot;

    /// <summary>Like <see cref="TryAsync"/>, keeping the call's value (default when it failed).</summary>
    public static async Task<(Attempt Attempt, T? Value)> TryValueAsync<T>(Func<Task<T>> call)
    {
        T? value = default;
        var attempt = await TryAsync(async () => value = await call());
        return (attempt, value);
    }

    /// <summary>A guarded write that may also target a throwaway notebook of this run.</summary>
    public Task<Attempt> TryWriteAnyAsync(string targetId, string method, Action<IApplication> call) =>
        TryAsync(() => TestNotebook.WriteAsync(
            Gateway,
            targetId,
            app =>
            {
                call(app);
                return true;
            },
            method,
            Ct,
            Throwaways));

    /// <summary>
    /// <c>OpenHierarchy</c> creating <paramref name="relativeName"/> under <paramref name="parentId"/> (guarded). The
    /// returned ID joins <see cref="Created"/> only if no allowed notebook held it before the call, so an "existing
    /// name" probe can never adopt a fixture.
    /// </summary>
    public async Task<(Attempt Attempt, string? Id)> CreateChildAsync(string parentId, string relativeName, CreateFileType kind)
    {
        if (!await EnsureNothingShownAsync(parentId, "OpenHierarchy"))
        {
            return (NotShownRefusal, null);
        }

        var before = await KnownIdsAsync();
        var (attempt, id) = await TryValueAsync(() => TestNotebook.WriteAsync(
            Gateway,
            parentId,
            app =>
            {
                app.OpenHierarchy(relativeName, parentId, out var created, kind);
                return created;
            },
            $"OpenHierarchy({kind})",
            Ct,
            Throwaways));
        await CheckTimeoutAsync($"OpenHierarchy-{kind}", attempt);
        if (attempt.Ok && !string.IsNullOrEmpty(id) && !before.Contains(id))
        {
            Created.Add(id);
        }

        return (attempt, id);
    }

    /// <summary>A section or section group that must be created (probe setup). Throws on failure.</summary>
    public async Task<string> MustCreateChildAsync(string parentId, string relativeName, CreateFileType kind)
    {
        var (attempt, id) = await CreateChildAsync(parentId, relativeName, kind);
        if (!attempt.Ok || string.IsNullOrEmpty(id) || !Created.Contains(id))
        {
            throw new InvalidOperationException($"Setup OpenHierarchy({kind}) failed: {attempt.Outcome} {attempt.HResult} {attempt.Message}");
        }

        return id;
    }

    /// <summary>A new page (title only, plus one line of probe text) in a section of this run or a throwaway; joins <see cref="Created"/>.</summary>
    public async Task<string> CreateProbePageAsync(string sectionId, string title)
    {
        var pageId = await TestNotebook.WriteAsync(
            Gateway,
            sectionId,
            app =>
            {
                app.CreateNewPage(sectionId, out var id, NewPageStyle.npsBlankPageWithTitle);
                return id;
            },
            "CreateNewPage(probe)",
            Ct,
            Throwaways);
        Created.Add(pageId);
        var setup = await TryWriteAnyAsync(
            pageId,
            "UpdatePageContent(setup)",
            app => app.UpdatePageContent(ProbeXml.Page(pageId, ProbeXml.Title(title) + ProbeXml.Outline(null, ProbeXml.Oe(null, "probe text"))), DateTime.MinValue, XMLSchema.xs2013, force: false));
        await CheckTimeoutAsync("UpdatePageContent-setup", setup);
        if (!setup.Ok)
        {
            throw new InvalidOperationException($"Setup update failed: {setup.Outcome} {setup.HResult} {setup.Message}");
        }

        return pageId;
    }

    /// <summary>The attempt a write returns, without calling COM, when something in its probe area stays on screen.</summary>
    public static Attempt NotShownRefusal { get; } = new("not-run", null, null, null, 0, "something in the probe area was still on screen (EnsureNothingShownAsync)");

    /// <summary>A guarded <c>UpdateHierarchy</c>, after the shown check on the XML's root, stopping the scenario on a timeout.</summary>
    public async Task<Attempt> UpdateHierarchyAsync(string xml, string method = "UpdateHierarchy")
    {
        if (RootId(xml) is { } root && !await EnsureNothingShownAsync(root, "UpdateHierarchy"))
        {
            return NotShownRefusal;
        }

        return await TryGuardedAsync(method, () => TestNotebook.UpdateHierarchyAsync(Gateway, xml, Created, Throwaways, method, Ct));
    }

    /// <summary>
    /// A guarded <c>DeleteHierarchy</c>, stopping the scenario on a timeout. A section, section group or notebook is
    /// deleted only once nothing in its probe area is on screen; a page keeps its shown state (the date probes).
    /// </summary>
    public async Task<Attempt> DeleteHierarchyAsync(string id, DateTime expected, bool permanently)
    {
        if (!await EnsureNothingShownAsync(id, "DeleteHierarchy", pageTargetsExempt: true))
        {
            return NotShownRefusal;
        }

        return await TryGuardedAsync(permanently ? "DeleteHierarchy-permanent" : "DeleteHierarchy-soft", () => TestNotebook.DeleteHierarchyAsync(Gateway, id, expected, permanently, Created, Throwaways, Ct));
    }

    /// <summary>A guarded <c>Publish</c> of one file, after the shown check, stopping the scenario on a timeout.</summary>
    public async Task<Attempt> PublishOneAsync(string sourceId, string path, PublishFormat format)
    {
        if (!await EnsureNothingShownAsync(sourceId, "Publish"))
        {
            return NotShownRefusal;
        }

        return await TryGuardedAsync($"Publish-{format}", () => TestNotebook.PublishAsync(Gateway, sourceId, path, format, PublishDir, Throwaways, Ct));
    }

    /// <summary>The <c>ID</c> on the root of an <c>UpdateHierarchy</c> document, or null when it isn't well-formed.</summary>
    internal static string? RootId(string xml)
    {
        try
        {
            return (string?)XDocument.Parse(xml).Root?.Attribute("ID");
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>Publish</c> of <paramref name="sourceId"/> to <see cref="PublishDir"/>, trying each (format, file name) in
    /// <paramref name="tries"/> until one succeeds (Q5: <c>pfOneNote</c> first, <c>pfOneNotePackage</c> where it fails).
    /// Returns every attempt with what landed on disk, and the path that worked.
    /// </summary>
    public async Task<(IReadOnlyList<object> Attempts, string? Path, Attempt Last)> PublishAsync(string probe, string sourceId, IReadOnlyList<(PublishFormat Format, string FileName)> tries)
    {
        Directory.CreateDirectory(PublishDir);
        var rows = new List<object>();
        var last = new Attempt("not-run", null, null, null, 0, null);
        var n = 0;
        foreach (var (format, fileName) in tries)
        {
            var path = Path.Combine(PublishDir, fileName);
            last = await PublishOneAsync(sourceId, path, format);
            rows.Add(new { format = format.ToString(), fileName, last.Outcome, last.HResult, last.HResultName, last.Ms, disk = Disk(path) });
            Recorder.Row($"{probe}-try-{++n}", "Backup", $"Publish({format})", new { fileName }, last, Disk(path));
            if (last.Ok)
            {
                return (rows, path, last);
            }
        }

        return (rows, null, last);
    }

    /// <summary>
    /// Opens a backup (guarded to <see cref="PublishDir"/>): a <c>.onepkg</c> is unpacked with <c>OpenPackage</c> first,
    /// into a folder beside it. Returns the opened object's ID and the path opened.
    /// </summary>
    public async Task<(Attempt Attempt, string? Id, string? Opened)> OpenBackupAsync(string path)
    {
        var opened = path;
        if (string.Equals(Path.GetExtension(path), ".onepkg", StringComparison.OrdinalIgnoreCase))
        {
            var destination = Path.Combine(PublishDir, Path.GetFileNameWithoutExtension(path) + "-unpacked");
            var (unpack, unpacked) = await TryValueAsync(() => TestNotebook.OpenPackageAsync(Gateway, path, destination, PublishDir, Ct));
            await CheckTimeoutAsync("OpenPackage", unpack);
            Recorder.Row("open-package", "Backup", "OpenPackage", new { package = Path.GetFileName(path) }, unpack, new { unpacked = unpacked is null ? null : Path.GetRelativePath(PublishDir, unpacked) });
            if (!unpack.Ok || unpacked is null)
            {
                return (unpack, null, null);
            }

            opened = unpacked;
        }

        var (attempt, id) = await TryValueAsync(() => TestNotebook.OpenBackupAsync(Gateway, opened, PublishDir, Ct));
        await CheckTimeoutAsync("OpenHierarchy-backup", attempt);
        return (attempt, id, opened);
    }

    /// <summary>A local throwaway notebook (<see cref="TestNotebook.CreateThrowawayNotebookAsync"/>); joins <see cref="Created"/> and <see cref="Throwaways"/>.</summary>
    public async Task<(Attempt Attempt, string? Id)> CreateThrowawayAsync(string leafName, bool requireEmpty)
    {
        var result = await TryValueAsync(() => TestNotebook.CreateThrowawayNotebookAsync(Gateway, leafName, requireEmpty, Ct));
        await CheckTimeoutAsync("OpenHierarchy-cftNotebook", result.Attempt);
        if (result.Attempt.Ok && !string.IsNullOrEmpty(result.Value))
        {
            Created.Add(result.Value);
            AllowThrowawayName(result.Value, leafName);
        }

        return result;
    }

    /// <summary>
    /// Adopts the cloud throwaway Phill made: exact name and OneDrive path (<see cref="TestNotebookGuard.IsCloudThrowaway"/>).
    /// Its sections, groups and pages join <see cref="Created"/> too, since Phill made them for this run.
    /// </summary>
    public async Task<string?> AdoptCloudThrowawayAsync()
    {
        var notebook = (await NotebooksAsync()).Elements(One + "Notebook")
            .Where(n => TestNotebookGuard.IsCloudThrowaway((string?)n.Attribute("name"), (string?)n.Attribute("path")))
            .ToList();
        if (notebook.Count != 1)
        {
            return null;
        }

        var id = (string)notebook[0].Attribute("ID")!;
        AllowThrowawayName(id, TestNotebook.CloudThrowawayName);
        Created.Add(id);
        foreach (var e in (await TreeAsync(id)).Descendants().Where(e => e.Attribute("ID") is not null))
        {
            Created.Add((string)e.Attribute("ID")!);
        }

        return id;
    }

    /// <summary>Every ID in MCP Test, MCP Test Cloud and this run's throwaways, from a live read.</summary>
    public async Task<HashSet<string>> KnownIdsAsync()
    {
        var notebooks = (await NotebooksAsync()).ToString();
        var folder = TestNotebook.ResolveFolder(Environment.GetEnvironmentVariable);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var roots = TestNotebookGuard.TestNotebookIds(notebooks, folder).Concat(TestNotebookGuard.ThrowawayNotebookIds(notebooks, ThrowawayRoot, Throwaways));
        foreach (var root in roots)
        {
            foreach (var e in (await TreeAsync(root)).DescendantsAndSelf().Where(e => e.Attribute("ID") is not null))
            {
                ids.Add((string)e.Attribute("ID")!);
            }
        }

        return ids;
    }

    /// <summary>The prefix every name a story 5.1 probe gives an object starts with. No fixture name does ("Probes" doesn't).</summary>
    public const string ProbeNamePrefix = "probe ";

    /// <summary>
    /// Every element named <paramref name="name"/> in the notebook's pages-scope tree, recycle bin included. When the
    /// name is a probe name (<see cref="ProbeNamePrefix"/>, and the run's stamp by convention), the nodes found join
    /// <see cref="Created"/>: a soft delete gives a page a new ID, and this is how the probe follows it. A fixture's name
    /// never starts with the prefix, so this can't adopt one.
    /// </summary>
    public async Task<IReadOnlyList<NodeInfo>> FindByNameAsync(string notebookId, string name)
    {
        var found = (await TreeAsync(notebookId)).Descendants()
            .Where(e => (string?)e.Attribute("name") == name)
            .Select(NodeInfo.From)
            .ToList();
        if (name.StartsWith(ProbeNamePrefix, StringComparison.Ordinal))
        {
            foreach (var node in found.Where(n => n.Id is not null))
            {
                Created.Add(node.Id!);
            }
        }

        return found;
    }

    /// <summary>The element with ID <paramref name="id"/> anywhere in the notebook's tree, as a <see cref="NodeInfo"/>, or null.</summary>
    public async Task<NodeInfo?> NodeAsync(string notebookId, string id) =>
        (await TreeAsync(notebookId)).DescendantsAndSelf().Where(e => (string?)e.Attribute("ID") == id).Select(NodeInfo.From).FirstOrDefault();

    /// <summary>Whether <paramref name="id"/> is anywhere in any open notebook (a full live read).</summary>
    public async Task<bool> ExistsAnywhereAsync(string id) =>
        TestNotebookGuard.AnyContains(await Gateway.GetHierarchyAsync(string.Empty, CoreScope.Pages, Ct), id);

    /// <summary>What is on disk at <paramref name="path"/> (local paths only; a URL reads as not local).</summary>
    public static object Disk(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
        {
            return new { local = false };
        }

        if (File.Exists(path))
        {
            return new { local = true, kind = "file", bytes = new FileInfo(path).Length, extension = Path.GetExtension(path) };
        }

        if (Directory.Exists(path))
        {
            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToList();
            return new { local = true, kind = "folder", files = files.Count, bytes = files.Sum(f => new FileInfo(f).Length), extensions = files.Select(Path.GetExtension).Distinct().ToList() };
        }

        return new { local = true, kind = "absent" };
    }

    /// <summary>The foreground window, to tell whether a call took focus (spike 1's focus check).</summary>
    public static long Foreground() => NativeMethods.GetForegroundWindow().ToInt64();

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        internal static extern nint GetForegroundWindow();
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
