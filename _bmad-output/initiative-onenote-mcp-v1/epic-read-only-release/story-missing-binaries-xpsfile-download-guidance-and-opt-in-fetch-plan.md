---
title: 'Missing binaries: XPSFile, download guidance and opt-in fetch'
type: 'feature'
ticket: '21'
created: '2026-11-07'
status: done
baseline_revision: 'e0b62d6d94c1a0c28ff73a4fdd5ec6adc285891a'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/CLAUDE.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Images in cloud notebooks are often not on this PC (spike 1.15: 86 of 90 and 177 of 188 in two real notebooks). get_binary_page_content then returns `binaryUnavailable` with no way to get the image. `one:XPSFile` CallbackIDs (file printouts) fail with `ObjectNotFound` and a misleading "use a current ID" userAction.

**Approach:**
- Add a `navigate_to` tool, the user-visible `NavigateTo` call (AD-11).
- The skill and the `binaryUnavailable` userAction offer it only when the user asks: show the page, then read the image again.
- Turn the XPSFile `ObjectNotFound` into a `binaryUnavailable` warning that says what it is.

## Boundaries & Constraints

**Always:**
- Probe result (2026-11-07, Phill approved): `NavigateTo` on a read page in a real cloud notebook made both of its unavailable images readable within 2 s (`--probe-navigate`, harness).
- AD-11: `navigate_to` says "user-visible: takes focus" in its description, and the skill calls it only when the user explicitly asks. No use case calls `NavigateTo` internally, so the read tools never fetch by themselves.
- `navigate_to` is an `AppState` call (AD-7). It returns no content.
- The `binaryUnavailable` userAction names OneNote's File > Options > Sync > "Download all files and images", and says the page can be shown in OneNote on the user's request (`navigate_to`), then the read retried.
- An XPSFile CallbackID's `0x80042014` becomes `binaryUnavailable`, with a message saying file printouts can't be read through the API. Only that case changes: the server confirms it with one piBasic read of the page, on that error path only. Any other `ObjectNotFound` stays an error.
- New tool means a contract change: minor version bump, 0.5 → 0.6 (AD-14).

**Never:** a background or automatic fetch; calling NavigateTo from any read tool; touching a real notebook in tests (the live check below needs Phill's go-ahead and his presence).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Show a page | `navigate_to(pageId)` | OneNote shows the page; `ok: true`, no body | COM errors mapped as today (ObjectNotFound, ModalDialogOpen, ...) |
| Object on a page | `navigate_to(pageId, objectId)` | OneNote shows and selects it | as above |
| Image not on this PC | `get_binary_page_content` → `0x8004200F` | `binaryUnavailable` with the Sync setting and the `navigate_to` route in userAction | — |
| XPSFile CallbackID | `0x80042014` and the callbackID is a page `one:XPSFile`'s | `ok: true`, `binaryUnavailable` warning naming file printouts | — |
| Stale CallbackID | `0x80042014`, not an XPSFile | `ObjectNotFound` error, as today | — |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Com/Interop/IApplication.cs:88-91` -- `NavigateTo(bstrHierarchyObjectID, bstrObjectID, fNewWindow)` is already declared. Add `NavigateToAsync` to `IOneNoteGateway` (Core), `ComGateway` (STA thread, AD-3 timeout, HRESULT map), and `FakeGateway` (records calls).
- `src/OneNoteMcp.Core/UseCases/GetBinaryPageContentUseCase.cs:24-44` -- the `0x8004200F` catch and its userAction. Add the `0x80042014` path: read piBasic, and if a `one:XPSFile`'s `one:CallbackID@callbackID` equals the callbackId, return `binaryUnavailable`; otherwise rethrow.
- `src/OneNoteMcp.Core/UseCases/GetPageContentUseCase.cs` -- the per-image `binaryUnavailable` warning's userAction gets the same wording.
- `src/OneNoteMcp.Core/OperationClass.cs` -- `AppState` exists. The new use case declares it.
- `src/OneNoteMcp.Server/Tools/*Tool.cs`, `ServerSetup.cs` -- new `NavigateToTool` (`navigate_to`; `ReadOnly=false`, `Destructive=false`, `Idempotent=true`, `OpenWorld=false`, and `[McpMeta]` as the other tools). Its description starts "User-visible: takes keyboard focus and changes what OneNote shows. Call only when the user explicitly asks."
- `tests/OneNoteMcp.Tests/Contract/tool-contract.json`, `ToolContractTests.cs` -- add the tool, contract 0.6. `Directory.Build.props` → `0.6.0-rc.1`.
- `plugin/skills/guide/SKILL.md` -- a "Missing images" recipe: tell the user about the Sync setting, or ask whether to show the page; only on a yes call `navigate_to`, then retry `get_binary_page_content`. A packaging test requires the skill to name every tool.
- `spikes/stress-harness/Program.cs:75-83` -- the tool-count check (ten, all read-only) must accept `navigate_to`, and the harness must never call it.
- `spikes/stress-harness/ProbeNavigate.cs` -- the probe, already written; keep it and document `--probe-navigate` in the harness README.
- `tests/OneNoteMcp.Tests/Fixtures` -- need a piBasic page with a `one:XPSFile` (synthetic is fine; the real ones are in private notebooks).

## Tasks & Acceptance

**Execution:**
- [x] Gateway: `NavigateToAsync` in Core's port, ComGateway and FakeGateway.
- [x] `NavigateToUseCase` + `NavigateToTool`, registered; contract and version bump.
- [x] `GetBinaryPageContentUseCase` and `GetPageContentUseCase`: the XPSFile path and the new userAction text.
- [x] Skill recipe; README: missing images, the Sync setting, `navigate_to` as user-visible.
- [x] Harness: accept the new tool, document `--probe-navigate`; record the probe result in the spike 1.15 findings' image section.
- [x] Unit tests for every matrix row with the fake gateway, contract snapshot, packaging test.

**Acceptance Criteria:**
- Given the unit and integration suites, when they run, then they pass and no test calls `NavigateTo` against real OneNote.
- Given Phill at the machine and his go-ahead, when Claude Code is asked to fetch a missing image on a real cloud page, then it calls `navigate_to` only because he asked for the image (a request for the image is the explicit request, per AD-11; Phill, 2026-11-07), and the retried `get_binary_page_content` returns the image.

## Implementation Notes

- `navigate_to`'s parameters are `hierarchyId` and `pageContentObjectId` (optional), mirroring `get_hyperlink_to_object` and COM's own arguments, since `NavigateTo` takes any hierarchy object, not only a page. `fNewWindow` is always false and isn't exposed.
- `ComGateway.NavigateToAsync` runs with `retryOnDisconnect: false`: it's `AppState`, not a read, so a call that reached COM isn't repeated (AD-3). For `AppState`, `Timeout` and `ComUnavailable` are not retryable (AD-9's existing table).
- The XPSFile check is `OneNoteMcp.Xml.XpsFiles.IsXpsFileCallbackId`: a `one:XPSFile` child of the `one:Page` root (the only place the XSD allows it). If the confirming piBasic read fails or its XML can't be parsed, the original `ObjectNotFound` is rethrown unchanged.
- The XPSFile fixture is hand-written (`Fixtures/synthetic/`, manifest `source: synthetic`, which neither capture removes) and validates against the XSD.
- The stress harness's binaries phase now expects `ok` + `binaryUnavailable` from the tool where the raw call gives `ObjectNotFound` on a page-level XPSFile CallbackID; otherwise a real-notebook run would count every XPSFile as a mismatch. Its tool client refuses `navigate_to`.
- The skill also gained `navigate_to` in the tool map, and a rule never to retry it without asking again.

- Verification (2026-11-07): build clean; 988 unit tests; integration 86 passed, 7 skipped; stress harness on MCP Test 617 comparisons, 0 mismatches, eleven tools listed, `navigate_to` never called.
- Live check (Phill, 2026-11-07, Claude Code 2.1.319 with only the 0.6.0-rc.1 server, no skill loaded): asked to get a missing image from a read page in notebook A. It found one, called `navigate_to` (it said it was treating the request as permission), and the retried `get_binary_page_content` returned the image (PNG, 558×484). No page text or image data appeared in chat. It did not ask before navigating. Decision (Phill, 2026-11-07): accept as is; a user asking for the image counts as the explicit request (AD-11), so AC 2's wording was changed to match.

## Plan Change Log

## Review Triage Log

Pass 1 (quick lens): high 0, medium 2, low 2, rejected 1.
| # | Finding | Verdict | Route | Evidence / action |
|---|---|---|---|---|
| 1 | AC2, the integration run and the harness run aren't recorded | — | rejected | The fix edits this plan. Results go in Implementation Notes anyway: unit 986, integration 86/7 skipped, harness 617 comparisons and 0 mismatches. AC2 is pending Phill's interactive check. |
| 2 | NavigateTo with no OneNote window returns ok and shows nothing, so the skill's guidance misleads | medium | patch | Epic open question (epic file :66). Document the window requirement; the skill tells the user to bring OneNote up instead of retrying. |
| 3 | OneMore row 14: a quick repeat call gives 0x80042014, and the first window is targeted | medium | patch | 0x80042014 maps to ObjectNotFound with a misleading userAction. Remap the userAction in NavigateToUseCase, fix the window wording, add a test. |
| 4 | "Downloads within seconds" overstates one measured page | low | patch | Reword to what was measured. |
| 5 | A harness README sentence is spliced into the exit-code list | low | patch | Move it after the list. |

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror`; `dotnet test tests/OneNoteMcp.Tests`; `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration` -- all pass.
- Stress harness on MCP Test -- 0 mismatches; it lists eleven tools and never calls `navigate_to`.

**Manual checks:**
- The live check in AC 2, run with Phill.
- The name grep on the diff finds nothing.
