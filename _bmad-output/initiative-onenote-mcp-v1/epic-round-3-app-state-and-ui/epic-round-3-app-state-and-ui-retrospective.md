---
epic: epic-round-3-app-state-and-ui
date: 2026-11-26
verdict: accepted-with-open-items
criteria: declared
headless: false
---

# Retrospective: Round 3, app state and UI

**Accepted with open items** by Phill (Inbox, 2026-11-26). The machine verdict was *rejected*, because Done when 5 (a release) isn't met; the release is the open item.

## Epic summary

- **Epic:** `epic-round-3-app-state-and-ui`, 13 tickets in build order: 4.1, 4.12, 4.13 (spikes), 4.2 (spine amendments), 4.3, 4.4, 4.5, 4.6, 4.7, 4.9, 4.10, 4.11, and 4.14 (added mid-epic, 2026-11-26). Entry 8 was dropped by Q8 before the build began.
- **Status:** every ticket is finished (`tickets.py status`: no pending tickets). One is still at `built` and not marked done: **4.1**, the open/close/sync probe spike. Its plan says `status: built` and its ticket state is `review`. Its findings are final (5e4bf7d), but no "mark done" commit exists. 4.12 (2215d75) and 4.13 (6c29611) have one.
- **Going-in concerns:** Phill named none (Inbox, 2026-11-26).
- **Ranges.** Each plan's `baseline_revision` was taken, ordered by commit date, and each range runs to the next baseline; the last runs to `f856e6b` (the head of origin/main, inferred).
  - 4.1 `62afdff..23b4da6` (3 commits); 4.12 `23b4da6..55fd049` (3).
  - 4.13 `55fd049..ffb64ed` (275). **Cut:** this range also carries the round-2 epic's work, which merged in between. Only the commits whose subjects name 4.13, 4.12 or 4.1 count as this epic's.
  - 4.2 `ffb64ed..8df0c01` (14); 4.3 `8df0c01..a585a4d` (10); 4.4 `a585a4d..4eb460e` (3); 4.6 `4eb460e..21f83ba` (7); 4.5 `21f83ba..f9608a6` (29); 4.7 `f9608a6..3080ff0` (3); 4.9 `3080ff0..35bccae` (23); 4.10 `35bccae..ee89b5c` (13); 4.11 `ee89b5c..0133163` (9); 4.14 `0133163..f856e6b` (5).
  - 4.4/4.6/4.5 and 4.7/4.9 built in parallel lanes, so their baselines aren't strictly ancestors of one another. Each range is a time slice, not a clean per-ticket diff.
  - The code analysis used the whole round-3 code range `ffb64ed..f856e6b`: 107 commits, 37 merges (23 measured), 110 files.
- **Evidence inventory.**
  - Available: the epic file, `tickets.toml`, all 13 plans, the three spike findings docs, the spine and its Deferred table, `deferred-work.md`, the round-2 retrospective, git history and tags, and the transcripts (grepped, not read whole).
  - Missing:
    - The spine's `.memlog.md` wasn't read.
    - No build or test run was part of this retro. The stories' own runs and sittings are the test evidence.
    - The bodies of `CloudLink.Parse` (lines 162–330), `FakeGateway` and `FakeApplication` weren't reviewed.

## Findings

Each finding has a source and a disposition (**fix now** / **defer** / **accept**). Findings were derived by four read-only passes: aggregate views, spec reconciliation, a `bmad-review` diff pass (adversarial, edge-case and verification-gap lenses, weighted to the boundaries between tickets), and process plus the previous retro. The main session then re-checked F1, F2, F4, F5, F7, F8 and F12 against the code or git before keeping them.

### Spec reconciliation

- **F1. Done when 5 isn't met: no release contains round 3.**
  - Evidence: `git tag --contains ffb64ed` returns nothing. The latest tag is `v0.9.0-rc.1` (2026-11-19). `Directory.Build.props:4` says `0.10.0-rc.1`, which was set before round 3 (435701c) and never tagged.
  - The epic's Notes say "Phill tags the release after the refactor sweep". The sweep (4.11) and 4.14 are both done. The skill and README parts of Done when 5 are in place.
  - Disposition: **fix now** (Phill tags it). Lesson: a Done-when item that no ticket owns gets skipped.
- **F2. Five spine Deferred rows are still open, though sittings settled them.** All are in `architecture-onenote-mcp.md`:
  - Line 1039, `close_notebook`'s settle: "Unmeasured". The 4.4 sitting showed a local edit surviving a 10.5 s close (4.4 plan, line 145).
  - Line 1046, dropping the last reference with a picker on screen: "Open, not probed". The 4.9 sitting passed it in steps 3–4 (lines 118–119).
  - Line 1051, `Window.NavigateToUrl`: "Not probed". The 4.7 sitting passed it (line 113).
  - Line 1052, which window app-level `NavigateToUrl` moves: the 4.7 sitting found it moves the current window (line 114).
  - Line 1053, a window on a section inside a section group: the 4.6 sitting found the group named (line 137).
  - Disposition: **fix now**, as a spine-sync chore. Lesson: each sitting record ends by naming the Deferred rows it closes.
- **F3. Behaviour added beyond the epic text, every item recorded.**
  - `open_notebook` also refuses `.onetoc2`, other files and an empty path, and maps `\\localhost\X$` when checking for an already-open notebook (4.5 plan, lines 104–105).
  - `navigate_to_url` gained `landedInRecycleBin` and the "&" / "#" title rule (4.7 Plan Change Log, line 145).
  - `close_notebook` now warns that OneNote may reset the notebook's colour (4.10 Plan Change Log).
  - `quick_filing` caps its title at 255 characters and its description at 1024 (`QuickFilingUseCase.cs:54,57`). OneNote's own limits are unprobed.
  - Disposition: **accept**. Lesson: note the `quick_filing` caps in the spine with the F2 chore.
- **F4. `navigate_to_url`'s open-notebook match is checked before COM, outside the write lock** (`NavigateToUrlUseCase.cs:301-346`; spine line 737 gives the lock only to open and close).
  - A close between the match and the call would leave the match out of date. Spike 4.12 N4 (spine line 370) found a link into a closed notebook reopens it. The gap is milliseconds, and a lock wouldn't cover a close made in OneNote itself. Inferred, not measured.
  - Disposition: **accept**.

### Diff review (boundaries between tickets)

- **F5. `close_notebook` points at a reopen route that `open_notebook` refuses (4.4 against 4.5).**
  - `CloseNotebookUseCase.cs:52-53,67-71` tells any non-http path to "use open_notebook with meta.path".
  - `open_notebook` refuses these before COM: every `\\` path (`FilePathGate.cs:91-94`), mapped network drives (`:109`) and OneDrive-synced folders (`OpenNotebookUseCase.cs:302`).
  - Spike 4.1 O10 found OneNote keeps a `\\localhost\X$` path as given (findings, line 68). The skill gives the same route (`references/app-state.md:85`, `SKILL.md:58`).
  - The test `CloseOk_TheReopenRoute_FollowsThePathsScheme_InAnyCase` (`CloseNotebookToolTests.cs:97`) checks only http against not-http.
  - Severity medium: the user is told a reopen will work and then gets a refusal. No data harm.
  - Disposition: **fix now**. Lesson: when one ticket's output feeds another's input, test the round trip.
- **F6. `quick_filing`'s text check is weaker than the repo's own rule for text shown on screen.**
  - `QuickFilingUseCase.cs:268` refuses only `char.IsControl`. `FilePathGate.cs:96-99` also refuses Format, LineSeparator and ParagraphSeparator.
  - So U+2028 and bidi overrides reach the picker. The tests try only `\n`, `\t` and `\a` (`QuickFilingToolTests.cs:280-283`).
  - Severity low. Disposition: **fix now**, using one shared check.
- **F7. Malformed XML in `close_notebook`'s reads isn't mapped to AD-9's `InvalidXml`.**
  - `CloseNotebookUseCase.cs:98` (`HierarchyNode.Notebooks`) and `:108` (`HierarchyTarget.Read`) aren't wrapped in `XmlReads.Readable`, unlike `OpenNotebookUseCase.cs:311`, `WindowPages.cs:95` and `CloudLink.cs:334`. Such a failure would surface as `Internal`.
  - Severity low. Disposition: **fix now** (two wraps and a test row).
- **F8. `close_notebook` says "The notebook is still open." after every failure that isn't ambiguous** (`CloseNotebookUseCase.cs:173-176`).
  - The user can close the notebook in OneNote during the sync and the 10 s settle, so an `ObjectNotFound` there would make the sentence false. Which HRESULT OneNote returns for that wasn't probed.
  - Severity low. Disposition: **fix now**, together with F5–F7.
- **F9. Accepted behaviours to record, so later retros stop flagging them:**
  - The sync before a close isn't tracked as an abandoned write, though its timeout is marked retryable (`CloseNotebookUseCase.cs:151`; intended, `CloseNotebookToolTests.cs:213`).
  - `navigate_to_url`'s landing check runs after the pipeline, so the tool can outlast AD-7's 120 s (`NavigateToUrlUseCase.cs:32-33,120`).
  - A retry after a timed-out `quick_filing` can leave two pickers on screen (`QuickFilingUseCase.cs:98-100,161-178`; spike 4.13 F4). `LeftOpenUserAction` covers this for the agent.
  - Disposition: **accept**. Make sure the spine's AD-7 deadline wording says the landing check runs outside it.

### Aggregate views

- **F10. `AppStatePipeline` copies `WritePipeline`'s lock, deadline and settle code.**
  - Copied: `AbandonedWrites`, `ReleaseWhenSettledAsync` and `DeadlineExpired` (`AppStatePipeline.cs:157-223`; `WritePipeline.cs:189-198, 324-367`).
  - The sweep (4.11) didn't look at the pipelines.
  - Severity medium (the two copies will drift). Disposition: **defer** to a refactor ticket.
- **F11. `ComGateway` keeps growing: 1,749 lines, +478 net this epic** (window reads, the `NavigateInWindow` paths, and the whole picker-hold lifecycle at about lines 466–700).
  - Severity medium (maintainability only). Disposition: **defer**: split the partial class by concern, as code motion only, which keeps AD-2.
- **F12. Two answers to "is this path an open notebook's folder?"**
  - `OpenNotebookUseCase.Names` (`:204-228`) uses `GrantKeys`, the `\\localhost` mapping and `FilePathGate.Canonical`. `CreateHierarchyUseCase.cs:387-401` (round 2) uses `NotebookFolders`, which has no `\\localhost` mapping.
  - So `create_notebook`'s "open notebook's folder" refusal may miss a notebook opened through the admin share. Inferred; listed as unprobed in the 4.1 findings, line 116.
  - Disposition: **defer** to one shared function. Lesson: when a spike finds a new path form, check every existing path comparer.
- **F13. The tool-contract snapshot grew by about 5,800 lines** (`tests/OneNoteMcp.Tests/Contract/tool-contract.json`, now 35 tools).
  - About 363 of each tool's roughly 640 lines are the same `meta` schema. 17216b0 (4.6) alone added 2,691 lines by putting `meta.windows` into every tool's schema.
  - Severity low (review noise). Disposition: **defer**.
- **F14. Residue of the URL rule inside one class:** `OpenNotebookUseCase.Names` (`:206`) tests `CloudNotebookCreate.IsUrl`, while `IsUrl` in the same class (`:242-248`) uses the wider rule the sweep kept on purpose. Disposition: **accept**, with a comment.
- **F15. `get_windows` re-maps its pipeline's errors as retryable** (`GetWindowsUseCase.cs:38-50`). Disposition: **accept**, or move the rule onto `AppStateOperation.ChangesState`.
- **F16. AD-2 holds.**
  - One interop type is public by decision (`IQuickFilingDialogCallback`; spine AD-2, line 70). Core and Xml don't reference Com, and no new ProjectReferences or cycles were added.
  - Side effect: `spikes/stress-harness/StressHarness.csproj` must exclude `IQuickFiling*.cs`. Disposition: **accept**.
- **Conventions held:** tool registration, the gateway port's default `NotSupported` members, and the `*ToolTests.cs` shape. The sweep (478b8f0) already removed the duplication it scoped.

### Process

- **F17. The security pass again caught AD-21 and validation holes that the quick lens passed.**
  - 4.7: a withheld page revealed by the landing check, the scheme not checked (Review Triage Log, lines 159–161), and the encoded-title bypass (line 163).
  - 4.4, 4.5, 4.6, 4.9, 4.11 and 4.14 ran only the quick lens. This is round-2 lesson T1 again, and its action 9 hasn't landed (see Previous-retro follow-through).
  - Disposition: **fix now** (the process change).
- **F18. Probe sittings needed reruns for causes outside OneNote.**
  - 4.1: low-memory kills in sittings A and B, and a step timeout (findings, lines 41–45).
  - 4.12: four runs: a Security Notice, a low-memory kill, and pauses skipped in run 4 (findings, line 13). A probe bug was found only live (line 87; fixed in 92f38d4).
  - Disposition: **accept**, plus a lesson: check free memory before a sitting, and add fake-throw tests for COM exceptions mapped at runtime.
- **F19. Live sittings changed frozen behaviour or found what the unit tests couldn't:** the 4.7 Recycle Bin landing and the "=" rule (line 145), the 4.4 grant prompt unseen for about 7 minutes (line 145), and the 4.10 colour reset. This is round-2 lesson T2 again. Disposition: **accept**. Lesson: measure open questions before the build.
- **F20. A test race found at the 4.11 sitting became ticket 14, added mid-epic** (02847fb; fixed in 49fbe41; its guard's red check is at line 38 of the 4.14 plan). Disposition: **accept**. This is how it should work.
- **F21. 4.1 was never marked done** (Epic summary). Disposition: **fix now**. Only Phill marks it.
- **F22. Sitting scripts went off course.**
  - 4.10: Phill closed OneNote after misreading a step (line 121). The agent misstated a closed window and called `navigate_to` without offering first (lines 114–115).
  - Disposition: **defer**, as a lesson on how sitting steps are worded.
- **Wins:**
  - The Fable reviews drove the planning fixes: 20 of 20 on the epic file, 13 on the 4.1 findings, and 2–19 plus D1–D3 on the spine.
  - 4.13 ran clean in one run. 4.3 and 4.6 passed all their sitting steps.
  - The 4.14 guard and the 4.10 coverage test were both shown red first.
  - Every check on A1–A10 that was run came out met (Behavior verification, and the spec pass's "checked and met" list).

## Behavior verification

- **Exercised by the stories' live sittings** (each recorded in its plan): sync, close and reopen with the grant kept (4.4, 4.5, and 4.10 step 7), `get_windows` (4.6), `navigate_to_url` including the window argument (4.7), `quick_filing` including idle release with a picker open (4.9), and the combined integration run with no skips (4.14).
- **Exercised end to end for this retro** (2026-11-26, `onenote-dev` built from 4.11's code; 4.14 changed tests only). None of these calls takes focus or changes the view:
  - `get_windows` listed one window with MCP Test's IDs.
  - `sync_hierarchy` on MCP Test returned `ok` with the `syncRequested` warning.
  - `open_notebook` refused a cloud URL, a `.one` section file and a missing path, each before COM with `InvalidArgument`.
  - `navigate_to_url` refused a local `onenote:///` link, and a cloud link into a notebook that isn't open, each with `InvalidArgument` and nothing shown.
  - Together these support Done when 3. Calls that take focus weren't repeated.
- **Static checks:** "user-visible" is in all five AD-11 descriptions (`CloseNotebookTool.cs:24`, `NavigateToTool.cs:24`, `NavigateToUrlTool.cs:27`, `OpenNotebookTool.cs:24`, `QuickFilingTool.cs:24`). `force: false` is the only `CloseNotebook` call (`ComGateway.cs:716`). The coverage table has 29 rows for slots 7–35, enforced by `CoverageTableTests`.

## Previous-retro follow-through

From `epic-round-2-external-and-structural-retrospective.md`, Action items (lines 221–232):

1. Tag `v0.9.0-rc.1` (Phill): **landed**. The tag is on 4abcaa1, 2026-11-19.
2. Record the 3.10 sitting's parts B and C (main session): **landed** (18c9a98, 429bba4).
3. Re-run 3.7 S1, or record its acceptance (Phill and main session): **no evidence found**. `story-publish-notebooks-and-section-groups-plan.md:142` still says "Not yet re-run".
4. and 5. Reconcile E3, Done when 1 and E5 (main session): **landed** (backlog 20, fc636c0).
6. Re-resolve tests (next lane): **landed** (backlog 18, 2cb7a9c).
7. Restore and export messages (next lane): **landed** (backlog 19, 8679952).
8. Refactor story D1–D3 and P1: **landed** (backlog 17, 084014a).
9. A "no read grant" probe in the quick lens or the plan template (Phill): **no evidence found**. No match in `_bmad/custom`, the `*.user.toml` overrides or `~/.claude/bmad`.
10. Teach the fake the new behaviour, and require a red-run note for guard tests (Phill): **no evidence found as a rule**. It was done case by case (4.14 plan, line 38; 4.10 plan, line 137).
11. Record `end_revision` when a plan is marked done (Phill): **no evidence found**. This retro had to work out the ranges itself.
12. The "age" of opened copies (Phill): **landed** (backlog 21, 2cb7a9c).

## Action items

Every item is proposed. Nothing here has been applied. Phill chose to carry items 1, 2, 3, 4, 5 and 8 (Inbox, 2026-11-26). Items 6, 7, 9 and 10 are recorded but not carried.

1. **Tag the round-3 release** (`v0.10.0-rc.1` through the epic 1 pipeline; decide whether the contract version moves from "0.10"). Owner: Phill. Closes F1 and Done when 5.
2. **Mark 4.1 done** with a chore PR, as for 4.12 and 4.13. Owner: Phill decides; the main session opens the PR. F21.
3. **Spine-sync chore:** close the five Deferred rows in F2, note the `quick_filing` caps (F3), and make AD-7's deadline wording say the landing check runs outside it (F9). Owner: main session. This is a spine reconciliation awaiting Phill's approval.
4. **Round-3 follow-up story:**
   - the reopen route (F5);
   - the shared screen-text check for `quick_filing` (F6);
   - the `InvalidXml` wraps in `close_notebook` (F7);
   - the "still open" wording (F8).
   - Owner: next build lane. This is proposed remediation.
5. **Refactor backlog ticket:**
   - a shared lock and deadline core for the two pipelines (F10);
   - a split of `ComGateway` by concern (F11);
   - one "same notebook folder" function (F12);
   - the contract snapshot's repeated envelope (F13).
   - Owner: Phill decides when.
6. **Process: land round-2 action 9 at last.** Put an AD-21 "no read grant" probe in the quick lens or in the plan template's Boundaries (F17). Owner: Phill.
7. **Process: round-2 actions 10 and 11.** A red-run note for guard tests, and `end_revision` when a plan is marked done. Owner: Phill. Carried over.
8. **Process: sitting hygiene.**
   - Check free memory before a sitting (F18).
   - Every sitting record names the Deferred rows it closes (F2).
   - Steps that close or reopen anything say so in bold (F22).
   - Owner: main session.
9. **Process: a release line in the refactor-sweep template**, or a release ticket per epic (F1). Owner: Phill.
10. **Round-2 carry-over:** re-run 3.7 S1, or record that it is accepted as it stands. Owner: Phill.

## Acceptance verdict

- **Machine verdict: rejected** (criteria declared).
  - Done when 1–4 are met in the evidence:
    1. The four AppState tools work through the pipeline and the grant survives a reopen (the 4.4, 4.5 and 4.10 sittings).
    2. The user-visible wording and the AD-21 windows filtering are in place.
    3. Refusals before COM, exercised today.
    4. The events Decision (spine line 912) and the coverage table, enforced in CI.
  - **Done when 5 isn't met:** no GitHub release contains round 3 (F1).
  - No ticket is unfinished. No blocking code finding stands: F5 is medium and does no harm to data.
- **Human decision (overrides): accepted-with-open-items.** Phill, Inbox, 2026-11-26: "Accept with open items (the release is an open item)". The open items are action items 1–5 and 8.

## Open questions

- Does the contract version move with the release (0.10 → 0.11), since round 3 added six tools? Phill decides when tagging (action item 1).
