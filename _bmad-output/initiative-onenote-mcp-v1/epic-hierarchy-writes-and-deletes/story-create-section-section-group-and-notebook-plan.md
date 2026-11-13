---
title: 'Create: section, section group and notebook'
type: 'feature'
ticket: '3'
created: '2026-11-12'
status: 'built'
baseline_revision: '008ad0a2dfc611c89d1eb6a6f03504bd3efa3f96'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
warnings: ['oversized']
deferred:
  - summary: >-
      ComGatewayLifecycleTests.AWriteStillQueuedAtItsOwnDeadline_NeverRan_AndIsNotTracked is timing-flaky (failed 1 run in 6 on 2026-11-13).
    evidence: |-
      Seen during 5.3's pre-commit verification: 2 of 8 full runs of tests/OneNoteMcp.Tests failed on this one test (406 ms), every other run passed 2469/2469. The test dates from story 2.7 (commit 06524c3) and this diff doesn't change it; it races the STA thread against a short deadline and is sensitive to machine load.
    location: >-
      tests/OneNoteMcp.Tests/ComGatewayLifecycleTests.cs:899
    severity: low
---

<intent-contract>

## Intent

**Problem:** Claude can write pages but can't make the structure they live in. The gateway can create only a section, by parent ID, for `restore_from_journal`'s `Recovered` fallback (2.11); there is no tool for a section, a section group or a notebook, and the automatic grant a new notebook gets (AD-6 `grantedVia: created`) has no writer.

**Approach:** `OpenHierarchy` create through the existing pipeline as `Create` (AD-7 table and its "`Create` validation for `OpenHierarchy` create" rules). Sections and groups by parent ID plus a relative name, never an absolute path. A notebook at a local folder the server checked is absent or empty, with its write grant written after the COM call under `Local\OneNoteMcp.Settings` inside the write lock, else `ok: true` with `createdNotGranted`. The gateway gains `CreateSectionGroupAsync` and `CreateNotebookAsync` beside 2.11's `CreateSectionAsync`.

## Boundaries & Constraints

**Always:**
- AD-7 order. Step 1 (before the lock): the name check (below), then for a section or group the write grant on the parent's notebook, the parent itself when it is a notebook (grant dialog as for `create_page`). A notebook create needs no grant, but `settings.json` must be writable (`SettingsInvalid` otherwise, AD-10), so the auto-grant can be saved.
- Names, checked before COM with `InvalidArgument` and a message that never quotes the name: one file-system name, no `..`, `\`, `/`, control or `< > : " | ? *` characters, no reserved device name, no leading or trailing space or dot (`SectionFileNames.Problem`, generalised). A section reaches COM ending `.one` (Q2). `OneNote_RecycleBin` and `OneNote_DeletedPages` are refused for every kind, ignoring case.
- Step 3 under the lock reads the parent at `Children` scope (section, group: one `GetHierarchy` of the parent). Step 4: the parent must be a live notebook or section group; a section, a page, the recycle bin (`isRecycleBin`), or anything in it (`isInRecycleBin`, `isDeletedPages`) is `ObjectNotFound` before COM, `userAction` asking for a notebook or section group ID.
- Existing name: a live child of the same kind whose name equals the new one ignoring case gives `ok: true`, that child's ID, the warning `alreadyExisted`, and no COM call (AD-7). The other kind's same name follows Q4.
- Notebook folder (Q3 sets where): made canonical with `FilePathGate.Canonical` (links resolved; UNC, mapped network and device paths refused, `InvalidArgument`). A URL is `NotSupported` (cloud create unprobed). The notebook folder must not exist, or exist with no entries at all, hidden ones included; else `FileExists` before COM. Refused with `InvalidArgument`: a folder inside or containing an open notebook's local path, or inside OneNoteMcp's own folders (the settings folder, the journal folder, `%LOCALAPPDATA%\OneNoteMcp`). Checked under the lock, against a live `GetHierarchy` of open notebooks.
- Auto-grant, a step between 7 and 8 (`AfterCallAsync`): read the new notebook at `Self`, take its `path`, `NotebookKey` it, and `SaveWriteGrant(write: true, permanentDelete: false, grantedVia: created)`. Only when that key equals the key of the folder the server checked and the returned ID wasn't open before the call; otherwise, or when the read-back or the settings write fails (`ComBusy`, IO), `ok: true` with `createdNotGranted`, whose `userAction` names the grant CLI (never run by the model). The write lock is always outside the settings mutex (AD-7).
- A `Timeout`, `ComUnavailable`, `Cancelled` or `0x800706BE` in step 7 is `outcomeUnknown` (AD-9, pipeline as is); a notebook's message adds that a notebook made this way has no grant yet.
- Results (Q5): the new or existing node's ID, and its parent's for a section or group. Messages and logs carry IDs only (AD-9, AD-13); never a name or a path, except the notebook's own read-back path in the success result, which the caller gave.
- Tool contract snapshot updated; `<Version>` stays `0.9.0-rc.1`. Tool rows only in the guide skill and README (recipes are entry 7's). Spine sync goes to `deferred-work.md`.

**Never:** `OpenHierarchy` on an existing path (that is `AppState`, epic 4); a section or group from an absolute path; a cloud (URL) notebook; renaming, moving or deleting anything; `NavigateTo`; automatic retries; a grant from any tool other than the post-create auto-grant; a read grant; editing `tickets.toml` (F15 refinement is `bmad-ticket`'s); spine edits; touching OneNote, setting `ONENOTE_MCP_INTEGRATION`, or running the server exe.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Section, granted | notebook or group ID, `Ideas` | `OpenHierarchy("Ideas.one", parentId, cftSection)`; new ID | No error expected |
| Group, granted | parent ID, `Archive` | `cftFolder`, new ID | No error expected |
| Notebook | `Trips`, folder absent | created; grant `created`; `list_grants` shows it | No error expected |
| Ungranted parent | section create | grant dialog, then as above or `WriteNotGranted` | no lock, no COM |
| Existing same-kind name | `ideas` beside `Ideas` | existing ID, `alreadyExisted` | no COM |
| Bad name | `a/b`, `..`, `x?`, `CON`, ` x`, `OneNote_RecycleBin` | `InvalidArgument`, name not quoted | before the lock |
| Parent wrong | section, page, recycle bin, recycled group | `ObjectNotFound` + `userAction` | before COM |
| Notebook folder not empty / an open notebook's | | `FileExists` | before COM |
| Notebook folder inside a notebook or OneNoteMcp folder; UNC; device | | `InvalidArgument` | before COM |
| Notebook URL | `https://…` | `NotSupported` | before COM |
| Read-back or grant save fails | | `ok: true`, `createdNotGranted` | never fails the create |
| Settings invalid | any create | `SettingsInvalid` | step 1 |
| COM timeout | any | `outcomeUnknown: true` | no retry |

</intent-contract>

## Answers (Phill, 2026-11-13, Inbox thread "5.3 create hierarchy")

- Q1: three tools, `create_section`, `create_section_group` and `create_notebook`. A `kind` argument would only shorten the tool list, at the cost of conditional arguments.
- Q2: add `.one` to a section name unless the name already ends with it.
- Q3: `create_notebook` asks the user where the notebook goes, in a dialog that offers the user's OneDrive (a cloud notebook, created by URL) or a local folder. The user picks the location, so the new notebook's no-dialog write grant is safe. Cloud is the common case. **It needs a live probe** at the sitting: can COM create a notebook at a OneDrive URL? If it can't, the dialog falls back to local folders only.
- Q4: a section and a section group with the same name under one parent are refused with `InvalidArgument`.
- Q5: a create returns `meta.objectId`, plus `meta.parentId` (section, group) or `meta.path` (notebook).
- Q6: live notebook creation runs in an opt-in integration test that makes a throwaway notebook under the test folder; Phill closes and deletes it afterwards.
- Q7: approved with these answers.

## Build Directions (unattended build, 2026-11-13)

These settle what the answers leave to the build. They are binding for this build and sit beside the Answers; where the intent contract above predates an answer, the answer and these directions win. Three of them began as assumptions; Phill confirmed all three as decisions on 2026-11-13 (marked **Decision (Phill, 2026-11-13)**): the name rule for groups and notebooks, `NotSupported` without dialog support, and the editable, prefilled folder field.

- **Name rule for groups and notebooks (the spine's AD-7 open point, PR #43). Decision (Phill, 2026-11-13).** The whole section rule applies to section groups and notebooks too: one file-system name; no `..`, `\`, `/`, control or `< > : " | ? *` characters; no reserved device name (`CON`, `NUL`, `COM1`…, also with an extension, as `SectionFileNames` already does); no leading or trailing space or dot; `OneNote_RecycleBin` and `OneNote_DeletedPages` refused ignoring case. Reason: a group is a folder and a notebook is a folder, so Windows has the same limits, and a trailing dot or space makes a folder Explorer and OneNote handle badly. Only a section gets `.one` (Q2). Record this choice in `deferred-work.md`'s spine-sync item.
- **`create_notebook(name, folder?)` and its location dialog (Q3).** The tool takes the notebook's name and an optional parent folder. In step 1, after the name check and the settings-writable check, the server asks the user where the notebook goes through a new Core port (beside `IGrantPrompt`, for example `INotebookLocationPrompt`) with an MCP elicitation adapter in `src/OneNoteMcp.Server/Grants/`. The form names the notebook and offers a `location` choice and an editable `folder` field (**Decision (Phill, 2026-11-13)**: editable and prefilled) prefilled with the caller's `folder` or, when absent, OneNote's `DefaultNotebookFolder` if that is a local path (blank otherwise, and then required). The notebook goes in `<chosen folder>\<name>`, and every folder rule in the intent contract runs on the folder the user chose, never only on the one the model proposed (the implementer may also pre-check the proposal to avoid asking about a folder that would be refused).
  - The `location` choice offers `local` always, and `oneDrive` only when the cloud seam (below) is open. In this build it is closed, so the dialog offers the local folder only.
  - Declined or cancelled: `Cancelled`, no lock, no COM.
  - **Decision (Phill, 2026-11-13):** a client without elicitation gets `NotSupported` with a `userAction` (create the notebook in OneNote, or use a client that shows dialogs), and nothing is created. The no-dialog auto-grant is only safe because the user picked the location, so without a dialog there is no create.
- **The cloud (OneDrive URL) seam.** Whether COM `OpenHierarchy(url, "", cftNotebook)` can create a notebook at a OneDrive URL is unproven; a live probe runs at the next sitting. Build the local path fully. Put the URL path behind one clearly marked seam in Core (for example `CloudNotebookCreate` with `Enabled => false` and a comment `SEAM(5.3 cloud probe)` saying what the probe must show before it opens), consulted by the dialog (whether to offer OneDrive) and by the use case (a URL `folder`, or a `oneDrive` answer, is `NotSupported` while it is closed — the intent contract's "A URL is `NotSupported`"). Where the OneDrive base URL would come from (the default notebook folder when it is a URL, or an open cloud notebook's parent) is part of the probe; the seam leaves it unimplemented. No code path sends a URL to COM in this build.
- **The probe, written but not run.** `tests/OneNoteMcp.Integration/CreateHierarchyIntegrationTests.cs` gets, beside Q6's local-notebook test and the section and group tests, an opt-in probe test (skipped unless `ONENOTE_MCP_INTEGRATION=1`, like the others) that tries `OpenHierarchy` `cftNotebook` at a throwaway OneDrive URL built from the cloud test notebook's parent URL, records what came back (ID or HRESULT, the read-back `path`, whether the notebook syncs), and prints the throwaway's name for Phill to close and delete. It must not be run in this build.
- **This build never touches OneNote.** No integration tests run, no COM, no starting, closing or killing OneNote, no `ONENOTE_MCP_INTEGRATION`, no server exe. Only `dotnet build OneNoteMcp.slnx -warnaserror` and `dotnet test tests/OneNoteMcp.Tests`.
- **Privacy.** No real notebook, employer or person names in code, tests, fixtures or docs; use "notebook A/B/C", "MCP Test" and "MCP Test Cloud".

## Open Questions

1. **Tool shape.**
   - (a) Three tools: `create_section(parentId, name)`, `create_section_group(parentId, name)`, `create_notebook(name, folder?)`.
   - (b) `create_section(parentId, name, kind: section|sectionGroup)` and `create_notebook`.
   - (c) One `create_hierarchy(kind, parentId?, name, folder?)` mirroring COM.
   Recommended: (a). Each description states one kind's rules, the notebook's (no grant, auto-grant, folder rules) stay apart from the others, and the schema can't express a notebook with a parent ID. Three short tools cost little context.
2. **The `.one` extension** (AD-7 leaves it to this story). The caller passes the name as OneNote shows it.
   - (a) The server always appends `.one`, so `Notes.one` makes a section shown as `Notes.one`.
   - (b) Appended unless the name already ends `.one` (ignoring case), which is then sent as is.
   - (c) A name ending `.one` is refused with `InvalidArgument`; otherwise appended.
   Recommended: (b). The model will sometimes send the file name; (b) does what it meant, at the cost of not being able to make a section literally named `x.one`.
3. **Where a notebook may be created.** AD-7 says "an absolute path verified empty" but not which paths. A created notebook gets a write grant with no dialog, so the location is the security surface.
   - (a) Any absolute local folder the caller names, under the rules above.
   - (b) Name only: always under OneNote's default notebook folder (`GetSpecialLocation` `DefaultNotebookFolder`, already on the gateway); refused if that is a URL.
   - (c) `create_notebook(name, folder?)`: `folder` defaults to (b)'s, and when given must be an existing local folder; the notebook goes in `folder\name`, under the rules above.
   Recommended: (c). (b) alone has no place for the live test's throwaway notebook (`%LOCALAPPDATA%\OneNoteMcp-Test`, as spike 5.1 did) or a user's chosen folder; (a) takes a full path where (c) splits name from folder, so the name check covers the last segment. Either way the server refuses folders inside open notebooks and OneNoteMcp's own folders.
4. **A section and a group with the same name under one parent.** On disk `Foo.one` and folder `Foo` can coexist; OneNote shows both as `Foo`. OneMore's add-group command treats sections and groups as one name space. A group's existing-name behaviour is unprobed (spike 5.1).
   - (a) Same kind only: the same name on the other kind is created.
   - (b) Any kind: the other kind's same name is refused with `InvalidArgument` before COM.
   Recommended: (b). Two siblings shown under one name confuse the user and later renames; refusing is cheap to relax.
5. **Result fields.** `create_page` returns `meta.pageId`.
   - (a) A new `meta.objectId` (the created or existing node) plus `meta.parentId` for a section or group, and `meta.path` (read back) for a notebook. Entry 4's and 5's tools can reuse `objectId`.
   - (b) Per kind: `meta.sectionId`, `meta.sectionGroupId`, `meta.notebookId`.
   Recommended: (a). One field for "the node this call made or touched" keeps epic 5's contract small.
6. **The live check of a notebook create.** COM can't delete a notebook (spike 5.1 constraint 18), so each run leaves one open.
   - (a) The opt-in integration test creates `MCP Test Throwaway <stamp>` under `%LOCALAPPDATA%\OneNoteMcp-Test\` with a test `settings.json`, checks `list_grants` shows `grantedVia: created`, and leaves it for Phill to close and delete (printed at the end).
   - (b) No automated notebook create; Phill runs it once through a dev server registration.
   Recommended: (a). It repeats the spike's guarded throwaway pattern and gives the Verify a repeatable test.

## Code Map

- `src/OneNoteMcp.Core/IOneNoteGateway.cs` -- add `CreateSectionGroupAsync(parentId, name)` and `CreateNotebookAsync(folderPath)`, both `Create` writes; doc comment lists them.
- `src/OneNoteMcp.Com/ComGateway.cs` (`CreateSectionAsync`, l.259) -- pattern to copy: argument guard before COM, `CallKind.Write`, never retried. `Interop/Enums.cs`: `cftFolder = 2`, `cftNotebook = 1` exist.
- `src/OneNoteMcp.Core/SectionFileNames.cs` -- extract the name rules for groups and notebooks (no extension); `Problem` keeps its contract for 2.11's caller.
- `src/OneNoteMcp.Core/UseCases/CreatePageUseCase.cs` -- the `Create` operation shape (`WriteOperation<T>`: `AuthorizeAsync`, `ReadAsync`, `Validate`, `CallAsync`); `RestoreFromJournalUseCase.cs` l.307 is the only `CreateSectionAsync` caller, unchanged.
- `src/OneNoteMcp.Core/Writes/WriteOperation.cs`, `WritePipeline.cs` -- `AfterCallAsync`/`AfterCallFailed` host the auto-grant; no runner change expected.
- `src/OneNoteMcp.Core/Access/WriteAccess.cs` -- `CheckAsync` walks to the owning notebook, and `HierarchyWalk.OwningNotebookAsync` returns null for a notebook itself, so add a check for a container parent that may be the notebook; `CheckNotebook` exists for a known notebook.
- `src/OneNoteMcp.Core/Settings/SettingsWriter.cs` -- `SaveWriteGrant` takes the settings mutex itself; `GrantedVia.Created` exists (`Access/GrantPrompt.cs`) and `SettingsStore` already accepts it (l.197).
- `src/OneNoteMcp.Core/Writes/FilePathGate.cs` -- `Canonical(path, out refusal)` for the notebook folder.
- `src/OneNoteMcp.Xml/HierarchyNode.cs`, `NotebookSections.cs` -- `HierarchyNode` lacks `isRecycleBin`; add a small Xml reader for a parent and its live children (kind, ID, name), recycle bin excluded.
- `src/OneNoteMcp.Core/Results/ToolWarnings.cs`, `ToolMeta.cs` -- `alreadyExisted`, `createdNotGranted`; meta per Q5.
- `src/OneNoteMcp.Server/Tools/CreatePageTool.cs`, `ServerSetup.cs` (l.117, l.163) -- tool and registration pattern, `ElicitationGrantPrompt.For(server)` for section and group only.
- `tests/OneNoteMcp.Tests/FakeGateway.cs` (`CreateSectionCalls`, l.178), `FakeApplication.cs` (`OpenHierarchy`, l.91), `CreatePageToolTests.cs`, `McpSdkBindingTests.cs`, `ToolContractTests.cs` + `Contract/tool-contract.json`.
- `tests/OneNoteMcp.Integration/CreatePageIntegrationTests.cs`, `TestNotebook.cs` -- opt-in pattern, test settings path, throwaway root.

## Tasks & Acceptance

**Execution:**
- [x] `src/OneNoteMcp.Core/SectionFileNames.cs` (+ tests) -- name rules shared by all three kinds, reserved OneNote names, Q2's `.one` rule.
- [x] `src/OneNoteMcp.Core/IOneNoteGateway.cs`, `src/OneNoteMcp.Com/ComGateway.cs`, `tests/OneNoteMcp.Tests/FakeGateway.cs` (+ `ComGatewayLifecycleTests`) -- the two gateway methods, guarded before COM, never retried.
- [x] `src/OneNoteMcp.Xml/` new parent-children reader (+ fixture tests).
- [x] `src/OneNoteMcp.Core/Access/WriteAccess.cs` -- container-parent check.
- [x] `src/OneNoteMcp.Core/UseCases/CreateHierarchyUseCase.cs` (new) -- section, group and notebook `Create` operations; notebook folder rules and auto-grant.
- [x] `src/OneNoteMcp.Core/Results/ToolWarnings.cs`, `ToolMeta.cs` -- warnings and meta.
- [x] `src/OneNoteMcp.Server/Tools/` new tools per Q1, `ServerSetup.cs` registration.
- [x] `tests/OneNoteMcp.Tests/CreateHierarchyToolTests.cs` (new) -- every I/O matrix row, lock order (settings inside write), no COM on refusals and `alreadyExisted`.
- [x] `tests/OneNoteMcp.Tests/Contract/tool-contract.json`, `McpSdkBindingTests.cs` -- contract.
- [x] `tests/OneNoteMcp.Integration/CreateHierarchyIntegrationTests.cs` (new, opt-in) -- each kind on MCP Test, per Q6.
- [x] `README.md`, `plugin/skills/guide/SKILL.md` -- tool rows; the skill's "read-only" wording for structure writes updated.
- [x] `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` -- spine sync (AD-7 `Create` row: gateway methods, Q2-Q4 rules, notebook folder rules), unprobed group existing name.

**Acceptance Criteria:**
- Given a granted notebook, when `create_section` names it or one of its live groups as the parent, then a section with that name exists under it and its ID is returned.
- Given an ungranted notebook and a client without elicitation, when any section or group create targets it, then `WriteNotGranted` returns and no lock is taken and no COM write runs.
- Given an absent folder under an allowed location, when `create_notebook` runs, then the notebook opens in OneNote and `list_grants` shows a `writeNotebook` grant with `grantedVia: created` and status `active`.
- Given the settings mutex is held by another process past its timeout, when a notebook create succeeds, then the result is `ok: true` with `createdNotGranted`, and nothing else is written.

## Implementation Notes

Build of 2026-11-13 (unattended; nothing touched OneNote). Choices the plan left open, and the build directions' assumptions, are recorded here and as spine-sync items in `deferred-work.md` (Story 5.3).

- Step 1 for a notebook runs before the pipeline, outside its deadline: the name, `settings.json` writable, the proposed folder (a URL is `NotSupported`; a UNC, device or relative path `InvalidArgument`; one that doesn't exist yet is still offered, since the user may change it), then the location dialog. The chosen folder must exist; the notebook's folder is `<chosen>\<name>`, made canonical with `FilePathGate.Canonical`. The pipeline's own step 1 re-checks settings; steps 3-4 read the open notebooks and the folder under the lock.
- Folder rules (`NotebookFolders`): an open notebook's own folder, a non-empty folder (hidden entries count; one that can't be listed counts as not empty) or a file there is `FileExists`; inside or around an open notebook's folder, or inside OneNoteMcp's folders (the settings folder, the journal folder, the default journal folder, the roaming and local `OneNoteMcp` folders; each also canonical) is `InvalidArgument`.
- Auto-grant: only when the read-back key equals the target's key and the ID wasn't open before; `meta.path` is reported only in that case (a path OneNote put elsewhere isn't echoed). A failed read-back or save gives `createdNotGranted` saying which. Step-7 ambiguous failures (and the pipeline's own deadline or cancel, through `FailureNote`) add "If a notebook was created, it has no write grant yet."
- A declined or dismissed location dialog is `Cancelled`, `retryable: false`, no `outcomeUnknown`; no dialog (no server, or no elicitation) is `NotSupported` (Phill's decision, 2026-11-13).
- When a section and a group of the same name both already exist under a parent, the same kind wins (`alreadyExisted`); otherwise the other kind's name is `InvalidArgument` (Q4).
- The device-name check now also ignores spaces before an extension (`CON .x`), for `Problem` too; that only refuses more.
- `create_section` and `create_section_group` are marked idempotent (a repeat returns the same ID); `create_notebook` isn't. `create_notebook` raises no write-grant dialog.
- The cloud seam is `CloudNotebookCreate` (`Enabled => false`, `SEAM(5.3 cloud probe)`); the probe is `CreateHierarchyIntegrationTests.CloudNotebookProbe_RecordsWhatOpenHierarchyDoesAtAOneDriveUrl`, not run.

## Plan Change Log

- 2026-11-13 — Security review fixes (Phill: "fix all three"). Three rules added to the plan's behaviour; status stays `built`.
  - **Closed-notebook containment.** Besides the open-notebook checks, `create_notebook` refuses with `InvalidArgument` (the code the open-notebook containment refusal uses) a proposed folder, before the dialog, and the folder the user accepted, after it, when that folder or any folder above it holds a `*.onetoc2` file (`NotebookFolders.InsideANotebookFolder`; hidden files count, case ignored; a folder that doesn't exist yet, or can't be listed, is passed over). OneNote's default-folder prefill isn't checked: the user may change it, and the accepted folder is.
  - **Name hygiene, all three kinds.** `SectionFileNames.NameProblem` (the one rule section, section group and notebook share, in the use case and the gateway) also refuses control characters (Unicode Cc, including U+007F-U+009F), U+2028/U+2029, the bidi controls U+200E, U+200F, U+202A-U+202E and U+2066-U+2069, the invisible U+200B-U+200D, U+2060 and U+FEFF, and a name over 128 UTF-16 code units (`MaxNameLength`; not text elements, since nothing in the code counts those, so an emoji counts as two). The character rule sits in the shared `Characters` check, so `Problem` (a section's file name at the gateway) gets it too; the length rule is in `NameProblem` only, so `.one` added to a 128-character section name is still accepted. Messages are phrases that never quote the name; `NameUserAction` and the three tool descriptions name the new rules.
  - **Grant only an empty new notebook.** The post-call read-back is now one `GetHierarchy` at `Children` scope (was `Self`); the automatic grant is written only when, on top of the existing key and not-already-open checks, the notebook holds no section or section group outside its recycle bin (`HierarchyChildren` skips the bin and what is in it). Otherwise the create stands with `createdNotGranted` ("It already holds sections or section groups, so it may be an existing notebook OneNote opened rather than a new one.") and `meta.path`.
  - Docs: `README.md`, `plugin/skills/guide/SKILL.md` (`createdNotGranted` row) and the `create_*` tool descriptions updated to match.

## Review Triage Log

### 2026-11-13 — Security review (after the build)

A security review of the 5.3 diff raised three findings; Phill decided on 2026-11-13 to fix all three. Each was fixed test-first (new tests seen failing, then passing).

- verdicts: 3 findings — all accepted and patched
- findings:
  - `[medium]` `[patch]` The "not inside another notebook" check knew only open notebooks, so a notebook could be created inside a closed one's folder — fixed: `NotebookFolders.InsideANotebookFolder` (any `*.onetoc2` in the folder or an ancestor), checked on the proposed folder before the dialog and on the accepted folder after it (`CreateHierarchyUseCase.CheckNotInANotebook`), `InvalidArgument`. Tests: `Notebook_AProposedFolderInAClosedNotebook_*`, `Notebook_AChosenFolderInAClosedNotebook_*`, `Notebook_AFolderNextToAClosedNotebook_IsCreated`, `InsideANotebookFolder_*`.
  - `[medium]` `[patch]` Names could carry control, bidi and invisible characters, and had no length cap, so a name in the location dialog or a listing could read as something it isn't — fixed in the shared `SectionFileNames.NameProblem` path (all three kinds, before any dialog or COM call), `InvalidArgument`, message never quotes the name. Tests: `NameProblem_RefusesControlBidiAndInvisibleCharacters_*`, `NameProblem_AllowsUpTo128Utf16Characters`, new rows in `BadName_*` and `Notebook_BadName_*`, `AnyKind_ANameOver128Characters_*`, `AnyKind_ANameOf128Characters_IsAccepted`.
  - `[low]` `[patch]` Hardening: the automatic grant could land on an existing notebook OneNote opened at the checked folder — fixed: the read-back is at `Children` scope and the grant is written only for a notebook with no sections or section groups (recycle bin ignored); otherwise `createdNotGranted`. Tests: `Notebook_ReadBackHoldingSectionsOrGroups_IsOk_CreatedNotGranted`, `Notebook_ReadBackHoldingOnlyTheRecycleBin_IsGranted`, `Notebook_ReadBack_IsOneChildrenScopeRead_OfTheNewNotebook`; the settings-mutex lock-order test now hooks the `Children` read.
- verification: `dotnet build OneNoteMcp.slnx -warnaserror` 0 warnings, 0 errors; `dotnet test tests/OneNoteMcp.Tests` 2515 passed, 0 failed (2469 before, plus 46 new cases). Nothing touched OneNote.

### 2026-11-13 — Review pass

The Quick lens was launched as a subagent, but on the coordinator's instruction (finish in this turn, do not wait on a running subagent) the orchestrator ran the Quick lens inline against the diff, the plan's acceptance criteria and `CLAUDE.md`; the subagent's later output, if any, is not part of this triage.

- verdicts: 5 findings — high 0, medium 0, low 2, false 3, maybe-false 0
- findings:
  - `[false]` `[reject]` A step-7 ambiguous failure could carry the "no write grant yet" note twice (once from `CreateNotebook.CallAsync`, once from the pipeline's `FailureNote`) — `WritePipeline.cs:195-205`: an ambiguous `OneNoteException` is rethrown with its own message only; `FailureNote` is added only on the cancel and deadline paths, which `CallAsync` does not catch, so the note appears once.
  - `[false]` `[reject]` A notebook folder could contain one of OneNoteMcp's own folders, since only "same or inside" is checked — the target must be absent or empty (`NotebookFolders.State`, step 4), so it can't contain anything.
  - `[false]` `[reject]` The auto-grant could land on a different notebook when OneNote resolves the folder through a link — `CreateHierarchyUseCase.cs:419-432`: the target is canonical (links followed) before the call, and the grant is written only when the read-back key equals the target's key and the ID wasn't open before; otherwise `createdNotGranted`.
  - `[low]` `[reject]` With a client that lacks elicitation, `create_notebook` still reads OneNote's default notebook folder (one COM read) before the dialog reports itself unsupported — `CreateHierarchyUseCase.cs:134-135`; harmless (a read, no write, result `NotSupported` as decided), and avoiding it needs a new capability query on the prompt port.
  - `[low]` `[reject]` The location dialog accepts the model-proposed folder with one click (no typed confirmation as the grant dialog has) — Phill's 2026-11-13 decision is an editable, prefilled field; the dialog states the grant and how to revoke it, and every folder rule runs on the answer. Not a defect against the plan.

## Design Notes

OneMore (checked 2026-11-12) creates sections and top-level groups with `UpdateHierarchy` (a new nameless-ID element, then a re-read to find the new ID) and compares names with `CurrentCultureIgnoreCase` across sections and groups together; it never creates a notebook (`OpenHierarchy` `cftNotebook` only opens an existing folder). We keep AD-7's `OpenHierarchy` create, which spike 5.1 measured, and take OneMore's shared name space as Q4's recommendation. OneMore's comment that omitting a sibling from `UpdateHierarchy` deletes it contradicts spike 5.1 constraint 4 ("children left out are kept"); that matters to entry 4, not here, and is noted for it.

The auto-grant's key check (the read-back key equals the checked folder's key, and the ID wasn't already open) is a tightening of AD-6's "keyed by the path read back": it stops a grant landing on a notebook OneNote resolved elsewhere (a link, an existing notebook) than the folder the server checked empty.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, contract snapshot matches.

**Manual checks (if no CLI):**
- Phill's live sitting: `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter CreateHierarchy` on MCP Test, then close and delete the throwaway notebook it names. A security review of the diff runs before merge.

## Auto Run Result

Status: built (2026-11-13, unattended `bmad-build-auto`, full route).

**Summary.** Three new MCP tools create OneNote structure through the AD-7 write pipeline as `Create`: `create_section` and `create_section_group` (by parent notebook or section group ID plus a relative name; existing same-kind name returns `alreadyExisted` with no COM call; the other kind's same name is `InvalidArgument`), and `create_notebook` (the user picks a local folder in an elicitation dialog; the folder is checked absent or empty and outside open notebooks and OneNoteMcp's folders under the lock; the notebook's write grant `grantedVia: created` is saved inside the write lock, else `createdNotGranted`). Creating a notebook at a OneDrive URL is behind the closed `CloudNotebookCreate` seam (`NotSupported`); its live probe is written as an opt-in integration test and was not run.

**Files changed (main ones).**
- `src/OneNoteMcp.Core/UseCases/CreateHierarchyUseCase.cs` — the three `Create` operations, notebook folder rules, auto-grant.
- `src/OneNoteMcp.Core/SectionFileNames.cs` — one name rule for all kinds, OneNote's reserved names, Q2's `.one` rule.
- `src/OneNoteMcp.Core/Writes/NotebookFolders.cs` — containment, OneNoteMcp's own folders, folder state.
- `src/OneNoteMcp.Core/Access/NotebookLocationPrompt.cs`, `src/OneNoteMcp.Server/Grants/ElicitationNotebookLocationPrompt.cs` — the location dialog port and its MCP elicitation adapter.
- `src/OneNoteMcp.Core/CloudNotebookCreate.cs` — the `SEAM(5.3 cloud probe)`, closed.
- `src/OneNoteMcp.Core/Access/WriteAccess.cs` — `CheckContainerAsync` for a parent that may be the notebook.
- `src/OneNoteMcp.Xml/HierarchyChildren.cs` — a parent and its live children.
- `src/OneNoteMcp.Core/IOneNoteGateway.cs`, `src/OneNoteMcp.Com/ComGateway.cs` — `CreateSectionGroupAsync`, `CreateNotebookAsync`, guarded before COM, never retried.
- `src/OneNoteMcp.Server/Tools/Create{Section,SectionGroup,Notebook}Tool.cs`, `ServerSetup.cs` — tools and registration; `ToolMeta.ObjectId`, `ToolWarnings.AlreadyExisted`/`CreatedNotGranted`.
- Tests: `CreateHierarchyToolTests.cs` (every I/O matrix row, lock order, mutex-held `createdNotGranted`), `HierarchyCreateRulesTests.cs`, `ComGatewayLifecycleTests.cs`, contract snapshot, test gateways; opt-in `tests/OneNoteMcp.Integration/CreateHierarchyIntegrationTests.cs` (section and group on MCP Test, throwaway notebook, cloud probe).
- Docs: `README.md`, `plugin/skills/guide/SKILL.md` tool rows; spine-sync items in `deferred-work.md`.

**Review.** One pass (Quick, run inline): 5 findings; 0 patched, 0 deferred, 5 rejected (3 false, 2 low; reasons in the triage log). Separately deferred (frontmatter `deferred`): the pre-existing story-2.7 test `AWriteStillQueuedAtItsOwnDeadline_NeverRan_AndIsNotTracked` is timing-flaky, failing 2 of 8 full runs during verification.

**Follow-up review recommended:** false (no patched entries).

**Verification.** `dotnet build OneNoteMcp.slnx -warnaserror`: 0 warnings, 0 errors. `dotnet test tests/OneNoteMcp.Tests`: 2469 passed, 0 failed, 0 skipped (contract snapshot matches). Privacy grep on the diff: 0 hits. No integration test ran; OneNote was not touched.

**Residual risks.** Nothing ran against a real OneNote: whether `OpenHierarchy` `cftNotebook` creates a notebook at an absent local folder, what `path` it reads back, and how a group's existing name behaves are for the live sitting. The OneDrive path is unproven and closed. Two unit tests hold the real `Local\OneNoteMcp.Settings` mutex for up to about 0.3 s. A security review of the diff is due before merge (ticket Verify).
