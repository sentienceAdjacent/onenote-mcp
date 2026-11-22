using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
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
        Assert.True(errors.Count == 0, Recorder.RedactLine("Probe steps failed (see the log):\n" + string.Join('\n', errors)));
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
            + "then check OneNote responds. Tell the team lead what dialog you saw, if any. Don't close OneNote. Then continue."
            + (RerunHint is null ? string.Empty : " " + RerunHint));
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
    /// <remarks>Spike 4.12: <paramref name="callSeconds"/> is the calling gateway's limit when it isn't the session's (<see cref="LimitOf"/>).</remarks>
    public async Task<Attempt> CheckTimeoutAsync(string method, Attempt attempt, int? callSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (StopOnBlockedCalls && Blocked(attempt, callSeconds ?? CallSeconds ?? ProbeCallSeconds))
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
    public async Task<Attempt> TryGuardedAsync(string method, Func<Task> call, int? callSeconds = null) => await CheckTimeoutAsync(method, await TryAsync(call), callSeconds);

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
        if (RunProbeRoot is not null)
        {
            // Spike 4.1: a notebook opened from the run's probe folder, whatever name OneNote gave it.
            roots.AddRange(TestNotebookGuard.NotebooksUnder(notebooks, RunProbeRoot).Select(n => n.Id).Where(id => !roots.Contains(id)));
        }

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
                app.CreateNewPage(sectionId, out var id, OneNoteMcp.Com.Interop.NewPageStyle.npsBlankPageWithTitle);
                return id;
            },
            "CreateNewPage(probe)",
            Ct,
            Throwaways);
        Created.Add(pageId);

        // Spike 3.1: the new page's own page-XML date, never DateTime.MinValue (the plan's Never list).
        var date = ProbeXml.ParseUtc(ProbeXml.Lmt(await PageAsync(pageId)) ?? throw new InvalidOperationException("The new page has no lastModifiedTime."));
        var setup = await TryWriteAnyAsync(
            pageId,
            "UpdatePageContent(setup)",
            app => app.UpdatePageContent(ProbeXml.Page(pageId, ProbeXml.Title(title) + ProbeXml.Outline(null, ProbeXml.Oe(null, "probe text"))), date, XMLSchema.xs2013, force: false));
        await CheckTimeoutAsync("UpdatePageContent-setup", setup);
        if (!setup.Ok)
        {
            throw new InvalidOperationException($"Setup update failed: {setup.Outcome} {setup.HResult} {setup.Message}");
        }

        return pageId;
    }

    /// <summary>The attempt a write returns, without calling COM, when something in its probe area stays on screen.</summary>
    public static Attempt NotShownRefusal { get; } = new("not-run", null, null, null, 0, "something in the probe area was still on screen (EnsureNothingShownAsync)");

    /// <summary>
    /// A guarded <c>UpdateHierarchy</c>, after the shown check on the XML's root, stopping the scenario on a timeout.
    /// <paramref name="allowRecycleBins"/>: spike 3.1's R1 second call and R5 only (see <see cref="TestNotebook.UpdateHierarchyAsync"/>).
    /// </summary>
    public async Task<Attempt> UpdateHierarchyAsync(string xml, string method = "UpdateHierarchy", bool allowRecycleBins = false)
    {
        if (RootId(xml) is { } root && !await EnsureNothingShownAsync(root, "UpdateHierarchy"))
        {
            return NotShownRefusal;
        }

        return await TryGuardedAsync(method, () => TestNotebook.UpdateHierarchyAsync(Gateway, xml, Created, Throwaways, method, Ct, allowRecycleBins));
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
        if (attempt.Ok && !string.IsNullOrEmpty(id))
        {
            Opened.Add(id); // spike 3.1: TestNotebook.OpenBackupAsync refused any path outside PublishDir
        }

        return (attempt, id, opened);
    }

    /// <summary>A local throwaway notebook (<see cref="TestNotebook.CreateThrowawayNotebookAsync"/>); joins <see cref="Created"/> and <see cref="Throwaways"/>.</summary>
    /// <remarks>Spike 4.1: <paramref name="root"/> makes it in a run's probe folder instead (<see cref="RunProbeRoot"/>).</remarks>
    public async Task<(Attempt Attempt, string? Id)> CreateThrowawayAsync(string leafName, bool requireEmpty, string? root = null)
    {
        var result = await TryValueAsync(() => TestNotebook.CreateThrowawayNotebookAsync(Gateway, leafName, requireEmpty, Ct, root));
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

    // --- Spike 3.1: merges, exports, packages, adoption and recycle bins -------------------------------------------

    /// <summary>
    /// What <see cref="OpenBackupAsync"/> opened from <see cref="PublishDir"/> this run (a section, or an unpacked
    /// notebook). A <c>MergeSections</c> source may be one of these instead of a <see cref="Created"/> ID; nothing else
    /// adds to it.
    /// </summary>
    public HashSet<string> Opened { get; } = new(StringComparer.Ordinal);

    private readonly List<ComGateway> sideGateways = [];

    /// <summary>X1: how long a <c>Publish</c> that returned ok is watched for its files to land and settle.</summary>
    public const int LandingPollSeconds = 120;

    /// <summary>How long a <c>Publish</c> that failed is watched, in case it wrote something anyway.</summary>
    public const int FailedLandingPollSeconds = 5;

    private const int LandingPollMs = 250;

    /// <summary>
    /// X3: a second gateway with its own call limit (<paramref name="callSeconds"/>) and its own crash breaker, for the one
    /// call that is meant to time out. Its timeout poisons only its own STA thread; the session's gateway and breaker
    /// carry on. Disposed with the session.
    /// </summary>
    public ComGateway CreateSideGateway(int callSeconds)
    {
        var settings = Path.Combine(settingsDir, string.Create(CultureInfo.InvariantCulture, $"settings-side-{sideGateways.Count}.json"));
        File.WriteAllText(settings, string.Create(CultureInfo.InvariantCulture, $$"""{ "schemaVersion": 1, "allowSilentActivation": false, "timeouts": { "callSeconds": {{callSeconds}}, "idleReleaseSeconds": 86400 } }"""));
        var gateway = new ComGateway(new PinnedOneNoteProbe(), new SettingsStore(settings));
        sideGateways.Add(gateway);
        sideLimits[gateway] = callSeconds;
        return gateway;
    }

    private readonly Dictionary<ComGateway, int> sideLimits = new();

    /// <summary>Spike 4.12: the per-call limit of <paramref name="gateway"/>: a side gateway's own, else the session's.</summary>
    public int LimitOf(ComGateway? gateway) =>
        gateway is not null && sideLimits.TryGetValue(gateway, out var seconds) ? seconds : CallSeconds ?? ProbeCallSeconds;

    /// <summary>
    /// One guarded <c>Publish</c> into a fresh, empty subfolder of <see cref="PublishDir"/> named after
    /// <paramref name="folderName"/>, after the shown check; then the landing poll (<see cref="PollLandingAsync"/>): up to
    /// <see cref="LandingPollSeconds"/> after an ok, <see cref="FailedLandingPollSeconds"/> after a failure, unless
    /// <paramref name="poll"/> says otherwise. A blocked call stops the scenario unless <paramref name="stopOnTimeout"/>
    /// is false (X3, whose timeout is the point). <paramref name="callGateway"/>: see <see cref="CreateSideGateway"/>.
    /// </summary>
    public async Task<PublishLanding> PublishFreshAsync(string folderName, string sourceId, PublishFormat format, string fileName, ComGateway? callGateway = null, bool stopOnTimeout = true, TimeSpan? poll = null)
    {
        Directory.CreateDirectory(PublishDir);
        var folder = FreshFolder(PublishDir, folderName);
        var path = Path.Combine(folder, fileName);
        Attempt attempt;
        if (!await EnsureNothingShownAsync(sourceId, "Publish"))
        {
            attempt = NotShownRefusal;
        }
        else
        {
            attempt = await TryAsync(() => TestNotebook.PublishAsync(Gateway, sourceId, path, format, PublishDir, Throwaways, Ct, callGateway));
            if (stopOnTimeout)
            {
                await CheckTimeoutAsync($"Publish-{format}", attempt);
            }
        }

        var landing = await PollLandingAsync(folder, path, poll ?? TimeSpan.FromSeconds(attempt.Ok ? LandingPollSeconds : FailedLandingPollSeconds));
        return new PublishLanding(format.ToString(), attempt, path, Path.GetRelativePath(PublishDir, folder), landing);
    }

    /// <summary>
    /// Polls <paramref name="folder"/> every 250 ms until its files have stopped changing for a second
    /// (<see cref="LandingTracker"/>) or <paramref name="limit"/> passes. Times are from the start of the poll, that is,
    /// from the moment the call returned.
    /// </summary>
    public static async Task<LandingPoll> PollLandingAsync(string folder, string target, TimeSpan limit)
    {
        var tracker = new LandingTracker(Path.GetFileName(target));
        var watch = Stopwatch.StartNew();
        while (!tracker.Observe(Snapshot(folder), watch.ElapsedMilliseconds) && watch.Elapsed < limit)
        {
            await Task.Delay(LandingPollMs, Ct);
        }

        return tracker.Result(watch.ElapsedMilliseconds);
    }

    /// <summary>Every file and folder under <paramref name="folder"/>, relative, sorted, with its size (-1 for a folder). Empty when it doesn't exist.</summary>
    internal static IReadOnlyList<FileEntry> Snapshot(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var entries = new List<FileEntry>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories))
        {
            try
            {
                entries.Add(new FileEntry(Path.GetRelativePath(folder, entry), Directory.Exists(entry) ? -1 : new FileInfo(entry).Length));
            }
            catch (IOException)
            {
                // Written or moved while listing: the next poll sees it.
            }
        }

        return entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// A new, empty folder <paramref name="name"/> in <paramref name="root"/>: <c>name</c>, else <c>name-2</c>,
    /// <c>name-3</c>…, so every <c>Publish</c> lands in a folder of its own and any extra file it writes shows.
    /// </summary>
    internal static string FreshFolder(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains("..", StringComparison.Ordinal) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException($"Refusing: \"{name}\" is not a single folder name.");
        }

        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(root, i == 1 ? name : string.Create(CultureInfo.InvariantCulture, $"{name}-{i}"));
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
        }
    }

    /// <summary>
    /// Extracts a <c>.onepkg</c> (a CAB file) with Windows' own <c>expand.exe -F:*</c> into <paramref name="destination"/>,
    /// without OneNote. Both must be under <see cref="PublishDir"/>, and the destination must be absent or empty.
    /// Records the exit code and the files written, never the tool's output (it names local paths).
    /// </summary>
    public static Task<ExtractResult> ExtractPackageAsync(string package, string destination) => ExtractPackageAsync(package, destination, PublishDir);

    internal static async Task<ExtractResult> ExtractPackageAsync(string package, string destination, string root)
    {
        const string Method = "expand";
        TestNotebookGuard.EnsureUnder(Method, package, root);
        TestNotebookGuard.EnsureUnder(Method, destination, root);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new InvalidOperationException($"Refusing {Method}: {destination} is not empty.");
        }

        Directory.CreateDirectory(destination);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "expand.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-F:*");
        start.ArgumentList.Add(package);
        start.ArgumentList.Add(destination);

        var watch = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("expand.exe didn't start.");
        var stdout = process.StandardOutput.ReadToEndAsync(Ct);
        var stderr = process.StandardError.ReadToEndAsync(Ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return new ExtractResult(null, watch.ElapsedMilliseconds, Snapshot(destination), TimedOut: true);
        }

        await Task.WhenAll(stdout, stderr);
        return new ExtractResult(process.ExitCode, watch.ElapsedMilliseconds, Snapshot(destination), TimedOut: false);
    }

    /// <summary>A guarded <c>MergeSections</c> (<see cref="TestNotebook.MergeSectionsAsync"/>), after the shown checks; the destination's new pages join <see cref="Created"/>.</summary>
    public async Task<Attempt> MergeSectionsAsync(string sourceId, string destinationId)
    {
        if (!await EnsureNothingShownAsync(destinationId, "MergeSections")
            || (Created.Contains(sourceId) && !await EnsureNothingShownAsync(sourceId, "MergeSections")))
        {
            return NotShownRefusal;
        }

        var before = PageIds(await TreeAsync(destinationId));
        var attempt = await TryGuardedAsync("MergeSections", () => TestNotebook.MergeSectionsAsync(Gateway, sourceId, destinationId, Created, Opened, Throwaways, PublishDir, Ct));
        Created.UnionWith(NewPageIds(before, await TreeAsync(destinationId)));
        return attempt;
    }

    /// <summary>The IDs of the pages directly in <paramref name="section"/>.</summary>
    internal static HashSet<string> PageIds(XElement section) =>
        section.Elements(section.Name.Namespace + "Page").Select(p => (string?)p.Attribute("ID")).OfType<string>().ToHashSet(StringComparer.Ordinal);

    /// <summary>The pages directly in <paramref name="section"/> whose IDs aren't in <paramref name="before"/>: a merge's new pages, which join <see cref="Created"/>.</summary>
    internal static IReadOnlyList<string> NewPageIds(IReadOnlySet<string> before, XElement section) =>
        PageIds(section).Where(id => !before.Contains(id)).Order(StringComparer.Ordinal).ToList();

    /// <summary>A guarded <c>MergeFiles</c> (<see cref="TestNotebook.MergeFilesAsync"/>): every path under <see cref="PublishDir"/>.</summary>
    public Task<Attempt> MergeFilesAsync(string baseFile, string clientFile, string serverFile, string targetFile) =>
        TryGuardedAsync("MergeFiles", () => TestNotebook.MergeFilesAsync(Gateway, baseFile, clientFile, serverFile, targetFile, PublishDir, Ct));

    /// <summary>
    /// <c>OpenPackage</c> of <paramref name="package"/> into <paramref name="destination"/> (both under
    /// <see cref="PublishDir"/>, as is the path OneNote reports), with whether the foreground window kept focus (O4).
    /// </summary>
    public async Task<(Attempt Attempt, string? PathOut, bool FocusKept)> OpenPackageIntoAsync(string package, string destination)
    {
        var focus = Foreground();
        var (attempt, pathOut) = await TryValueAsync(() => TestNotebook.OpenPackageAsync(Gateway, package, destination, PublishDir, Ct));
        var kept = focus == Foreground();
        await CheckTimeoutAsync("OpenPackage", attempt);
        return (attempt, pathOut, kept);
    }

    /// <summary>
    /// Q3 a: adopts an earlier run's local throwaway by its exact name (<see cref="TestNotebook.AdoptThrowawayNotebookAsync"/>).
    /// Only the notebook itself joins <see cref="Created"/>, not what an earlier run left in it. Null when it isn't open.
    /// </summary>
    public async Task<string?> AdoptThrowawayAsync(string exactName)
    {
        var id = await TestNotebook.AdoptThrowawayNotebookAsync(Gateway, exactName, Ct);
        if (id is not null)
        {
            AllowThrowawayName(id, exactName);
            Created.Add(id);
        }

        return id;
    }

    /// <summary>
    /// Q5: adopts <c>MCP Test Tenant</c> (<see cref="TestNotebookGuard.IsTenantTest"/>) like a throwaway, so the guard
    /// accepts it and nothing else on the tenant. Returns its ID and path, or nulls unless exactly one such notebook is open.
    /// </summary>
    public async Task<(string? Id, string? Path)> AdoptTenantAsync()
    {
        var matches = (await NotebooksAsync()).Elements(One + "Notebook")
            .Where(n => TestNotebookGuard.IsTenantTest((string?)n.Attribute("name"), (string?)n.Attribute("path")))
            .ToList();
        if (matches.Count != 1 || (string?)matches[0].Attribute("ID") is not { Length: > 0 } id)
        {
            return (null, null);
        }

        AllowThrowawayName(id, TestNotebook.TenantNotebookName); // admits it to EnsureTargetsAsync only; never to Created
        return (id, (string?)matches[0].Attribute("path"));
    }

    /// <summary>A notebook's own recycle bin, read live: its <c>OneNote_RecycleBin</c> group ID and its Deleted Pages section IDs.</summary>
    public async Task<(string? Bin, IReadOnlyList<string> DeletedPages)> RecycleBinAsync(string notebookId) =>
        RecycleBin(await TreeAsync(notebookId, CoreScope.Sections));

    internal static (string? Bin, IReadOnlyList<string> DeletedPages) RecycleBin(XElement notebook)
    {
        var bin = notebook.Elements(notebook.Name.Namespace + "SectionGroup").FirstOrDefault(g => (string?)g.Attribute("isRecycleBin") == "true");
        return ((string?)bin?.Attribute("ID"), bin?.Elements(notebook.Name.Namespace + "Section")
            .Where(s => (string?)s.Attribute("isDeletedPages") == "true")
            .Select(s => (string?)s.Attribute("ID"))
            .OfType<string>()
            .ToList() ?? []);
    }

    // --- Spike 3.1 / 4.1: the cross-storage settle --------------------------------------------------------------

    /// <summary>How long <see cref="SettleAsync"/> waits (spike 3.1, Implication 16).</summary>
    public const int SettleSeconds = 10;

    /// <summary>
    /// Waits <see cref="SettleSeconds"/> after a cross-storage move (spike 3.1) or a cloud close or open (spike 4.1), then
    /// reads the notebook list: a crash the call set off trips the breaker on that read, pinning it to the call.
    /// </summary>
    public async Task SettleAsync(string after, string question)
    {
        await Task.Delay(TimeSpan.FromSeconds(SettleSeconds), Ct);
        _ = await NotebooksAsync();
        Recorder.Row($"{after}-settled", question, "GetHierarchy", null, null, new { after, settledSeconds = SettleSeconds, oneNoteAlive = true });
    }

    // --- Spike 4.1: app state (close, open existing, sync) -----------------------------------------------------------

    /// <summary>
    /// This run's probe folder, <c>%LOCALAPPDATA%\OneNoteMcp-Test\probes\app-state\&lt;stamp&gt;</c>
    /// (<see cref="BeginRunProbeRoot"/>). The close and open guards accept paths inside it; null outside spike 4.1.
    /// </summary>
    public string? RunProbeRoot { get; private set; }

    /// <summary>Spike 4.1: how to rerun this run's later steps (its stamp), added to the timeout pause and echoed at the start.</summary>
    public string? RerunHint { get; set; }

    /// <summary>
    /// The paths of throwaway <c>b</c>, <c>c</c> and the cloud throwaway, recorded while they were open (or, for <c>b</c>
    /// and <c>c</c>, their fixed folders), that <see cref="OpenExistingAsync"/> may reopen. The guard checks each again.
    /// </summary>
    public HashSet<string> RecordedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>IDs <see cref="SyncHierarchyAsync"/> may name although they're gone: S2's synthetic ID and closed run notebooks' old IDs.</summary>
    public HashSet<string> SyncGone { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Makes this run's probe folder for <paramref name="stamp"/>, which must be new; or, with <paramref name="reuse"/>
    /// (a rerun of later steps of an earlier run, <c>ONENOTE_MCP_PROBE_STAMP</c>), finds the existing one.
    /// </summary>
    public string BeginRunProbeRoot(string stamp, bool reuse)
    {
        var root = Path.Combine(TestNotebook.AppStateProbesDir, stamp);
        if (!TestNotebookGuard.IsRunProbeRoot(root, ThrowawayRoot))
        {
            throw new InvalidOperationException($"Refusing: \"{stamp}\" is not a run stamp.");
        }

        if (reuse)
        {
            if (!Directory.Exists(root))
            {
                throw new InvalidOperationException($"Refusing: the probe folder of run {stamp} doesn't exist.");
            }
        }
        else
        {
            if (Directory.Exists(root) || File.Exists(root))
            {
                throw new InvalidOperationException($"Refusing: the probe folder of run {stamp} already exists.");
            }

            Directory.CreateDirectory(root);
        }

        RunProbeRoot = root;
        return root;
    }

    /// <summary>A guarded <c>CloseNotebook</c> (always <c>force=false</c>), after the shown check unless <paramref name="shownCheck"/> is false (C4); stops the scenario on a timeout.</summary>
    public async Task<Attempt> CloseNotebookAsync(string notebookId, bool shownCheck = true)
    {
        if (shownCheck && !await EnsureNothingShownAsync(notebookId, "CloseNotebook"))
        {
            return NotShownRefusal;
        }

        return await TryGuardedAsync("CloseNotebook", () => TestNotebook.CloseNotebookAsync(Gateway, notebookId, RunProbeRoot, Ct));
    }

    /// <summary>A guarded <c>OpenHierarchy</c> <c>cftNone</c> of an existing path; stops the scenario on a timeout.</summary>
    public async Task<(Attempt Attempt, string? Id)> OpenExistingAsync(string path, bool allowOpenTestNotebooks = false)
    {
        var (attempt, id) = await TryValueAsync(() => TestNotebook.OpenExistingAsync(Gateway, path, RunProbeRoot, RecordedPaths, allowOpenTestNotebooks, Ct));
        await CheckTimeoutAsync("OpenHierarchy-cftNone", attempt);
        return (attempt, id);
    }

    /// <summary>A guarded <c>SyncHierarchy</c>; stops the scenario on a timeout.</summary>
    public Task<Attempt> SyncHierarchyAsync(string id) =>
        TryGuardedAsync("SyncHierarchy", () => TestNotebook.SyncHierarchyAsync(Gateway, id, Throwaways, SyncGone, Ct));

    /// <summary>S2: a well-formed notebook-style ID that no notebook has (<c>{GUID}{1}{B0}</c>, upper case).</summary>
    internal static string SyntheticId() => "{" + Guid.NewGuid().ToString("D").ToUpperInvariant() + "}{1}{B0}";

    /// <summary>
    /// The focus-and-view snapshot: the foreground window and its process (<see cref="ProbeView.ProcessLabel"/>), and the
    /// <c>isCurrentlyViewed</c> notebook, section and page. A notebook that isn't a test, throwaway or run notebook is
    /// logged as "other notebook" and not read further. No window title is ever read.
    /// </summary>
    public async Task<ViewSnapshot> ViewAsync()
    {
        var window = NativeView.GetForegroundWindow();
        var process = NativeView.ProcessName(window);
        var notebooks = await NotebooksAsync();
        var viewed = notebooks.Elements(One + "Notebook").FirstOrDefault(n => (string?)n.Attribute("isCurrentlyViewed") == "true");
        XElement? tree = null;
        if (viewed is not null && ProbeView.IsLoggable((string?)viewed.Attribute("name"), (string?)viewed.Attribute("path"), RunProbeRoot) && (string?)viewed.Attribute("ID") is { } id)
        {
            // Sections scope, then only the viewed section's pages: a pages-scope read of MCP Test (Ballast) is slow.
            tree = await TreeAsync(id, CoreScope.Sections);
            var section = tree.Descendants(One + "Section").LastOrDefault(e => (string?)e.Attribute("isCurrentlyViewed") == "true");
            if ((string?)section?.Attribute("ID") is { } sectionId)
            {
                var pages = await TreeAsync(sectionId);
                pages.SetAttributeValue("isCurrentlyViewed", "true"); // it was marked in the sections-scope read
                section!.ReplaceWith(pages);
            }
        }

        return ProbeView.Snapshot(window.ToInt64(), ProbeView.ProcessLabel(process, CurrentProcessName), notebooks, tree, RunProbeRoot);
    }

    private static readonly string CurrentProcessName = Process.GetCurrentProcess().ProcessName;

    /// <summary>The fill series' fixed read times, then <see cref="FillSchedule"/>'s 10 s steps.</summary>
    public async Task<FillSeries> FillSeriesAsync(string notebookId, TimeSpan limit, (int Sections, int Pages)? floor = null)
    {
        var points = new List<FillPoint>();
        var limitMs = (long)limit.TotalMilliseconds;
        var watch = Stopwatch.StartNew();
        while (FillSchedule.NextAtMs(points, limitMs, floor) is { } at)
        {
            var wait = at - watch.ElapsedMilliseconds;
            if (wait > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(wait), Ct);
            }

            points.Add(await FillPointAsync(notebookId, watch.ElapsedMilliseconds));
        }

        return new FillSeries(points, FillSchedule.Settled(points, floor), limitMs);
    }

    private async Task<FillPoint> FillPointAsync(string notebookId, long ms)
    {
        try
        {
            var (sections, pages) = FillSchedule.Count(await TreeAsync(notebookId));
            return new FillPoint(ms, sections, pages, null);
        }
        catch (OneNoteException ex) when (ex.Code is not (ErrorCode.Timeout or ErrorCode.ComBusy))
        {
            return new FillPoint(ms, null, null, ex.Code.ToString());
        }
    }

    /// <summary>
    /// Every ID in each open test notebook, throwaway and run notebook except <paramref name="excludeId"/>, by notebook
    /// name (those names are test names): the before half of C1's "did the other notebooks keep their IDs".
    /// </summary>
    public async Task<Dictionary<string, HashSet<string>>> TestIdsAsync(string excludeId)
    {
        var notebooks = (await NotebooksAsync()).ToString();
        var roots = TestNotebookGuard.TestNotebookIds(notebooks, TestNotebook.ResolveFolder(Environment.GetEnvironmentVariable))
            .Concat(TestNotebookGuard.ThrowawayNotebookIds(notebooks, ThrowawayRoot, Throwaways))
            .Concat(RunProbeRoot is null ? [] : TestNotebookGuard.NotebooksUnder(notebooks, RunProbeRoot).Select(n => n.Id))
            .Where(id => id != excludeId)
            .Distinct(StringComparer.Ordinal);
        var ids = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            var tree = await TreeAsync(root);
            var name = (string?)tree.Attribute("name") ?? root;
            ids[ids.ContainsKey(name) ? $"{name} ({root})" : name] = tree.DescendantsAndSelf().Select(e => (string?)e.Attribute("ID")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        }

        return ids;
    }

    /// <summary>C1: whether each notebook of <paramref name="before"/> still has exactly the same IDs, from a fresh read.</summary>
    public async Task<IReadOnlyList<IdsKept>> OtherIdsKeptAsync(IReadOnlyDictionary<string, HashSet<string>> before, string closedId) =>
        ProbeView.CompareIds(before, await TestIdsAsync(closedId));

    /// <summary>
    /// Makes a directory junction <paramref name="link"/> → <paramref name="target"/> with <c>cmd /c mklink /J</c> (no
    /// elevation needed). Both must lie in this run's probe folder; the link must not exist and the target must.
    /// </summary>
    public void MakeJunction(string link, string target) =>
        Junctions.Make(link, target, RunProbeRoot ?? throw new InvalidOperationException("Refusing mklink: no run probe folder."));

    /// <summary>Removes the junction <paramref name="link"/> without following it (false when it doesn't exist). Refuses a real folder.</summary>
    public bool RemoveJunction(string link) =>
        Junctions.Remove(link, RunProbeRoot ?? throw new InvalidOperationException("Refusing to remove a junction: no run probe folder."));

    /// <summary>
    /// C5: writes a second settings file in the session's temp folder holding one write grant per notebook in
    /// <paramref name="grants"/> (keyed by its live <c>path</c>, the <c>NotebookKey</c>), runs Core's
    /// <c>ListGrantsUseCase</c> on it over this session's gateway, and returns each grant's kind, name and status.
    /// </summary>
    public async Task<IReadOnlyList<object>> ProbeGrantsAsync(IReadOnlyList<(string Path, string Name)> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        var file = Path.Combine(settingsDir, "settings-grants.json");
        await File.WriteAllTextAsync(file, ProbeView.GrantSettingsJson(grants, DateTime.UtcNow), Ct);
        var store = new SettingsStore(file);
        var listing = new OneNoteMcp.Core.UseCases.ListGrantsUseCase(Gateway, store.Read, TimeProvider.System);
        var result = await listing.ExecuteAsync(new OneNoteMcp.Core.UseCases.ToolCallContext("probe-grants"), Ct);
        return (result.Meta.Grants ?? []).Select(g => (object)new { g.Kind, g.Name, g.Key, g.Status }).ToList();
    }

    /// <summary>
    /// The pause before a close or the open of a closed notebook: what Phill will see. He watches and doesn't click.
    /// Pair it with <see cref="ReportPauseAsync"/> after the calls.
    /// </summary>
    public Task<bool> ObservePauseAsync(string step, string whatYouWillSee) => PauseAsync(
        $"{step}-watch",
        $"Watch OneNote and don't click in it until the next pause. What should happen next: {whatYouWillSee} Then continue.");

    /// <summary>The pause after the calls: Phill tells the team lead what he saw.</summary>
    public Task<bool> ReportPauseAsync(string step, string whatToReport) => PauseAsync(
        $"{step}-report",
        $"Tell the team lead what you saw in OneNote since the last pause: {whatToReport} Don't change anything. Then continue.");

    /// <summary>The open notebook <paramref name="id"/>, from a live read, or null.</summary>
    public async Task<NodeInfo?> NotebookInfoAsync(string id) =>
        (await NotebooksAsync()).Elements(One + "Notebook").Where(n => (string?)n.Attribute("ID") == id).Select(NodeInfo.From).FirstOrDefault();

    /// <summary>
    /// How a pause names a notebook, from a live read: its exact name, and its folder for a local one (the recorder
    /// writes <c>%LOCALAPPDATA%</c> for the user's folder), or "on OneDrive".
    /// </summary>
    internal static string Label(string? name, string? path) =>
        path is not null && Path.IsPathFullyQualified(path) ? $"\"{name}\" (folder {path})" : $"\"{name}\" on OneDrive";

    private static class NativeView
    {
        public static nint GetForegroundWindow() => NativeMethodsView.GetForegroundWindow();

        public static string? ProcessName(nint window)
        {
            if (window == 0 || NativeMethodsView.GetWindowThreadProcessId(window, out var pid) == 0 || pid == 0)
            {
                return null;
            }

            try
            {
                using var process = Process.GetProcessById((int)pid);
                return process.ProcessName;
            }
            catch (ArgumentException)
            {
                return null; // gone
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    private static class NativeMethodsView
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        internal static extern nint GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    }

    // --- Spike 4.12: navigation, links, windows and landings ---------------------------------------------------------

    /// <summary>
    /// The URLs <see cref="NavigateToUrlAsync"/> may follow: exactly the strings this run produced (<see cref="LinkAsync"/>,
    /// <see cref="WebLinkAsync"/>, <see cref="MissingPageLink"/>, <see cref="LooseFileLink"/>). Ordinal.
    /// </summary>
    public HashSet<string> NavigableUrls { get; } = new(StringComparer.Ordinal);

    /// <summary>The <c>.one</c> files this run published into <see cref="PublishDir"/> (N5), the only files <see cref="LooseFileLink"/> links.</summary>
    public HashSet<string> PublishedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A guarded <c>GetHyperlinkToObject</c> on <paramref name="hierarchyId"/> (and a paragraph's
    /// <paramref name="objectId"/>) inside an allowed notebook; the link joins <see cref="NavigableUrls"/>. Never logged
    /// raw: rows take <see cref="LinkShape.Parse"/>.
    /// </summary>
    public Task<(Attempt Attempt, string? Link)> LinkAsync(string hierarchyId, string objectId = "", ComGateway? gateway = null) =>
        LinkAsync(hierarchyId, objectId, web: false, gateway);

    /// <summary>A guarded <c>GetWebHyperlinkToObject</c> (N2); the link joins <see cref="NavigableUrls"/>.</summary>
    public Task<(Attempt Attempt, string? Link)> WebLinkAsync(string hierarchyId, ComGateway? gateway = null) =>
        LinkAsync(hierarchyId, string.Empty, web: true, gateway);

    private async Task<(Attempt Attempt, string? Link)> LinkAsync(string hierarchyId, string objectId, bool web, ComGateway? gateway)
    {
        var (attempt, link) = await TryValueAsync(() => TestNotebook.HyperlinkAsync(gateway ?? Gateway, hierarchyId, objectId, web, RunProbeRoot, Ct));
        await CheckTimeoutAsync(web ? "GetWebHyperlinkToObject" : "GetHyperlinkToObject", attempt, LimitOf(gateway));
        if (attempt.Ok && !string.IsNullOrEmpty(link))
        {
            NavigableUrls.Add(link);
        }

        return (attempt, attempt.Ok ? link : null);
    }

    /// <summary>
    /// N3: <paramref name="link"/> (one of <see cref="NavigableUrls"/>) with its <c>page-id</c> replaced by a fresh GUID,
    /// a link to a page that never existed. It joins <see cref="NavigableUrls"/>.
    /// </summary>
    public string MissingPageLink(string link)
    {
        if (!NavigableUrls.Contains(link))
        {
            throw new InvalidOperationException("Refusing: a missing-page link is made only from a link this run produced.");
        }

        var missing = NavLinks.WithPageId(link, Guid.NewGuid());
        NavigableUrls.Add(missing);
        return missing;
    }

    /// <summary>
    /// N5: <c>onenote:///&lt;path&gt;</c> for a <c>.one</c> this run published into <see cref="PublishDir"/>
    /// (<see cref="PublishedFiles"/>). It joins <see cref="NavigableUrls"/>.
    /// </summary>
    public string LooseFileLink(string path)
    {
        TestNotebookGuard.EnsureUnder("LooseFileLink", path, PublishDir);
        if (!PublishedFiles.Contains(path) || !string.Equals(Path.GetExtension(path), ".one", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing: a file link is made only for a .one this run published.");
        }

        var link = NavLinks.LooseFile(path);
        NavigableUrls.Add(link);
        return link;
    }

    /// <summary>A guarded app-level <c>NavigateTo</c> (<c>fNewWindow=false</c>); stops the scenario on a timeout.</summary>
    public Task<Attempt> NavigateToAsync(string hierarchyId, ComGateway? gateway = null) =>
        TryGuardedAsync("NavigateTo", () => TestNotebook.NavigateToAsync(gateway ?? Gateway, hierarchyId, RunProbeRoot, Ct), LimitOf(gateway));

    /// <summary>A guarded <c>NavigateToUrl</c> (<c>fNewWindow=false</c>) on a URL of <see cref="NavigableUrls"/>; stops the scenario on a timeout.</summary>
    public Task<Attempt> NavigateToUrlAsync(string url, ComGateway? gateway = null) =>
        TryGuardedAsync("NavigateToUrl", () => TestNotebook.NavigateToUrlAsync(gateway ?? Gateway, url, NavigableUrls, Ct), LimitOf(gateway));

    /// <summary>R1: the guard alone for one call of a pair, run before the pair (a read), so nothing reads between its two calls.</summary>
    public async Task<(Attempt Attempt, NavigationCheck? Check)> CheckNavigationAsync(string target, bool url, ComGateway? gateway = null)
    {
        var (attempt, check) = await TryValueAsync(() => url
            ? TestNotebook.CheckNavigateToUrlAsync(gateway ?? Gateway, target, NavigableUrls, Ct)
            : TestNotebook.CheckNavigateToAsync(gateway ?? Gateway, target, RunProbeRoot, "NavigateTo", Ct));
        await CheckTimeoutAsync(url ? "NavigateToUrl-check" : "NavigateTo-check", attempt, LimitOf(gateway));
        return (attempt, check);
    }

    /// <summary>R1: the call for a target <see cref="CheckNavigationAsync"/> passed; stops the scenario on a timeout.</summary>
    public Task<Attempt> NavigateCheckedAsync(NavigationCheck check, ComGateway? gateway = null)
    {
        ArgumentNullException.ThrowIfNull(check);
        return check.Method == "NavigateToUrl"
            ? TryGuardedAsync("NavigateToUrl", () => TestNotebook.NavigateToUrlCheckedAsync(gateway ?? Gateway, check, Ct), LimitOf(gateway))
            : TryGuardedAsync("NavigateTo", () => TestNotebook.NavigateToCheckedAsync(gateway ?? Gateway, check, Ct), LimitOf(gateway));
    }

    /// <summary>W4: a guarded <c>Window.NavigateTo</c> on the window with <paramref name="windowHandle"/>; Value is false when no window had it.</summary>
    public async Task<(Attempt Attempt, bool Found)> WindowNavigateToAsync(ulong windowHandle, string hierarchyId, ComGateway? gateway = null)
    {
        var (attempt, found) = await TryValueAsync(() => TestNotebook.WindowNavigateToAsync(gateway ?? Gateway, windowHandle, hierarchyId, RunProbeRoot, Ct));
        await CheckTimeoutAsync("Window.NavigateTo", attempt, LimitOf(gateway));
        return (attempt, found);
    }

    /// <summary>
    /// W1: one full read of <c>Windows</c> in one STA delegate: <c>Count</c>, <c>CurrentWindow</c>, and <c>Item(i)</c> for
    /// i = 0 … Count (the index base, and the HRESULT past the end), each window's getters with each getter's HRESULT
    /// kept, and whether its handle is a top-level window. Every <c>Windows</c> and <c>Window</c> wrapper is released in
    /// a <c>finally</c> before the delegate returns. A read: no setter can be called (they don't compile).
    /// </summary>
    public async Task<WindowsSnapshot> WindowsSnapshotAsync(ComGateway? gateway = null)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            return await (gateway ?? Gateway).InvokeAsync(
                app =>
                {
                    var wrappers = new List<object>();
                    try
                    {
                        IProbeWindows windows;
                        try
                        {
                            var raw = app.Windows;
                            wrappers.Add(raw);
                            windows = (IProbeWindows)raw;
                        }
                        catch (Exception ex) when (ProbeValue.IsComFailure(ex))
                        {
                            return new WindowsSnapshot(ProbeValue.Error(ex.HResult), ProbeValue.None, null, [], watch.ElapsedMilliseconds);
                        }
                        catch (InvalidCastException)
                        {
                            return new WindowsSnapshot(new ProbeValue(null, "InvalidCast"), ProbeValue.None, null, [], watch.ElapsedMilliseconds);
                        }

                        var count = ProbeValue.Read(() => windows.Count);
                        WindowRead current;
                        try
                        {
                            IProbeWindow? window = windows.CurrentWindow;
                            if (window is null)
                            {
                                current = WindowRead.Failed(null, WindowRead.NullWindow);
                            }
                            else
                            {
                                wrappers.Add(window);
                                current = ReadWindow(window, null);
                            }
                        }
                        catch (Exception ex) when (ProbeValue.IsComFailure(ex))
                        {
                            current = WindowRead.Failed(null, ProbeValue.HResultText(ex.HResult));
                        }

                        var items = new List<WindowRead>();
                        var n = count.Value is uint c ? c : 0u;
                        for (uint i = 0; i <= n; i++)
                        {
                            try
                            {
                                IProbeWindow? window = windows.GetItem(i);
                                if (window is null)
                                {
                                    items.Add(WindowRead.Failed((int)i, WindowRead.NullWindow));
                                    continue;
                                }

                                wrappers.Add(window);
                                items.Add(ReadWindow(window, (int)i));
                            }
                            catch (Exception ex) when (ProbeValue.IsComFailure(ex))
                            {
                                items.Add(WindowRead.Failed((int)i, ProbeValue.HResultText(ex.HResult)));
                            }
                        }

                        return new WindowsSnapshot(ProbeValue.Ok("Windows"), count, current, items, watch.ElapsedMilliseconds);
                    }
                    finally
                    {
                        ProbeWindowsRelease.All(wrappers);
                    }
                },
                Ct,
                retryOnDisconnect: false,
                method: "Windows(read)");
        }
        catch (OneNoteException ex) when (ex.Code is not (ErrorCode.Timeout or ErrorCode.ComBusy))
        {
            return new WindowsSnapshot(new ProbeValue(null, ex.ComHResult is { } h ? ProbeValue.HResultText(h) : ex.Code.ToString()), ProbeValue.None, null, [], watch.ElapsedMilliseconds);
        }
    }

    private static WindowRead ReadWindow(IProbeWindow window, int? index)
    {
        var handle = ProbeValue.Read(() => window.WindowHandle);
        var top = handle.Value is ulong h ? NavNative.TopLevel(h) : null;
        return new WindowRead(
            index,
            null,
            handle,
            top?.IsWindow,
            top?.TopLevel,
            top?.RootIsForeground,
            ProbeValue.Read(() => window.CurrentPageId),
            ProbeValue.Read(() => window.CurrentSectionId),
            ProbeValue.Read(() => window.CurrentSectionGroupId),
            ProbeValue.Read(() => window.CurrentNotebookId),
            new WindowFlags(
                ProbeValue.Read(() => window.GetFullPageView()),
                ProbeValue.Read(() => window.GetActive()),
                ProbeValue.Read(() => window.GetDockedLocation()),
                ProbeValue.Read(() => window.GetSideNote())));
    }

    /// <summary>
    /// K1: the landing series after a navigation returned: <c>CurrentWindow</c>'s page, section and notebook IDs, each
    /// read in one STA delegate (wrappers released in a <c>finally</c>), at <see cref="LandingSchedule"/>'s times, until
    /// the stop rule holds or the limit (5 s; 10 s on the cloud) passes.
    /// </summary>
    public async Task<LandingSeries> LandingSeriesAsync(bool cloud, ComGateway? gateway = null)
    {
        var points = new List<LandingPoint>();
        var watch = Stopwatch.StartNew();
        while (LandingSchedule.NextAtMs(points, cloud) is { } at)
        {
            var wait = at - watch.ElapsedMilliseconds;
            if (wait > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(wait), Ct);
            }

            points.Add(await LandingPointAsync(watch, gateway ?? Gateway));
        }

        return new LandingSeries(points, LandingSchedule.Settled(points), LandingSchedule.SettledAtMs(points), LandingSchedule.LimitMs(cloud));
    }

    private static async Task<LandingPoint> LandingPointAsync(Stopwatch watch, ComGateway gateway)
    {
        var ms = watch.ElapsedMilliseconds;
        try
        {
            return await gateway.InvokeAsync(
                app =>
                {
                    var wrappers = new List<object>();
                    try
                    {
                        var raw = app.Windows;
                        wrappers.Add(raw); // before the cast, so a failed cast still releases it
                        IProbeWindow? window = ((IProbeWindows)raw).CurrentWindow;
                        if (window is null)
                        {
                            return new LandingPoint(ms, null, null, null, WindowRead.NullWindow);
                        }

                        wrappers.Add(window);
                        return new LandingPoint(ms, window.CurrentPageId, window.CurrentSectionId, window.CurrentNotebookId, null);
                    }
                    catch (Exception ex) when (ProbeValue.IsComFailure(ex))
                    {
                        return new LandingPoint(ms, null, null, null, ProbeValue.HResultText(ex.HResult));
                    }
                    catch (InvalidCastException)
                    {
                        return new LandingPoint(ms, null, null, null, "InvalidCast");
                    }
                    finally
                    {
                        ProbeWindowsRelease.All(wrappers);
                    }
                },
                Ct,
                retryOnDisconnect: false,
                method: "CurrentWindow(landing)");
        }
        catch (OneNoteException ex) when (ex.Code is not (ErrorCode.Timeout or ErrorCode.ComBusy))
        {
            return new LandingPoint(ms, null, null, null, ex.ComHResult is { } h ? ProbeValue.HResultText(h) : ex.Code.ToString());
        }
    }

    /// <summary>
    /// K2 (c): the <c>isCurrentlyViewed</c> notebook, section and page IDs, read the way <see cref="ViewAsync"/> reads
    /// them (sections scope, then the viewed section's pages). A notebook outside MCP Test, MCP Test Cloud and the run's
    /// notebooks is read no further: its ID is kept, its section and page are null.
    /// </summary>
    public async Task<(string? Notebook, string? Section, string? Page)> ViewedIdsAsync(ComGateway? gateway = null)
    {
        var notebooks = await NotebooksAsync(gateway);
        var viewed = notebooks.Elements(One + "Notebook").FirstOrDefault(n => (string?)n.Attribute("isCurrentlyViewed") == "true");
        var id = (string?)viewed?.Attribute("ID");
        var navigable = TestNotebookGuard.NavigableNotebookIds(notebooks.ToString(), TestNotebook.ResolveFolder(Environment.GetEnvironmentVariable), ThrowawayRoot, RunProbeRoot);
        if (id is null || !navigable.Contains(id))
        {
            return (id, null, null);
        }

        var section = (await TreeAsync(id, CoreScope.Sections, gateway)).Descendants(One + "Section").LastOrDefault(e => (string?)e.Attribute("isCurrentlyViewed") == "true");
        var sectionId = (string?)section?.Attribute("ID");
        if (sectionId is null || (string?)section!.Attribute("locked") == "true")
        {
            return (id, sectionId, null);
        }

        var page = (await TreeAsync(sectionId, CoreScope.Pages, gateway)).Elements(One + "Page").FirstOrDefault(e => (string?)e.Attribute("isCurrentlyViewed") == "true");
        return (id, sectionId, (string?)page?.Attribute("ID"));
    }

    // --- Reads ----------------------------------------------------------------------------------------------------

    /// <remarks>Spike 4.12: <paramref name="gateway"/> (default: the session's) lets a cloud read use the cloud side gateway's limit.</remarks>
    public async Task<string> PageXmlAsync(string pageId, XMLSchema schema = XMLSchema.xs2013, PageInfo info = PageInfo.piBasic, ComGateway? gateway = null) =>
        await (gateway ?? Gateway).InvokeAsync(
            app =>
            {
                app.GetPageContent(pageId, out var xml, info, schema);
                return xml;
            },
            Ct,
            method: "GetPageContent(probe)");

    public async Task<XElement> PageAsync(string pageId, XMLSchema schema = XMLSchema.xs2013, PageInfo info = PageInfo.piBasic, ComGateway? gateway = null) =>
        XDocument.Parse(await PageXmlAsync(pageId, schema, info, gateway)).Root!;

    public Task<XElement> NotebooksAsync() => NotebooksAsync(null);

    public async Task<XElement> NotebooksAsync(ComGateway? gateway) =>
        XDocument.Parse(await (gateway ?? Gateway).GetHierarchyAsync(string.Empty, CoreScope.Notebooks, Ct)).Root!;

    public async Task<XElement> TreeAsync(string nodeId, CoreScope scope = CoreScope.Pages, ComGateway? gateway = null) =>
        XDocument.Parse(await (gateway ?? Gateway).GetHierarchyAsync(nodeId, scope, Ct)).Root!;

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
    public Task<string> CloudIdAsync() => CloudIdAsync(null);

    /// <summary>MCP Test Cloud's ID through <paramref name="gateway"/> (spike 4.12's cloud side gateway); skips when it isn't open.</summary>
    public async Task<string> CloudIdAsync(ComGateway? gateway)
    {
        var id = await TestNotebook.FindCloudAsync(gateway ?? Gateway, Ct);
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
        var text = Recorder.RedactLine($"[{Scenario} / {step}]\n{instruction}\n\nWhen done, delete this file. To skip this step, replace this file's content with: skip\n");
        await File.WriteAllTextAsync(file, text, Ct);
        Recorder.Row(step, "hitl", "pause", null, null, new { instruction });
        Echo($"PAUSE ({file}): {instruction}");
        Console.WriteLine(Recorder.RedactLine($"PAUSE [{Scenario} / {step}]: {instruction}"));

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
            output.WriteLine(Recorder.RedactLine(line)); // spike 3.1: URLs too, once the label scenario turns that on
        }
        catch (InvalidOperationException)
        {
            // No test is running (the output helper is gone).
        }
    }

    public void Dispose()
    {
        foreach (var side in sideGateways)
        {
            side.Dispose();
        }

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

/// <summary>Spike 3.1: one file or folder in a publish folder (relative path; -1 bytes for a folder).</summary>
internal sealed record FileEntry(string Path, long Bytes);

/// <summary>
/// Spike 3.1's landing poll, pure (unit-tested): fed one snapshot of a publish folder per poll, it records when anything
/// first appeared, every change (time, files, bytes, the target's bytes), and calls the folder stable once it is
/// non-empty, holds no empty file, and hasn't changed for <paramref name="stableForMs"/>.
/// </summary>
internal sealed class LandingTracker(string targetName, long stableForMs = 1000)
{
    private readonly List<LandingChange> changes = [];
    private IReadOnlyList<FileEntry>? last;
    private long lastChangeMs;
    private long? firstSeenMs;
    private long? stableMs;

    /// <summary>Takes one snapshot at <paramref name="ms"/>; true once the folder is stable (stop polling).</summary>
    public bool Observe(IReadOnlyList<FileEntry> snapshot, long ms)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Count > 0)
        {
            firstSeenMs ??= ms;
        }

        if (last is null || !snapshot.SequenceEqual(last))
        {
            last = snapshot;
            lastChangeMs = ms;
            changes.Add(new LandingChange(ms, snapshot.Count(e => e.Bytes >= 0), snapshot.Where(e => e.Bytes > 0).Sum(e => e.Bytes), TargetBytes(snapshot)));
            return false;
        }

        if (snapshot.Count > 0 && snapshot.All(e => e.Bytes != 0) && ms - lastChangeMs >= stableForMs)
        {
            stableMs = lastChangeMs;
            return true;
        }

        return false;
    }

    public LandingPoll Result(long polledMs)
    {
        var files = last ?? [];
        var target = TargetBytes(files);
        return new LandingPoll(firstSeenMs, stableMs, stableMs is not null, polledMs, target is not null, target, files, changes);
    }

    private long? TargetBytes(IReadOnlyList<FileEntry> snapshot) =>
        snapshot.FirstOrDefault(e => string.Equals(e.Path, targetName, StringComparison.OrdinalIgnoreCase) && e.Bytes >= 0)?.Bytes;
}

/// <summary>One change the landing poll saw: when, how many files, their bytes, and the target file's bytes (null: absent).</summary>
internal sealed record LandingChange(long Ms, int Files, long Bytes, long? TargetBytes);

/// <summary>
/// The landing poll's answer for one <c>Publish</c>: when the first file appeared and when the folder settled (ms after
/// the call returned; null: never), whether the target file exists and its size, what the folder holds, and each change.
/// </summary>
internal sealed record LandingPoll(long? FirstSeenMs, long? StableMs, bool Stable, long PolledMs, bool TargetExists, long? TargetBytes, IReadOnlyList<FileEntry> Files, IReadOnlyList<LandingChange> Changes);

/// <summary>One fresh-folder <c>Publish</c> (<see cref="ProbeSession.PublishFreshAsync"/>): the format, the attempt, the target path, the folder (relative to the publish folder) and the landing poll.</summary>
internal sealed record PublishLanding(string Format, Attempt Attempt, string Path, string Folder, LandingPoll Landing)
{
    /// <summary>The target path, when the call returned ok and the file is there.</summary>
    public string? Landed => Attempt.Ok && Landing.TargetExists ? Path : null;

    /// <summary>What a row logs: everything but the absolute path.</summary>
    public object Summary => new { format = Format, file = System.IO.Path.GetFileName(Path), folder = Folder, landing = Landing };
}

/// <summary><c>expand.exe</c>'s result: its exit code (null: killed at the 120 s limit), the time taken, and the files it wrote.</summary>
internal sealed record ExtractResult(int? ExitCode, long Ms, IReadOnlyList<FileEntry> Files, bool TimedOut);

// --- Spike 4.1: the focus-and-view snapshot, the fill series, ID comparison and junctions (pure parts unit-tested) ---

/// <summary>
/// Spike 4.1's focus-and-view snapshot: the foreground window handle, its process as <c>ONENOTE</c>, <c>test host</c>,
/// <c>other</c> or <c>none</c>, and the <c>isCurrentlyViewed</c> notebook, section and page (names redacted by
/// <see cref="ProbeView.Snapshot"/>).
/// </summary>
internal sealed record ViewSnapshot(long Window, string Process, string? Notebook, string? Section, string? Page);

/// <summary>One read of a fill series: when (ms after the open returned), the section and page counts, or the error code.</summary>
internal sealed record FillPoint(long Ms, int? Sections, int? Pages, string? Error);

/// <summary>A fill series: its reads, whether the last three matched (settled), and its limit.</summary>
internal sealed record FillSeries(IReadOnlyList<FillPoint> Points, bool Settled, long LimitMs)
{
    /// <summary>The last read's counts (what the notebook settled at, or reached by the limit).</summary>
    public FillPoint? Last => Points.Count == 0 ? null : Points[^1];
}

/// <summary>C1: one other notebook's IDs before and after a close.</summary>
internal sealed record IdsKept(string Notebook, bool Open, bool Kept, int Before, int After, int Lost, int Gained);

/// <summary>
/// The fill series' schedule (O1, O2), pure: reads at 0, 0.5, 1, 2, 4, 8 and 16 s, then every 10 s until the last three
/// reads match, never past the limit.
/// </summary>
internal static class FillSchedule
{
    public static readonly IReadOnlyList<long> FixedMs = [0, 500, 1000, 2000, 4000, 8000, 16000];

    public const long StepMs = 10_000;

    /// <summary>
    /// When the next read is due (ms from the open's return), or null when the series is done. <paramref name="floor"/>:
    /// the counts read before the close, when known (see <see cref="Settled"/>).
    /// </summary>
    public static long? NextAtMs(IReadOnlyList<FillPoint> points, long limitMs, (int Sections, int Pages)? floor = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        long next;
        if (points.Count < FixedMs.Count)
        {
            next = FixedMs[points.Count];
        }
        else
        {
            if (Settled(points, floor))
            {
                return null;
            }

            next = FixedMs[^1] + (StepMs * (points.Count - FixedMs.Count + 1));
        }

        return next <= limitMs ? next : null;
    }

    /// <summary>
    /// True when the last three reads succeeded with the same section and page counts, and those counts are a filled
    /// notebook's: not 0 sections and 0 pages, and not below <paramref name="floor"/> (the counts before the close), when
    /// known. A notebook still filling can stall at 0/0 or part-way (OneMore: a cloud notebook refilled over about 90 s).
    /// </summary>
    public static bool Settled(IReadOnlyList<FillPoint> points, (int Sections, int Pages)? floor = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 3)
        {
            return false;
        }

        var last = points.Skip(points.Count - 3).ToList();
        var first = last[0];
        return last.All(p => p.Error is null && p.Sections is not null && p.Pages is not null)
            && last.All(p => p.Sections == first.Sections && p.Pages == first.Pages)
            && !(first.Sections == 0 && first.Pages == 0)
            && (floor is not { } f || (first.Sections >= f.Sections && first.Pages >= f.Pages));
    }

    /// <summary>The sections and pages in a notebook's pages-scope tree, leaving out its recycle bin.</summary>
    public static (int Sections, int Pages) Count(XElement notebook)
    {
        ArgumentNullException.ThrowIfNull(notebook);
        bool Live(XElement e) => (string?)e.Attribute("isInRecycleBin") != "true" && !e.AncestorsAndSelf().Any(a => (string?)a.Attribute("isRecycleBin") == "true");
        var ns = notebook.Name.Namespace;
        return (notebook.Descendants(ns + "Section").Count(Live), notebook.Descendants(ns + "Page").Count(Live));
    }
}

/// <summary>Spike 4.1's pure helpers for what a row may name, and C1's and C5's comparisons.</summary>
internal static class ProbeView
{
    /// <summary>What every notebook that isn't a test, throwaway or run notebook is logged as.</summary>
    public const string OtherNotebook = "other notebook";

    /// <summary>
    /// True for a notebook a row may name: MCP Test, MCP Test Cloud, a throwaway (by its name prefix, or the cloud
    /// throwaway's exact name), or a notebook whose path lies in the run's probe folder.
    /// </summary>
    public static bool IsLoggable(string? name, string? path, string? runProbeRoot) =>
        name is TestNotebook.NotebookName or TestNotebook.CloudNotebookName or TestNotebook.CloudThrowawayName
        || name?.StartsWith(TestNotebook.ThrowawayNamePrefix, StringComparison.Ordinal) == true
        || (runProbeRoot is not null && TestNotebookGuard.IsUnderRunProbeRoot(path, runProbeRoot));

    /// <summary><c>ONENOTE</c>, <c>test host</c> (this process), <c>other</c>, or <c>none</c> when there is no foreground window.</summary>
    public static string ProcessLabel(string? processName, string testHostName) =>
        processName is null ? "none"
        : string.Equals(processName, "ONENOTE", StringComparison.OrdinalIgnoreCase) ? "ONENOTE"
        : string.Equals(processName, testHostName, StringComparison.OrdinalIgnoreCase) ? "test host"
        : "other";

    /// <summary>
    /// The snapshot from a notebooks-scope read and, when the viewed notebook is loggable, its pages-scope tree: the
    /// viewed notebook's name (or <see cref="OtherNotebook"/>), and the viewed section's and page's names inside a
    /// loggable notebook (null when none is marked, or the notebook isn't loggable).
    /// </summary>
    public static ViewSnapshot Snapshot(long window, string process, XElement notebooks, XElement? viewedTree, string? runProbeRoot)
    {
        ArgumentNullException.ThrowIfNull(notebooks);
        var viewed = notebooks.Elements(notebooks.Name.Namespace + "Notebook").FirstOrDefault(n => (string?)n.Attribute("isCurrentlyViewed") == "true");
        if (viewed is null)
        {
            return new ViewSnapshot(window, process, null, null, null);
        }

        var name = (string?)viewed.Attribute("name");
        if (!IsLoggable(name, (string?)viewed.Attribute("path"), runProbeRoot))
        {
            return new ViewSnapshot(window, process, OtherNotebook, null, null);
        }

        string? Marked(string kind) => viewedTree?.Descendants(viewedTree.Name.Namespace + kind)
            .LastOrDefault(e => (string?)e.Attribute("isCurrentlyViewed") == "true") is { } e ? (string?)e.Attribute("name") ?? string.Empty : null;
        var sameTree = viewedTree is not null && (string?)viewedTree.Attribute("ID") == (string?)viewed.Attribute("ID");
        return new ViewSnapshot(window, process, name, sameTree ? Marked("Section") : null, sameTree ? Marked("Page") : null);
    }

    /// <summary>C1: per notebook of <paramref name="before"/>, whether it is still open with exactly the same IDs.</summary>
    public static IReadOnlyList<IdsKept> CompareIds(IReadOnlyDictionary<string, HashSet<string>> before, IReadOnlyDictionary<string, HashSet<string>> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return before.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
        {
            if (!after.TryGetValue(p.Key, out var now))
            {
                return new IdsKept(p.Key, false, false, p.Value.Count, 0, p.Value.Count, 0);
            }

            var lost = p.Value.Count(id => !now.Contains(id));
            var gained = now.Count(id => !p.Value.Contains(id));
            return new IdsKept(p.Key, true, lost == 0 && gained == 0, p.Value.Count, now.Count, lost, gained);
        }).ToList();
    }

    /// <summary>C5's settings file: one write grant per notebook, keyed by its live path, granted via the CLI at <paramref name="grantedAt"/>.</summary>
    public static string GrantSettingsJson(IReadOnlyList<(string Path, string Name)> grants, DateTime grantedAt)
    {
        ArgumentNullException.ThrowIfNull(grants);
        var notebooks = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (path, name) in grants)
        {
            // Core matches grants by NotebookKey (trailing separators trimmed); a raw cloud path ends in '/'.
            notebooks[OneNoteMcp.Core.Access.GrantKeys.NotebookKey(path)] = new Dictionary<string, object>
            {
                ["name"] = name,
                ["idAtGrant"] = "probe",
                ["write"] = true,
                ["permanentDelete"] = false,
                ["grantedAt"] = ProbeXml.FormatUtc(grantedAt),
                ["grantedVia"] = "cli",
            };
        }

        return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["schemaVersion"] = 1, ["notebooks"] = notebooks });
    }

    /// <summary>
    /// C2: every element of a full hierarchy read whose name contains "Misplaced" (OneNote's Misplaced Sections), with
    /// its kind, the loggable notebook it sits in (or <see cref="OtherNotebook"/>), and its children's names when they are
    /// probe names (others counted only).
    /// </summary>
    public static IReadOnlyList<object> Misplaced(XElement all, string? runProbeRoot)
    {
        ArgumentNullException.ThrowIfNull(all);
        return all.Descendants()
            .Where(e => ((string?)e.Attribute("name"))?.Contains("Misplaced", StringComparison.OrdinalIgnoreCase) == true)
            .Select(e =>
            {
                var notebook = e.AncestorsAndSelf(e.Name.Namespace + "Notebook").FirstOrDefault();
                var loggable = notebook is null || IsLoggable((string?)notebook.Attribute("name"), (string?)notebook.Attribute("path"), runProbeRoot);
                var children = e.Elements().Select(c => (string?)c.Attribute("name")).ToList();
                return (object)new
                {
                    kind = e.Name.LocalName,
                    name = (string?)e.Attribute("name"),
                    notebook = notebook is null ? null : loggable ? (string?)notebook.Attribute("name") : OtherNotebook,
                    probeChildren = loggable ? children.Where(n => n?.StartsWith(ProbeSession.ProbeNamePrefix, StringComparison.Ordinal) == true).ToList() : [],
                    otherChildren = children.Count(n => !loggable || n?.StartsWith(ProbeSession.ProbeNamePrefix, StringComparison.Ordinal) != true),
                };
            })
            .ToList();
    }

    /// <summary>A notebook as a row may log it: in full when loggable, else only <see cref="OtherNotebook"/>.</summary>
    public static object Notebook(NodeInfo node, string? runProbeRoot)
    {
        ArgumentNullException.ThrowIfNull(node);
        return IsLoggable(node.Name, node.Path, runProbeRoot) ? node : new { kind = node.Kind, name = OtherNotebook };
    }
}

/// <summary>Spike 4.1 (O9): directory junctions in the run's probe folder, made with <c>mklink /J</c> and removed without being followed.</summary>
internal static class Junctions
{
    /// <summary>Makes <paramref name="link"/> → <paramref name="target"/>; both strictly inside <paramref name="root"/>.</summary>
    public static void Make(string link, string target, string root)
    {
        TestNotebookGuard.EnsureUnder("mklink", link, root);
        TestNotebookGuard.EnsureUnder("mklink", target, root);
        if (Directory.Exists(link) || File.Exists(link))
        {
            throw new InvalidOperationException($"Refusing mklink: {link} already exists.");
        }

        if (!Directory.Exists(target))
        {
            throw new InvalidOperationException($"Refusing mklink: {target} is not a folder.");
        }

        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("cmd.exe didn't start.");
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("mklink /J didn't finish within 30 s.");
        }

        if (process.ExitCode != 0 || !IsJunction(link))
        {
            throw new InvalidOperationException($"mklink /J failed (exit code {process.ExitCode}).");
        }
    }

    /// <summary>
    /// Removes the junction <paramref name="link"/> (strictly inside <paramref name="root"/>) without following it: the
    /// link goes, its target and the target's files stay. False when nothing is there; refuses a real folder or a file.
    /// </summary>
    public static bool Remove(string link, string root)
    {
        TestNotebookGuard.EnsureUnder("remove junction", link, root);
        if (!Directory.Exists(link) && !File.Exists(link))
        {
            return false;
        }

        if (!IsJunction(link))
        {
            throw new InvalidOperationException($"Refusing to remove {link}: it is not a junction.");
        }

        Directory.Delete(link, recursive: false); // removes the reparse point only
        return true;
    }

    /// <summary>True when <paramref name="path"/> is a folder that is a reparse point (a junction or a symbolic link).</summary>
    public static bool IsJunction(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }
}

// --- Spike 4.12: windows, landings and links (pure parts unit-tested in ProbeHelpersTests) ---------------------------

/// <summary>One getter's result: its value, or the HRESULT it failed with (<c>0x…</c> and AD-9's name when mapped).</summary>
internal sealed record ProbeValue(object? Value, string? HResult)
{
    public static ProbeValue None { get; } = new(null, null);

    public static ProbeValue Ok(object? value) => new(value, null);

    public static ProbeValue Error(int hresult) => new(null, HResultText(hresult));

    public static string HResultText(int hresult) =>
        HResultMap.Name(hresult) is { } name
            ? string.Create(CultureInfo.InvariantCulture, $"0x{hresult:x8} {name}")
            : string.Create(CultureInfo.InvariantCulture, $"0x{hresult:x8}");

    /// <summary>
    /// True for a failed COM call. The runtime raises a failure HRESULT as <c>COMException</c> unless it maps the code to
    /// a .NET exception: <c>E_INVALIDARG</c> becomes <see cref="ArgumentException"/> (sitting 2026-11-21:
    /// <c>Windows.Item</c> past the end) and <c>E_NOTIMPL</c> <see cref="NotImplementedException"/>. The HRESULT is
    /// still on <see cref="Exception.HResult"/>.
    /// </summary>
    public static bool IsComFailure(Exception ex) =>
        ex is System.Runtime.InteropServices.COMException or ArgumentException or NotImplementedException;

    /// <summary>Runs one getter, keeping a COM failure's HRESULT instead of throwing.</summary>
    public static ProbeValue Read<T>(Func<T> getter)
    {
        ArgumentNullException.ThrowIfNull(getter);
        try
        {
            return new ProbeValue(getter(), null);
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            return Error(ex.HResult);
        }
    }
}

/// <summary>
/// W1: one window's getters. <see cref="Index"/> is null for <c>CurrentWindow</c>; <see cref="ItemHResult"/> is the
/// failure of <c>Item(i)</c> or <c>CurrentWindow</c> itself (then nothing else was read). <see cref="TopLevel"/>: the
/// handle is its own root window (<c>GetAncestor(GA_ROOT)</c>); OneMore found <c>WindowHandle</c> can be an inner pane.
/// </summary>
internal sealed record WindowRead(
    int? Index,
    string? ItemHResult,
    ProbeValue? Handle,
    bool? IsWindow,
    bool? TopLevel,
    bool? RootIsForeground,
    ProbeValue? PageId,
    ProbeValue? SectionId,
    ProbeValue? SectionGroupId,
    ProbeValue? NotebookId,
    WindowFlags? Flags)
{
    /// <summary>What a row records when <c>CurrentWindow</c> or <c>Item(i)</c> returned S_OK with no window.</summary>
    public const string NullWindow = "null (S_OK, no window)";

    public static WindowRead Failed(int? index, string hresult) => new(index, hresult, null, null, null, null, null, null, null, null, null);

    [System.Text.Json.Serialization.JsonIgnore]
    public ulong? HandleValue => Handle?.Value as ulong?;

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Page => PageId?.Value as string;
}

/// <summary>W1: a window's flag getters (read only; their setters are uncallable placeholders).</summary>
internal sealed record WindowFlags(ProbeValue FullPageView, ProbeValue Active, ProbeValue DockedLocation, ProbeValue SideNote);

/// <summary>W1: one full read of <c>Windows</c>: the collection, <c>Count</c>, <c>CurrentWindow</c>, and <c>Item(i)</c> for i = 0 … Count.</summary>
internal sealed record WindowsSnapshot(ProbeValue Windows, ProbeValue Count, WindowRead? Current, IReadOnlyList<WindowRead> Items, long Ms)
{
    /// <summary>The windows <c>Item(i)</c> returned, by handle (first index wins).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<WindowRead> ListedWindows => Items.Where(i => i.ItemHResult is null && i.HandleValue is not null).GroupBy(i => i.HandleValue).Select(g => g.First()).ToList();
}

/// <summary>K1: one landing read: ms after the navigation returned, and <c>CurrentWindow</c>'s IDs, or the error.</summary>
internal sealed record LandingPoint(long Ms, string? PageId, string? SectionId, string? NotebookId, string? Error);

/// <summary>K1: a landing series, whether it settled under the stop rule, when its final value first showed, and its limit.</summary>
internal sealed record LandingSeries(IReadOnlyList<LandingPoint> Points, bool Settled, long? SettledAtMs, long LimitMs)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public LandingPoint? Last => Points.Count == 0 ? null : Points[^1];
}

/// <summary>
/// K1's schedule and stop rule, pure: reads at 0, 100, 250, 500, 750, 1000, 1500, 2000, 3000 and 5000 ms (then 7500
/// and 10000 on the cloud), stopping once the reads have been unchanged for 1 s, and not before 1.5 s.
/// </summary>
internal static class LandingSchedule
{
    public static readonly IReadOnlyList<long> LocalMs = [0, 100, 250, 500, 750, 1000, 1500, 2000, 3000, 5000];

    public static readonly IReadOnlyList<long> CloudMs = [.. LocalMs, 7500, 10000];

    public const long MinimumMs = 1500;

    public const long StableForMs = 1000;

    public static long LimitMs(bool cloud) => (cloud ? CloudMs : LocalMs)[^1];

    /// <summary>When the next read is due (ms after the call returned), or null when the series is done.</summary>
    public static long? NextAtMs(IReadOnlyList<LandingPoint> points, bool cloud)
    {
        ArgumentNullException.ThrowIfNull(points);
        var schedule = cloud ? CloudMs : LocalMs;
        return Settled(points) || points.Count >= schedule.Count ? null : schedule[points.Count];
    }

    /// <summary>True when the last read is at least 1.5 s in and the reads have been the same for at least the last 1 s.</summary>
    public static bool Settled(IReadOnlyList<LandingPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
        {
            return false;
        }

        var last = points[^1];
        return last.Ms >= MinimumMs && points[StableStart(points)].Ms <= last.Ms - StableForMs;
    }

    /// <summary>When the settled value was first read (ms), or null when the series didn't settle.</summary>
    public static long? SettledAtMs(IReadOnlyList<LandingPoint> points) =>
        Settled(points) ? points[StableStart(points)].Ms : null;

    private static int StableStart(IReadOnlyList<LandingPoint> points)
    {
        var last = points[^1];
        var start = points.Count - 1;
        while (start > 0 && Same(points[start - 1], last))
        {
            start--;
        }

        return start;
    }

    private static bool Same(LandingPoint a, LandingPoint b) =>
        a.PageId == b.PageId && a.SectionId == b.SectionId && a.NotebookId == b.NotebookId && a.Error == b.Error;
}

/// <summary>
/// A link as a row may log it: its shape, never the link itself (plan, Always). The scheme; the path form (a local or UNC
/// path, a OneDrive or other https host, a fragment only); what the path names (<c>.one</c>, <c>.onetoc2</c>, a folder);
/// which fragment keys are present; the query's key names; how many GUIDs it holds; and the <c>page-id</c> and
/// <c>section-id</c> GUIDs (IDs, not names: K2 compares the page GUID).
/// </summary>
internal sealed partial record LinkShape(
    string Scheme,
    string PathForm,
    string Target,
    bool Title,
    bool SectionId,
    bool PageId,
    bool ObjectId,
    bool BasePath,
    bool End,
    IReadOnlyList<string> QueryKeys,
    int Guids,
    string? PageGuid,
    string? SectionGuid)
{
    public static LinkShape Parse(string? link)
    {
        if (string.IsNullOrEmpty(link))
        {
            return new LinkShape("none", "none", "none", false, false, false, false, false, false, [], 0, null, null);
        }

        var colon = link.IndexOf(':', StringComparison.Ordinal);
        var scheme = colon > 1 && link[..colon].All(char.IsAsciiLetter) ? link[..colon].ToLowerInvariant() : "none";
        var body = scheme == "onenote" ? link[(colon + 1)..] : link;
        if (scheme == "onenote" && body.StartsWith("///", StringComparison.Ordinal))
        {
            body = body[3..];
        }

        var hash = body.IndexOf('#', StringComparison.Ordinal);
        var beforeHash = hash < 0 ? body : body[..hash];
        var fragment = hash < 0 ? string.Empty : body[(hash + 1)..];
        var question = beforeHash.IndexOf('?', StringComparison.Ordinal);
        var path = question < 0 ? beforeHash : beforeHash[..question];
        var query = question < 0 ? string.Empty : beforeHash[(question + 1)..];

        var pieces = fragment.Length == 0 ? [] : fragment.Split('&');
        string? Value(string key) => pieces.Where(p => p.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)).Select(p => p[(key.Length + 1)..]).FirstOrDefault();
        bool Has(string key) => pieces.Any(p => p.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));

        return new LinkShape(
            scheme,
            PathFormOf(scheme, body),
            TargetOf(path),
            pieces.Length > 0 && !pieces[0].Contains('=', StringComparison.Ordinal) && pieces[0] != "end",
            Has("section-id"),
            Has("page-id"),
            Has("object-id"),
            Has("base-path"),
            pieces.Contains("end", StringComparer.Ordinal),
            query.Length == 0 ? [] : query.Split('&').Select(p => p.Split('=')[0]).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
            GuidPattern().Matches(Decode(link)).Count,
            NormalizeGuid(Value("page-id")),
            NormalizeGuid(Value("section-id")));
    }

    /// <summary>A GUID in any of its link forms (<c>{…}</c>, <c>%7B…%7D</c>, bare) as <c>{UPPER-CASE}</c>, or null.</summary>
    public static string? NormalizeGuid(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var decoded = Decode(value).Trim().TrimStart('{').TrimEnd('}');
        return Guid.TryParse(decoded, out var guid) ? "{" + guid.ToString("D").ToUpperInvariant() + "}" : null;
    }

    private static string PathFormOf(string scheme, string body)
    {
        if (body.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var host = Uri.TryCreate(body, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
            return string.Equals(host, TestNotebookGuard.CloudHost, StringComparison.OrdinalIgnoreCase) ? "https d.docs.live.net"
                : string.Equals(host, "onedrive.live.com", StringComparison.OrdinalIgnoreCase) ? "https onedrive.live.com"
                : "https other host";
        }

        if (body.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return "http";
        }

        if (scheme != "onenote" && scheme != "none")
        {
            return "other";
        }

        return body.Length >= 2 && char.IsAsciiLetter(body[0]) && body[1] == ':' ? "local path"
            : body.StartsWith(@"\\", StringComparison.Ordinal) || body.StartsWith("//", StringComparison.Ordinal) ? "unc path"
            : body.StartsWith('#') ? "fragment only"
            : "other";
    }

    private static string TargetOf(string path)
    {
        var decoded = Decode(path);
        if (decoded.Length == 0)
        {
            return "none";
        }

        if (decoded.EndsWith('/') || decoded.EndsWith('\\'))
        {
            return "folder";
        }

        var leaf = decoded[(decoded.LastIndexOfAny(['/', '\\']) + 1)..];
        var dot = leaf.LastIndexOf('.');
        return dot < 0 ? "no extension"
            : leaf[dot..].ToLowerInvariant() is ".one" or ".onetoc2" or ".onepkg" ? leaf[dot..].ToLowerInvariant()
            : "other extension";
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex GuidPattern();
}

/// <summary>Spike 4.12's link builders, pure.</summary>
internal static partial class NavLinks
{
    /// <summary>
    /// N3: <paramref name="link"/> with the value of its one <c>page-id</c> fragment key replaced by
    /// <paramref name="pageGuid"/>, written in the form the old value had (<c>{…}</c>, <c>%7B…%7D</c> or bare, upper case).
    /// Throws unless the link has exactly one <c>page-id</c>.
    /// </summary>
    public static string WithPageId(string link, Guid pageGuid)
    {
        ArgumentNullException.ThrowIfNull(link);
        var matches = PageIdValue().Matches(link);
        if (matches.Count != 1)
        {
            throw new InvalidOperationException($"The link has {matches.Count} page-id keys; a missing-page link needs exactly one.");
        }

        var old = matches[0].Groups["v"];
        var guid = pageGuid.ToString("D").ToUpperInvariant();
        var value = old.Value.StartsWith('{') ? "{" + guid + "}"
            : old.Value.StartsWith("%7B", StringComparison.OrdinalIgnoreCase) ? "%7B" + guid + "%7D"
            : guid;
        return link[..old.Index] + value + link[(old.Index + old.Length)..];
    }

    /// <summary>
    /// N5: the link to a loose <c>.one</c> file, <c>onenote:///&lt;path&gt;</c>, encoded the way OneNote's own
    /// <c>GetHyperlinkToObject</c> links are (fixture <c>tests/OneNoteMcp.Tests/Fixtures/mcp-test/link/page.txt</c>): each
    /// path segment percent-encoded (a space as <c>%20</c>), the drive (<c>C:</c>) and the backslashes kept.
    /// </summary>
    public static string LooseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var segments = path.Split('\\').Select((segment, i) =>
            i == 0 && segment.Length == 2 && char.IsAsciiLetter(segment[0]) && segment[1] == ':' ? segment : Uri.EscapeDataString(segment));
        return "onenote:///" + string.Join('\\', segments);
    }

    [GeneratedRegex(@"(?<=[#&])page-id=(?<v>[^&]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PageIdValue();
}

/// <summary>Releases the <c>Windows</c> and <c>Window</c> wrappers a delegate obtained, last first (OneMore, research row 10).</summary>
internal static class ProbeWindowsRelease
{
    /// <summary>Releases each COM wrapper in <paramref name="wrappers"/> once (one per time it was obtained), skipping anything that isn't one.</summary>
    public static int All(List<object> wrappers)
    {
        ArgumentNullException.ThrowIfNull(wrappers);
        var released = 0;
        for (var i = wrappers.Count - 1; i >= 0; i--)
        {
            if (wrappers[i] is { } o && System.Runtime.InteropServices.Marshal.IsComObject(o))
            {
                try
                {
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(o);
                    released++;
                }
                catch (System.Runtime.InteropServices.InvalidComObjectException)
                {
                    // already separated from its COM object
                }
            }
        }

        wrappers.Clear();
        return released;
    }
}

/// <summary>W1: whether a <c>WindowHandle</c> is a window, a top-level one, and the root of the foreground window. No title is read.</summary>
internal static class NavNative
{
    public static WindowTop TopLevel(ulong handle)
    {
        var h = unchecked((nint)(long)handle);
        if (h == 0 || !IsWindow(h))
        {
            return new WindowTop(false, false, false);
        }

        var root = GetAncestor(h, GaRoot);
        return new WindowTop(true, root == h, root != 0 && root == GetForegroundWindow());
    }

    private const uint GaRoot = 2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern nint GetAncestor(nint hWnd, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern nint GetForegroundWindow();
}

/// <summary>W1: <see cref="NavNative.TopLevel"/>'s result.</summary>
internal sealed record WindowTop(bool IsWindow, bool TopLevel, bool RootIsForeground);
