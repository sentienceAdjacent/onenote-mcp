using System.Globalization;
using System.Xml.Linq;
using OneNoteMcp.Com;
using Xunit;
using static OneNoteMcp.Integration.WriteProbes.ProbeXml;
using CoreScope = OneNoteMcp.Core.HierarchyScope;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Spike 4.13, scenario <c>app-state-filing</c> (one sitting, every step hitl): the quick filing picker
/// (<c>QuickFiling</c>, early-bound) and its callback, F1–F8 in the Sitting script's order. It builds on spikes 4.1 and
/// 4.12's harness: the steps, pauses, F/V, the crash breaker and 60 s call limit, and 4.12's window snapshot for F3's
/// parent handle.
/// <para>
/// The picker is configured only through its own setters and run with <c>Run</c>; what Phill picks is recorded, never
/// acted on. Each dialog RCW and its <see cref="FilingRecorder"/> stay referenced from the session until the callback
/// fires or the step's wait passes, except in F7, which drops them on purpose. Every picker is preceded by a pause
/// saying what Phill will see and do, and followed by one where he reports. F4's one write is a guarded
/// <c>UpdatePageContent</c> (5.1's guard) to the probe page in MCP Test &gt; Probes, and <c>cleanup</c> removes its
/// marker. No event sink, no <c>SetFilingLocation</c>, no navigation, no window setter.
/// </para>
/// <para>Rerun steps with <c>ONENOTE_MCP_PROBE_STEPS</c>; <c>cleanup</c> is its own step and finds the marker by text.</para>
/// </summary>
public sealed class AppStateFilingProbes(ITestOutputHelper output)
{
    public const string ScenarioName = "app-state-filing";

    [Fact]
    public async Task AppStateFiling()
    {
        ProbeSession.RequireScenario(ScenarioName);
        using var s = new ProbeSession(ScenarioName, output, ProbeSession.ProbeCallSeconds);
        var f = await s.SetupAsync("setup", () => FilingContext.CreateAsync(s));
        s.Recorder.Row("setup-relied-on", "Relied on", "-", null, null, FilingProbes.ReliedOn);

        await s.StepAsync("bind-pick", () => BindPickAsync(f));
        await s.StepAsync("modal", () => ModalAsync(f));
        await s.StepAsync("while-open", () => WhileOpenAsync(f));
        await s.StepAsync("cancel", () => CancelAsync(f));
        await s.StepAsync("late", () => LateAsync(f));
        await s.StepAsync("release", () => ReleaseAsync(f));
        await s.StepAsync("thread", () => ThreadAsync(f));
        await s.StepAsync("cleanup", () => CleanupAsync(f));

        s.Finish();
    }

    // --- F1, F2: bind, run, pick ---------------------------------------------------------------------------------------

    private static async Task BindPickAsync(FilingContext f)
    {
        var s = f.S;
        await f.PickerPauseAsync("bind-pick", "a picker titled \"MCP probe\" appears, like OneNote's Move or Copy dialog", "pick MCP Test > Probes in it and press its \"File here\" button");
        var run = await f.RunAsync("bind-pick", PickerConfig.Default);

        // F1: QuickFiling, the QI to the early-bound interface, and each setter, one row each (their HRESULTs join H).
        foreach (var call in run.Steps.Where(c => c.Method != "Run"))
        {
            s.Recorder.Row($"f1-{call.Method}", "F1", call.Method, null, call.ToAttempt(), new { call.Value, run.StaThreadId, run.StaApartment });
        }

        var wait = await ProbeSession.WaitForPickAsync(run.Calls, f.PickLimit(run));
        await f.CallbackRowAsync("f2-callback", "F2", run, wait);
        await f.ReportAsync("bind-pick", "when the picker appeared, whether it came to the front and took the keyboard focus, what its tree showed, and whether it closed when you pressed the button.");
        await f.ReleaseAsync(run);
    }

    // --- F3: modal to OneNote's window ---------------------------------------------------------------------------------

    private static async Task ModalAsync(FilingContext f)
    {
        var s = f.S;
        var windows = await s.WindowsSnapshotAsync();
        var handle = windows.Current?.HandleValue;
        s.Recorder.Row("f3-parent", "F3", "Windows(read)", null, null, new
        {
            handle,
            top = handle is { } h ? NavNative.TopLevel(h) : null,
            summary = NavProbe.Summarize(windows),
        });
        if (handle is null)
        {
            s.Recorder.Row("f3-skipped", "F3", "-", null, null, new { reason = "no CurrentWindow handle to parent the picker to" });
            return;
        }

        await f.PickerPauseAsync(
            "modal",
            "the \"MCP probe\" picker appears again, this time attached to OneNote's window",
            "click once on OneNote's page behind it and see whether OneNote responds, then press the picker's Cancel button");
        var run = await f.RunAsync("modal", PickerConfig.Default with { ParentWindowHandle = handle });
        var wait = await ProbeSession.WaitForPickAsync(run.Calls, f.PickLimit(run));
        await f.CallbackRowAsync("f3-callback", "F3", run, wait);
        await f.ReportAsync("modal", "whether the picker was in front of OneNote and took the keyboard focus, whether clicking OneNote behind it did anything (or was OneNote blocked, perhaps with a ding), and whether Cancel closed it.");
        await f.ReleaseAsync(run);
    }

    // --- F4: other calls while the picker is open ----------------------------------------------------------------------

    private static async Task WhileOpenAsync(FilingContext f)
    {
        var s = f.S;
        var before = await s.PageAsync(f.PageId);
        var expected = ParseUtc(Lmt(before) ?? throw new InvalidOperationException("The probe page has no lastModifiedTime."));
        f.BeforeF4Hash ??= PageHash(before.ToString());
        var marker = $"{FilingContext.MarkerPrefix} {f.Stamp}";

        // The target check reads the hierarchy live, before the picker opens: a read while it is open could itself fail,
        // and F4's reads are measured on their own below. 5.1's guard reads again at the write.
        var isProbePage = await f.IsProbePageAsync(f.PageId);

        await f.PickerPauseAsync(
            "while-open",
            "the \"MCP probe\" picker appears and stays open while the probe makes four calls; a second picker may appear on top of it",
            "don't touch either picker or OneNote until the next pause");
        var first = await f.RunAsync("while-open-first", PickerConfig.Default);

        var open = () => new { firstCallbacks = first.Calls.All.Count };
        var hierarchy = await s.TryGuardedAsync("GetHierarchy(notebooks)", () => s.Gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, ProbeSession.Ct));
        s.Recorder.Row("f4-get-hierarchy", "F4", "GetHierarchy(notebooks)", open(), hierarchy, null);

        var content = await s.TryGuardedAsync("GetPageContent", () => s.PageXmlAsync(f.PageId));
        s.Recorder.Row("f4-get-page-content", "F4", "GetPageContent", new { page = FilingContext.PageTitle, section = ProbeSession.ProbesSectionName, picker = open() }, content, null);

        // The one write: 5.1's guard (TestNotebook.WriteAsync), to the probe page in MCP Test > Probes only.
        var write = isProbePage
            ? await s.CheckTimeoutAsync("UpdatePageContent", await s.UpdateAsync(f.PageId, Page(f.PageId, Outline(null, Oe(null, marker))), expected))
            : new Attempt("refused", null, null, null, 0, "not the probe page in MCP Test > Probes");
        s.Recorder.Row("f4-update-page-content", "F4", "UpdatePageContent", new { page = FilingContext.PageTitle, section = ProbeSession.ProbesSectionName, expected = FormatUtc(expected), picker = open() }, write, null);

        var second = await f.RunAsync("while-open-second", PickerConfig.Default, row: "f4-second-run", question: "F4");

        await s.PauseAsync(
            "while-open-close",
            "Tell the team lead what you saw since the last pause: did a second picker appear, did OneNote show anything else (a message, a busy cursor)? "
            + "Then press Cancel on each \"MCP probe\" picker that is open, the top one first. Then continue.");
        var waits = await Task.WhenAll(
            ProbeSession.WaitForPickAsync(first.Calls, f.PickLimit(first)),
            ProbeSession.WaitForPickAsync(second.Calls, f.PickLimit(second)));
        await f.CallbackRowAsync("f4-first-callback", "F4", first, waits[0]);
        await f.CallbackRowAsync("f4-second-callback", "F4", second, waits[1]);

        // Did the write apply? (Read once the pickers are closed.)
        var (read, after) = await ProbeSession.TryValueAsync(() => s.PageAsync(f.PageId));
        await s.CheckTimeoutAsync("GetPageContent(after)", read);
        s.Recorder.Row("f4-applied", "F4", "GetPageContent(after)", null, read, new
        {
            writeOutcome = write.Outcome,
            markerOnPage = after is null ? (bool?)null : OesByText(after, marker).Count > 0,
        });

        await f.ReleaseAsync(first);
        await f.ReleaseAsync(second);
    }

    // --- F5: Cancel, then Esc ------------------------------------------------------------------------------------------

    private static async Task CancelAsync(FilingContext f)
    {
        var s = f.S;
        foreach (var (how, instruction) in new[]
        {
            ("cancel-button", "press its Cancel button"),
            ("esc", "press Esc on the keyboard (if the picker doesn't have the focus, click its title bar first)"),
        })
        {
            await f.PickerPauseAsync($"cancel-{how}", "the \"MCP probe\" picker appears", instruction);
            var run = await f.RunAsync($"cancel-{how}", PickerConfig.Default);
            var wait = await ProbeSession.WaitForPickAsync(run.Calls, f.PickLimit(run));
            await f.CallbackRowAsync($"f5-{how}-callback", "F5", run, wait, new { how });
            if (!wait.Arrived && run.RunOk)
            {
                // No callback yet: the picker may still be open, and the next one would open on top of it.
                await s.PauseAsync($"cancel-{how}-close", "No answer came from the picker. Tell the team lead whether it is still open; if it is, press its Cancel button. Then continue.");
            }

            await f.ReleaseAsync(run);
        }

        await f.ReportAsync("cancel", "whether each picker closed (the first with Cancel, the second with Esc), and anything else you noticed.");
    }

    // --- F6: our wait ends first -----------------------------------------------------------------------------------------

    private static async Task LateAsync(FilingContext f)
    {
        var s = f.S;
        await f.PickerPauseAsync(
            "late",
            "the \"MCP probe\" picker appears",
            $"leave it open and don't touch it until the next pause (about {FilingProbes.BoundedWaitSeconds} seconds)");
        var run = await f.RunAsync("late", PickerConfig.Default);
        var bounded = await ProbeSession.WaitForPickAsync(run.Calls, TimeSpan.FromSeconds(FilingProbes.BoundedWaitSeconds));
        s.Recorder.Row("f6-wait-over", "F6", "-", new { limitSeconds = FilingProbes.BoundedWaitSeconds }, null, new
        {
            bounded,
            note = "the probe's bounded wait (A8) is over here; the dialog and the recorder stay referenced",
        });

        await s.PauseAsync("late-pick", "Now pick MCP Test > Probes in the \"MCP probe\" picker that is still open and press its \"File here\" button. Then continue.");
        var late = await ProbeSession.WaitForPickAsync(run.Calls, f.PickLimit(run));
        await f.CallbackRowAsync("f6-late-callback", "F6", run, late, new { bounded });
        await f.SessionCheckAsync("f6-session-check", "F6");
        await f.ReportAsync("late", "whether the picker closed when you pressed the button, and anything else: a message, an error, OneNote slow or not responding.");
        await f.ReleaseAsync(run);
    }

    // --- F7: our reference released while the picker is open -----------------------------------------------------------

    private static async Task ReleaseAsync(FilingContext f)
    {
        var s = f.S;
        await f.PickerPauseAsync("release", "the \"MCP probe\" picker appears", "leave it open and don't touch it until the next pause");
        var run = await f.RunAsync("release", PickerConfig.Default);
        await Task.Delay(TimeSpan.FromSeconds(FilingProbes.SettleSeconds), ProbeSession.Ct);

        // Drop both: the dialog RCW (FinalReleaseComObject on its STA thread) and the callback object (GC forced).
        var callsBefore = run.Calls.All.Count;
        var release = await s.ReleasePickerAsync(run);
        var recorder = FilingProbes.DropRecorder(run);
        FilingProbes.ForceCollect();
        s.Recorder.Row("f7-release", "F7", "FinalReleaseComObject(dialog)", null, release?.ToAttempt(), new
        {
            release,
            callsBefore,
            recorderStillAlive = recorder.IsAlive, // a COM reference (OneNote's) keeps a CCW alive
        });

        await s.PauseAsync(
            "release-pick",
            "Is the \"MCP probe\" picker still open? If it is, pick MCP Test > Probes and press its \"File here\" button. If it isn't, don't do anything. Then continue.");
        var wait = await ProbeSession.WaitForPickAsync(run.Calls, f.PickLimit(run));
        await f.CallbackRowAsync("f7-callback", "F7", run, wait, new { recorderStillAlive = recorder.IsAlive });
        await f.SessionCheckAsync("f7-session-check", "F7");
        await f.ReportAsync("release", "whether the picker stayed open after the probe let go of it, whether it closed when you pressed the button, and any error, message or hang in OneNote.");
    }

    // --- F8: the STA thread replaced while the picker is open ----------------------------------------------------------

    private static async Task ThreadAsync(FilingContext f)
    {
        var s = f.S;
        var side = s.CreateSideGateway(ProbeSession.ProbeCallSeconds);
        await f.PickerPauseAsync("thread", "the \"MCP probe\" picker appears", "leave it open and don't touch it until the next pause");
        var run = await f.RunAsync("thread", PickerConfig.Default, side);
        await Task.Delay(TimeSpan.FromSeconds(FilingProbes.SettleSeconds), ProbeSession.Ct);

        // Dispose the side gateway: it posts ReleaseApplication and ends its STA thread. The session disposes it again
        // at the end, which ComGateway.Dispose allows (it returns at once once disposed).
        var callsBefore = run.Calls.All.Count;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string? disposeError = null;
        try
        {
            side.Dispose();
        }
        catch (Exception ex)
        {
            disposeError = ex.GetType().Name;
        }

        run.GatewayGone = true;
        run.Dialog = null; // its STA thread is gone: the RCW is only dropped
        // StaThread.Dispose joins the thread for at most 5 s and ignores the result, and the gateway doesn't expose its
        // worker to the test assembly: a dispose under 5 s means the join returned, so the thread ended.
        var ms = watch.ElapsedMilliseconds;
        s.Recorder.Row("f8-dispose", "F8", "ComGateway.Dispose(side)", null, null, new
        {
            ms,
            staThreadEnded = disposeError is null && ms < FilingProbes.StaJoinMs ? "confirmed (the join returned before its 5 s limit)" : "unconfirmed",
            disposeError,
            sideStaThread = run.StaThreadId,
            callsBefore,
        });

        await s.PauseAsync(
            "thread-pick",
            "Is the \"MCP probe\" picker still open? If it is, pick MCP Test > Probes and press its \"File here\" button. If it isn't, don't do anything. Then continue.");
        var wait = await ProbeSession.WaitForPickAsync(run.Calls, f.PickLimit(run));
        await f.CallbackRowAsync("f8-callback", "F8", run, wait);
        await f.SessionCheckAsync("f8-session-check", "F8");
        await f.ReportAsync("thread", "whether the picker stayed open after the probe's thread ended, whether it closed when you pressed the button, and any error, message or hang in OneNote.");
    }

    // --- cleanup -------------------------------------------------------------------------------------------------------

    private static async Task CleanupAsync(FilingContext f)
    {
        var s = f.S;
        await s.PauseAsync(
            "cleanup-pickers",
            "If any \"MCP probe\" picker is still on screen, press its Cancel button. Then tell the team lead whether any picker is still open. Then continue.");
        await Task.Delay(TimeSpan.FromSeconds(FilingProbes.SettleSeconds), ProbeSession.Ct);

        // Any callback that came after its step's row, then release every dialog this run still holds.
        foreach (var run in s.Pickers)
        {
            var released = await s.ReleasePickerAsync(run);
            s.Recorder.Row($"cleanup-picker-{run.Label}", "Left behind", "-", null, released?.ToAttempt(), new
            {
                run.Label,
                callbacks = run.Calls.All.Count,
                released,
                run.GatewayGone,
            });
        }

        // The probe page's marker (F4's write), found by text so a rerun of cleanup alone finds it too.
        var removed = new List<object>();
        var page = await s.PageAsync(f.PageId);
        foreach (var outline in OesByText(page, FilingContext.MarkerPrefix).Select(OutlineOf).Where(id => id is not null).Distinct(StringComparer.Ordinal))
        {
            var current = await s.PageAsync(f.PageId);
            var expected = ParseUtc(Lmt(current) ?? throw new InvalidOperationException("The probe page has no lastModifiedTime."));
            var delete = await f.IsProbePageAsync(f.PageId)
                ? await s.CheckTimeoutAsync("DeletePageContent", await s.DeleteContentAsync(f.PageId, outline!, expected))
                : new Attempt("refused", null, null, null, 0, "not the probe page in MCP Test > Probes");
            s.Recorder.Row("cleanup-marker", "Left behind", "DeletePageContent", new { page = FilingContext.PageTitle }, delete, null);
            removed.Add(new { delete.Outcome, delete.HResult });
        }

        var final = await s.PageAsync(f.PageId);
        s.Recorder.Row("cleanup-page", "Left behind", "-", null, null, new
        {
            removed,
            markersLeft = OesByText(final, FilingContext.MarkerPrefix).Count,
            asBeforeF4 = f.BeforeF4Hash is null ? (bool?)null : PageHash(final.ToString()) == f.BeforeF4Hash,
            probePage = $"MCP Test > {ProbeSession.ProbesSectionName} > {FilingContext.PageTitle} (kept for reruns)",
            pageCreatedThisRun = f.PageCreated,
        });
    }
}

/// <summary>Spike 4.13: the run's fixed facts and the steps' shared moves.</summary>
internal sealed class FilingContext
{
    /// <summary>The probe page in MCP Test &gt; Probes that F4 writes to. Kept between runs.</summary>
    public const string PageTitle = "probe filing page";

    /// <summary>The start of F4's marker text; cleanup removes every outline holding it.</summary>
    public const string MarkerPrefix = "probe filing marker";

    private FilingContext(ProbeSession s, string mcpTestId, string probesSectionId, string pageId, bool pageCreated)
    {
        S = s;
        McpTestId = mcpTestId;
        ProbesSectionId = probesSectionId;
        PageId = pageId;
        PageCreated = pageCreated;
        Stamp = DateTime.Now.ToString("MMdd-HHmmss", CultureInfo.InvariantCulture);
    }

    public ProbeSession S { get; }

    public string McpTestId { get; }

    public string ProbesSectionId { get; }

    public string PageId { get; }

    public bool PageCreated { get; }

    public string Stamp { get; }

    /// <summary>The probe page's hash before F4's write, for cleanup's "as before" check.</summary>
    public string? BeforeF4Hash { get; set; }

    /// <summary>MCP Test, its Probes section, and the probe page (found by title, else created once through the guard).</summary>
    public static async Task<FilingContext> CreateAsync(ProbeSession s)
    {
        var mcp = await s.McpTestIdAsync();
        var probes = await s.SectionIdAsync(mcp, "MCP Test", ProbeSession.ProbesSectionName);
        var page = await s.PageIdByTitleAsync(probes, PageTitle);
        var created = page is null;
        page ??= await s.CreateProbePageAsync(probes, PageTitle);
        s.Recorder.Row("setup", "-", "-", null, null, new { probePage = $"MCP Test > {ProbeSession.ProbesSectionName} > {PageTitle}", created });
        return new FilingContext(s, mcp, probes, page, created);
    }

    /// <summary>
    /// The write target check on top of 5.1's guard, from a live read: <paramref name="pageId"/> is a page titled
    /// <see cref="PageTitle"/>, not in the recycle bin, directly in the section named Probes directly in MCP Test.
    /// </summary>
    public async Task<bool> IsProbePageAsync(string pageId)
    {
        var mcp = await S.McpTestIdAsync();
        var sections = await S.TreeAsync(mcp, CoreScope.Sections);
        var probes = sections.Elements(ProbeSession.One + "Section")
            .FirstOrDefault(e => (string?)e.Attribute("name") == ProbeSession.ProbesSectionName && (string?)e.Attribute("isInRecycleBin") != "true");
        if ((string?)probes?.Attribute("ID") is not { } probesId)
        {
            return false;
        }

        return (await S.TreeAsync(probesId)).Elements(ProbeSession.One + "Page").Any(p =>
            (string?)p.Attribute("ID") == pageId
            && (string?)p.Attribute("name") == PageTitle
            && (string?)p.Attribute("isInRecycleBin") != "true");
    }

    /// <summary>How long a step waits for the callback: <see cref="FilingProbes.PickSeconds"/> after a <c>Run</c> that returned ok, else briefly.</summary>
    public TimeSpan PickLimit(PickerRun run) => TimeSpan.FromSeconds(run.RunOk ? FilingProbes.PickSeconds : FilingProbes.SettleSeconds);

    /// <summary>The pause before a picker: what Phill will see, and what to do in it.</summary>
    public Task<bool> PickerPauseAsync(string step, string whatYouWillSee, string whatToDo) => S.PauseAsync(
        $"{step}-watch",
        $"After you continue, {whatYouWillSee}. When it shows, {whatToDo}. Don't click anything else in OneNote until the next pause. Continue when ready.");

    /// <summary>
    /// The pause after a picker: what Phill saw, and a request to close any picker still open, so no step ends with a
    /// picker on screen and no pause asking Phill to close it (plan, Never).
    /// </summary>
    public Task<bool> ReportAsync(string step, string whatToReport) => S.PauseAsync(
        $"{step}-report",
        $"Tell the team lead what you saw in OneNote since the last pause: {whatToReport} Then, if an \"MCP probe\" picker is still open, press its Cancel button. Then continue.");

    /// <summary>F/V, <see cref="ProbeSession.RunPickerAsync"/>, F/V; then a row for <c>Run</c> (time, thread, the picker's window).</summary>
    public async Task<PickerRun> RunAsync(string label, PickerConfig config, ComGateway? gateway = null, string? row = null, string? question = null)
    {
        var before = await S.PickerViewAsync($"{label}-before");
        var run = await S.RunPickerAsync(label, config, gateway);
        var after = await S.PickerViewAsync($"{label}-after");
        var runCall = run.RunCall;
        var q = question ?? FilingProbes.QuestionOf(label);

        // When Run wasn't reached, the row carries the first failed call (QuickFiling or the QI), not the gateway's ok.
        var attempt = runCall?.ToAttempt() ?? run.Steps.FirstOrDefault(c => !c.Ok)?.ToAttempt() ?? run.Invoke;

        // Each failed call before Run gets its own row (so its HRESULT reaches H); F3 rows every call, its
        // ParentWindowHandle setter included. bind-pick rows all of them itself (F1).
        foreach (var call in FilingProbes.StepsToRow(label, run.Steps))
        {
            S.Recorder.Row($"{label}-{call.Method}", q, call.Method, null, call.ToAttempt(), new { call.Value });
        }

        if (run.PickerWindow is { Ok: false } window)
        {
            S.Recorder.Row($"{label}-{window.Method}", q, window.Method, null, window.ToAttempt(), null);
        }

        S.Recorder.Row(row ?? $"{label}-run", q, "Run", new { parent = config.ParentWindowHandle is not null, sideGateway = gateway is not null }, attempt, new
        {
            runMs = runCall?.Ms,
            failedBeforeRun = runCall is null ? run.Steps.Where(c => !c.Ok).ToList() : null,
            run.StaThreadId,
            run.StaApartment,
            pickerWindow = run.PickerWindow,
            pickerTop = run.PickerWindow?.Value is ulong h ? NavNative.TopLevel(h) : null,
            fv = new { before, after },
        });
        return run;
    }

    /// <summary>A row for the callbacks a run has seen so far, with each selected ID named by 4.1's rules.</summary>
    public async Task CallbackRowAsync(string row, string question, PickerRun run, PickWait wait, object? args = null)
    {
        var calls = run.Calls.All;
        var trees = calls.Any(c => !string.IsNullOrEmpty(c.SelectedItem.Value as string)) ? await TestTreesAsync() : [];
        S.Recorder.Row(row, question, "OnDialogClosed", args, null, new
        {
            wait,
            arrived = calls.Count > 0,
            calls = calls.Select(c => FilingProbe.Describe(c, run, id => trees is null ? new FilingTarget("unread", null, null) : FilingProbe.Target(id, trees))).ToList(),
        });

        // A getter that failed inside the callback gets its own row, so its HRESULT reaches H.
        foreach (var call in calls)
        {
            foreach (var (method, value) in new[] { ("get_SelectedItem", call.SelectedItem), ("get_PressedButton", call.PressedButton) })
            {
                if (FilingProbe.AttemptOf(value) is { } failed)
                {
                    S.Recorder.Row($"{row}-{call.Sequence}-{method}", question, $"OnDialogClosed:{method}", null, failed, null);
                }
            }
        }
    }

    /// <summary>Sections-scope trees of MCP Test and MCP Test Cloud (when open), or null when the read fails.</summary>
    private async Task<IReadOnlyList<XElement>?> TestTreesAsync()
    {
        var (attempt, trees) = await ProbeSession.TryValueAsync(async () =>
        {
            var notebooks = (await S.NotebooksAsync()).ToString();
            var ids = TestNotebookGuard.TestNotebookIds(notebooks, TestNotebook.ResolveFolder(Environment.GetEnvironmentVariable));
            var list = new List<XElement>();
            foreach (var id in ids)
            {
                list.Add(await S.TreeAsync(id, CoreScope.Sections));
            }

            return (IReadOnlyList<XElement>)list;
        });
        await S.CheckTimeoutAsync("GetHierarchy(targets)", attempt);
        return attempt.Ok ? trees : null;
    }

    /// <summary>After a picker is gone (or left): does the session gateway's next call still work? One read, no retry.</summary>
    public async Task SessionCheckAsync(string row, string question)
    {
        var attempt = await S.TryGuardedAsync("GetHierarchy(notebooks)", () => S.Gateway.InvokeAsync(
            app =>
            {
                app.GetHierarchy(string.Empty, OneNoteMcp.Com.Interop.HierarchyScope.hsNotebooks, out var xml, OneNoteMcp.Com.Interop.XMLSchema.xs2013);
                return xml.Length;
            },
            ProbeSession.Ct,
            retryOnDisconnect: false,
            method: "GetHierarchy(notebooks)"));
        S.Recorder.Row(row, question, "GetHierarchy(notebooks)", new { gateway = "session" }, attempt, null);
    }

    /// <summary>Releases the run's dialog (after its callback or its wait), rowing a failure.</summary>
    public async Task ReleaseAsync(PickerRun run)
    {
        var release = await S.ReleasePickerAsync(run);
        if (release is { Ok: false })
        {
            S.Recorder.Row($"{run.Label}-release", "H", release.Method, null, release.ToAttempt(), null);
        }
    }
}

/// <summary>Spike 4.13: the scenario's constants and helpers.</summary>
internal static class FilingProbes
{
    /// <summary>How long a step waits for Phill's pick or cancel after the instruction.</summary>
    public const int PickSeconds = 180;

    /// <summary>F6: the probe's bounded wait (A8) before Phill is told to pick.</summary>
    public const int BoundedWaitSeconds = 10;

    /// <summary>A short settle: after a picker opens (F7, F8), at cleanup, or the wait when <c>Run</c> failed.</summary>
    public const int SettleSeconds = 3;

    public static object ReliedOn { get; } = new
    {
        oneMore = "research row 26: the callback only records SelectedItem and returns, because OneNote's UI thread waits for it; the dialog must outlive Run; Run shows the picker from a new OneNote thread, modal only to a ParentWindowHandle",
        spike41 = "4.1's harness: steps, pauses, F/V (ViewAsync), the crash breaker, the 60 s call limit, side gateways",
        spike412 = "4.12's window copies (IProbeWindows, IProbeWindow) and window snapshot, for F3's parent handle",
        spike51 = "5.1's guard (TestNotebook.WriteAsync) for F4's write",
    };

    /// <summary>StaThread.Dispose's join limit (<c>src/OneNoteMcp.Com/StaThread.cs</c>).</summary>
    public const long StaJoinMs = 5000;

    /// <summary>
    /// The calls before <c>Run</c> that <see cref="FilingContext.RunAsync"/> rows on their own: every one for F3
    /// (<c>modal</c>), none for <c>bind-pick</c> (F1 rows them all), else each that failed.
    /// </summary>
    public static IEnumerable<PickerCall> StepsToRow(string label, IEnumerable<PickerCall> steps) => label switch
    {
        "bind-pick" => [],
        "modal" => steps.Where(c => c.Method != "Run"),
        _ => steps.Where(c => c.Method != "Run" && !c.Ok),
    };

    /// <summary>The Matrix row a picker run's label belongs to.</summary>
    public static string QuestionOf(string label) => label switch
    {
        "bind-pick" => "F2",
        "modal" => "F3",
        _ when label.StartsWith("while-open", StringComparison.Ordinal) => "F4",
        _ when label.StartsWith("cancel", StringComparison.Ordinal) => "F5",
        "late" => "F6",
        "release" => "F7",
        "thread" => "F8",
        _ => "-",
    };

    /// <summary>F7: drops the run's callback object, keeping only a weak reference to see whether it is collected.</summary>
    public static WeakReference DropRecorder(PickerRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var weak = new WeakReference(run.Recorder);
        run.Recorder = null;
        return weak;
    }

    /// <summary>F7: a forced, blocking collection, finalizers included.</summary>
    public static void ForceCollect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
