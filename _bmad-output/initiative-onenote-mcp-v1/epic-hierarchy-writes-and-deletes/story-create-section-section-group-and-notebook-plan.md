---
title: 'Create: section, section group and notebook'
type: 'feature'
ticket: '3'
created: '2026-11-12'
status: 'ready-for-dev'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
warnings: ['oversized']
deferred: []
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
- [ ] `src/OneNoteMcp.Core/SectionFileNames.cs` (+ tests) -- name rules shared by all three kinds, reserved OneNote names, Q2's `.one` rule.
- [ ] `src/OneNoteMcp.Core/IOneNoteGateway.cs`, `src/OneNoteMcp.Com/ComGateway.cs`, `tests/OneNoteMcp.Tests/FakeGateway.cs` (+ `ComGatewayLifecycleTests`) -- the two gateway methods, guarded before COM, never retried.
- [ ] `src/OneNoteMcp.Xml/` new parent-children reader (+ fixture tests).
- [ ] `src/OneNoteMcp.Core/Access/WriteAccess.cs` -- container-parent check.
- [ ] `src/OneNoteMcp.Core/UseCases/CreateHierarchyUseCase.cs` (new) -- section, group and notebook `Create` operations; notebook folder rules and auto-grant.
- [ ] `src/OneNoteMcp.Core/Results/ToolWarnings.cs`, `ToolMeta.cs` -- warnings and meta.
- [ ] `src/OneNoteMcp.Server/Tools/` new tools per Q1, `ServerSetup.cs` registration.
- [ ] `tests/OneNoteMcp.Tests/CreateHierarchyToolTests.cs` (new) -- every I/O matrix row, lock order (settings inside write), no COM on refusals and `alreadyExisted`.
- [ ] `tests/OneNoteMcp.Tests/Contract/tool-contract.json`, `McpSdkBindingTests.cs` -- contract.
- [ ] `tests/OneNoteMcp.Integration/CreateHierarchyIntegrationTests.cs` (new, opt-in) -- each kind on MCP Test, per Q6.
- [ ] `README.md`, `plugin/skills/guide/SKILL.md` -- tool rows; the skill's "read-only" wording for structure writes updated.
- [ ] `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md` -- spine sync (AD-7 `Create` row: gateway methods, Q2-Q4 rules, notebook folder rules), unprobed group existing name.

**Acceptance Criteria:**
- Given a granted notebook, when `create_section` names it or one of its live groups as the parent, then a section with that name exists under it and its ID is returned.
- Given an ungranted notebook and a client without elicitation, when any section or group create targets it, then `WriteNotGranted` returns and no lock is taken and no COM write runs.
- Given an absent folder under an allowed location, when `create_notebook` runs, then the notebook opens in OneNote and `list_grants` shows a `writeNotebook` grant with `grantedVia: created` and status `active`.
- Given the settings mutex is held by another process past its timeout, when a notebook create succeeds, then the result is `ok: true` with `createdNotGranted`, and nothing else is written.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

OneMore (checked 2026-11-12) creates sections and top-level groups with `UpdateHierarchy` (a new nameless-ID element, then a re-read to find the new ID) and compares names with `CurrentCultureIgnoreCase` across sections and groups together; it never creates a notebook (`OpenHierarchy` `cftNotebook` only opens an existing folder). We keep AD-7's `OpenHierarchy` create, which spike 5.1 measured, and take OneMore's shared name space as Q4's recommendation. OneMore's comment that omitting a sibling from `UpdateHierarchy` deletes it contradicts spike 5.1 constraint 4 ("children left out are kept"); that matters to entry 4, not here, and is noted for it.

The auto-grant's key check (the read-back key equals the checked folder's key, and the ID wasn't already open) is a tightening of AD-6's "keyed by the path read back": it stops a grant landing on a notebook OneNote resolved elsewhere (a link, an existing notebook) than the folder the server checked empty.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, contract snapshot matches.

**Manual checks (if no CLI):**
- Phill's live sitting: `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter CreateHierarchy` on MCP Test, then close and delete the throwaway notebook it names. A security review of the diff runs before merge.
