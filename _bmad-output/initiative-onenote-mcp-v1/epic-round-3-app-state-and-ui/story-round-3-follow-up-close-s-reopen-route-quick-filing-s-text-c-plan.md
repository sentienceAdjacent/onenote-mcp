---
title: 'Round-3 follow-up: close''s reopen route, quick filing''s text check, close''s XML reads and its failure wording'
type: 'bugfix'
ticket: '15'
created: '2026-11-26'
status: 'built'
baseline_revision: '81909f7be4b7fb81d9c614ddd4cad6754f6ef37b'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The round-3 retrospective's fix-now findings F5-F8: `close_notebook` tells every non-http notebook to reopen through `open_notebook`, which refuses network, device, relative and OneDrive paths before COM (F5); `quick_filing`'s title/description check misses invisible formatting and line/paragraph separators that `FilePathGate` already refuses (F6); `close_notebook`'s two hierarchy reads map malformed XML to `Internal` instead of AD-9's `InvalidXml` (F7); and every non-ambiguous close failure claims "The notebook is still open." (F8).

**Approach:** One path rule, `NotebookFolderPath` beside `FilePathGate`, answers "would `open_notebook` take this folder path?"; `open_notebook` uses it and `close_notebook` picks its reopen route from it (entry 16 builds its notebook-folder function on it). One shared screen-text check serves `FilePathGate` and `quick_filing`. Two `XmlReads.Readable` wraps. The "still open" sentence only for failures where OneNote refused the close. Approved by Phill with the story, 2026-11-26.

## Boundaries & Constraints

**Always:**
1. `NotebookFolderPath` (`src/OneNoteMcp.Core/Writes/NotebookFolderPath.cs`, public static): `Canonical(path, links, out FilePathRefusal)` (delegates to `FilePathGate`), `InOneDrive(canonical, oneDrive)`, and `Openable(path, oneDrive, links)` = both. `open_notebook` calls the first two in its existing order, so its rule names, messages and order stay as they are.
2. `close_notebook`'s route: http path → `ReopenCloudUserAction` (unchanged); `Openable` → `ReopenLocalUserAction` (unchanged); otherwise a new `ReopenInOneNoteUserAction` (network, device, relative or OneDrive path, so the user reopens it in OneNote, File > Open > Browse; then re-read for new IDs). The check runs after the close succeeded and never fails the call.
3. Mapped network drives get a test seam: `FilePathGate.ILinkReader` gains a default-implemented `IsNetworkDrive(root)` (the real `DriveInfo` check), which `Classify` uses. Fakes that don't override it behave as today.
4. `ScreenText` (`src/OneNoteMcp.Core/Writes/ScreenText.cs`): `IsHidden(char)` = control, Format, LineSeparator, ParagraphSeparator. `FilePathGate.Classify` uses it plus its own `< > " | ? *`; `quick_filing`'s check uses it, its message still saying "control character" and naming invisible formatting characters.
5. F8: the sentence "The notebook is still open." stays for `ComBusy` and `ModalDialogOpen` (OneNote refused while holding the notebook, e.g. hrNotYetSynchronized); any other code says "Re-read the open notebooks to see whether it is still open." The HRESULT for a notebook closed by hand during the sync or settle was never probed (ticket unknown), so the wording doesn't depend on it.
6. Skill text (`plugin/skills/guide/SKILL.md` close row, `references/app-state.md` §6) says a notebook kept at a network, OneDrive or other path `open_notebook` refuses is reopened in OneNote, as the close's userAction says.

**Never:** a new tool or setting; a change to AD-7's pipeline, error codes, retryability or the close's COM steps; a change to `open_notebook`'s refusals, messages or rule names; the admin-share mapping (entry 16); spine edits; touching real OneNote.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error Handling |
|---|---|---|---|
| Close, local openable | `C:\Notebooks\Notebook A` | `ReopenLocalUserAction` | none |
| Close, refused forms | `\\server\share\NB`, `\\localhost\c$\NB`, `\\?\C:\NB`, mapped `Z:\NB` (fake), link to a server, OneDrive folder | `ReopenInOneNoteUserAction`; same path to `open_notebook` is `InvalidArgument` | none |
| Close, cloud | `https://…` | `ReopenCloudUserAction` | none |
| quick_filing text | U+2028, U+202E, U+200B in title or description | `InvalidArgument` before COM | not retryable |
| Close read 1 malformed | notebooks XML `<one:Notebooks` | `InvalidXml`, nothing synced or closed | AD-9 |
| Close read 2 malformed | unknown ID, self XML malformed | `InvalidXml`, nothing synced or closed | AD-9 |
| Close refused, ObjectNotFound | `CloseNotebook` throws ObjectNotFound | message lacks "still open", says to re-read | code kept, `CloseUserAction` |
| Close refused, ComBusy | as today | "The notebook is still open." | unchanged |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/Writes/FilePathGate.cs` -- `Classify` (:82-110) holds the hidden-character test (:96-99) and `IsNetworkDrive` (:112); `ILinkReader` (:203) is the test seam; `Canonical` (:42) and `ResolveLinks` call `Classify`.
- `src/OneNoteMcp.Core/UseCases/OpenNotebookUseCase.cs` -- `Open.Validate` makes the canonical path (FilePathGate at ~:260), `CheckOneDrive` (~:350) runs at step 1 and under the lock. `Names`/`MapLocalhostAdminShare` stay (entry 16's).
- `src/OneNoteMcp.Core/UseCases/CloseNotebookUseCase.cs` -- route at `ExecuteAsync` (:67-71), reads at :98 and :110, the "still open" catch at :173-176. Primary ctor `(gateway, pipeline, settle)`: keep that order, add `oneDrive` optional and an internal ctor with `links`.
- `src/OneNoteMcp.Core/UseCases/QuickFilingUseCase.cs` -- `Show.Check` (:268).
- `src/OneNoteMcp.Core/XmlReads.cs` -- `Readable(read, what)`; `what` is "GetHierarchy" as `OpenNotebookUseCase` uses it.
- `src/OneNoteMcp.Core/Access/OneDriveFolders.cs` -- `Holds(path)`.
- `src/OneNoteMcp.Server/ServerSetup.cs` (:123) -- register `CloseNotebookUseCase` with `OneDriveAccounts.Current`, as `OpenNotebookUseCase` is (:135-143).
- `tests/OneNoteMcp.Tests/CloseNotebookToolTests.cs` -- `Tool(...)` helper (~:395), `CloseOk_TheReopenRoute_FollowsThePathsScheme_InAnyCase` (:97; its `\\server` row expects the local route today and must change), `ARefusedClose_…` (:325).
- `tests/OneNoteMcp.Tests/OpenNotebookToolTests.cs` -- `Tool`, `FakeLinks`, `FakeFolders` (private); `StepOneRefusals` rows.
- `tests/OneNoteMcp.Tests/QuickFilingToolTests.cs` -- `ControlCharacters_AreInvalidArgument_BeforeCom` (:280).
- `tests/OneNoteMcp.Tests/Contract/tool-contract.json` -- unchanged unless a tool description changes (none planned).

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Core/Writes/ScreenText.cs`, `FilePathGate.cs` -- add `ScreenText`; `Classify` uses it; `ILinkReader.IsNetworkDrive` default member used by `Classify(path, links)`.
- [x] `src/OneNoteMcp.Core/Writes/NotebookFolderPath.cs` -- the path rule (Always 1).
- [x] `OpenNotebookUseCase.cs` -- use `NotebookFolderPath.Canonical`/`InOneDrive`; no behaviour change.
- [x] `CloseNotebookUseCase.cs`, `ServerSetup.cs` -- route (Always 2), wraps (F7), wording (Always 5), DI.
- [x] `QuickFilingUseCase.cs` -- `ScreenText` check and message.
- [ ] Skill files -- Always 6.
- [ ] Tests -- a `CloseReopenRouteTests` (or rows in `CloseNotebookToolTests`) theory that runs each refused form through `close_notebook` and `open_notebook` with the same fakes; local and cloud rows; `FilePathGate` mapped-drive row; quick_filing U+2028/U+202E/U+200B rows; two malformed-XML rows; ObjectNotFound vs ComBusy wording rows.

**Acceptance Criteria:**
- Given main before this change, when the new tests run, then each of the refused-route, U+2028/bidi, malformed-XML and ObjectNotFound-wording rows fails.
- Given the change, when the unit suite and contract snapshot test run, then all pass and `open_notebook`'s existing tests are unchanged.

## Implementation Notes

- Planning: the plan is about 2,100 tokens, over the 1,600 guide; kept whole, since the ticket bundles F5-F8 as one story by design. No open questions; approved by Phill with the story, 2026-11-26.
- The step-03 implementation subagent was cut off by the session's script time limit after writing the F7/F8 and quick_filing test rows; the main session kept those rows and implemented the rest directly from the plan.
- Files: new `ScreenText.cs`, `NotebookFolderPath.cs` (Core/Writes); `FilePathGate` (`Classify(path, links)`, `ILinkReader.IsNetworkDrive` default member); `OpenNotebookUseCase` (uses `NotebookFolderPath.Canonical`/`InOneDrive`); `CloseNotebookUseCase` (explicit ctors, `ReopenRoute`, `ReopenInOneNoteUserAction`, two `XmlReads.Readable` wraps, F8 wording); `QuickFilingUseCase`; `ServerSetup`; skill `SKILL.md` and `references/app-state.md`; tests `CloseReopenRouteTests.cs` (new), `CloseNotebookToolTests.cs` (the old `\\server` row that expected the open_notebook route is removed: it now lives in the round-trip theory with the OneNote route), `QuickFilingToolTests.cs`.
- Fails on main (2026-11-26, reversible: src files restored from a scratch copy, diff hash checked equal): the F7 and F8 rows fail; the quick_filing U+2028/U+2029/U+202E/U+200B rows reach the fake picker and hang (blame-hang at 60 s), so they don't pass; the round-trip tests don't compile on main (new API).
- Verification 2026-11-26: build 0 warnings; unit 4232 passed, 3 skipped, 0 failed (4235); integration without the variable 417 passed, 137 skipped, 0 failed (554). Contract snapshot unchanged and passing.
- After build (main session, 2026-11-26): an automated security review flagged that `ScreenText` checked each UTF-16 unit, so a hidden character outside the BMP (the U+E0000 tag block, category Cf) passed as two surrogates. `HasHidden` now reads by code point (`Rune`) and refuses a lone surrogate; `FilePathGate` calls `HasHidden`. `ScreenTextTests` covers tag characters, lone surrogates and a visible emoji. Unit suite: 4242 passed, 3 skipped.

## Plan Change Log

## Review Triage Log

**Pass 1 (quick lens, 2026-11-26):** 6 findings: high 0, medium 1, low 4, false 0, maybe-false 0, rejected 1 (plan edit).

| # | Finding | Verdict | Route | Evidence / action |
|---|---|---|---|---|
| 1 | `SKILL.md` close row names cloud, network, OneDrive only; Always 6 and `app-state.md` also cover other refused paths | low | patch | Row now says "a network, OneDrive or other path `open_notebook` refuses". |
| 2 | quick_filing test asserts only "control character", which the old message passed | low | patch | Added `Contains("invisible formatting character")`. |
| 3 | A regression in the new quick_filing rows hangs at the fake picker instead of failing | medium | patch | Rows now script a Cancel answer, so a text that slips through returns ok and `Error(...)` fails at once. |
| 4 | `CloseOk_TheReopenRoute_…` keeps a dead `cloud` parameter after its `\\server` row moved | low | patch | Renamed `CloseOk_TheCloudRoute_FollowsThePathsScheme_InAnyCase`, parameter and ternary removed. |
| 5 | `CloseNotebookToolTests.Tool` now reads the real file system and drive types for the reopen route | low | patch | Helper passes a `NoLinks` reader (no links, no network drives) through the internal ctor. |
| 6 | Plan task checkboxes unticked | — | rejected | Fix is an edit of this build's plan; ticked at "built" anyway. |

After patches: build 0 warnings; unit 4232 passed, 3 skipped, 0 failed; integration (no variable) 417 passed, 137 skipped, 0 failed.

## Design Notes

`Openable` touches the file system (links, drive type, OneDrive prefixes) read-only; it runs after the close, so a failure there falls to the OneNote route rather than failing the tool. `\\localhost\X$` paths get the OneNote route here, because `open_notebook` refuses them today; entry 16 may map them.

## Verification

**Commands:**
Run from the worktree root `D:/Development/OneNote COM MCP/.claude/worktrees/s415` with `--artifacts-path "C:/Users/phill/AppData/Local/Temp/claude/2e954d91aa84c703/421e0750-1c29-4aff-a08b-1fe8f7985a3f/scratchpad/art415"` on every command. Never set `ONENOTE_MCP_INTEGRATION`, never touch real OneNote or `%APPDATA%\OneNoteMcp`.

- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass.
- `dotnet test tests/OneNoteMcp.Integration` (no `ONENOTE_MCP_INTEGRATION`) -- expected: all skip or pass, none fail.
