---
title: 'Scaffold, CI and COM tracer bullet'
type: 'feature'
ticket: '2'
created: '2026-11-03'
status: done
baseline_revision: '89edc756faf98ce27c4e6654b44e1cb5711a349d'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** No code exists. The riskiest assumption in the spine (AD-1: a hand-declared `[ComImport] IApplication` matches OneNote's real vtable) is unproven, and the solution layout, CI and stdio host don't exist yet.

**Approach:** Scaffold the AD structural seed (four src projects, two test projects, CI). Hand-declare `IApplication` from the registered OneNote 15 type library. Prove it with an opt-in smoke test across the vtable. Ship one tracer tool, `get_hierarchy` at notebooks scope, through Core's `IOneNoteGateway` and a minimal STA `ComGateway` that refuses to start OneNote.

## Boundaries & Constraints

**Always:**
- Dependencies follow the spine diagram only. Core never references Com or Server, and no type outside Com references COM types (AD-2).
- Every COM call runs on the one STA thread.
- stdout carries MCP only; logs go to stderr (AD-13).
- Check that `ONENOTE.EXE` is running before creating the COM object; never activate OneNote silently.
- Real notebooks get read-only calls only.

**Never:**
- The GAC PIA. If the hand-declared vtable fails, stop and record a Decision; don't switch silently.
- `NavigateTo`, `NavigateToUrl`, `QuickFiling` or any write in tests.
- Killing or closing OneNote.
- Belongs to later stories: the AD-9 envelope and HRESULT map (3), settings and file logs (4), the AD-3 lifecycle with timeouts and reconnect (5), other scopes and tools (7).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Happy path | OneNote running; `get_hierarchy` with no args | 2013-schema notebooks XML, unchanged | No error expected |
| Not running | No `ONENOTE.EXE` process | Tool result `isError: true`, text starts `OneNoteNotRunning`; no OneNote process starts | `OneNoteException(OneNoteNotRunning)` caught in the tool |
| COM failure | `GetHierarchy` raises a `COMException` | `isError: true`, `Internal` with the HRESULT in hex | Gateway wraps it in `OneNoteException(Internal)`; no raw `COMException` leaves Com |
| Bad start ID | `startNodeId` unknown | `isError: true` with the HRESULT | Same as COM failure (named mapping comes in story 3) |

</frozen-after-approval>

## Code Map

- `spikes/host-csharp/Program.cs` -- spike host: reuse its hosting and stdio wiring and the stderr logging line; replace its `BlockingCollection` STA queue. It uses the GAC PIA; don't copy that reference.
- `spikes/host-csharp/HostCsharp.csproj`, `nuget.config` -- package versions and the nuget.org-only source to copy.
- Registered type library `{0EA692EE-BB50-4E3C-AEF0-356D91732725}` 1.1, at `ONENOTE.EXE\3`, is the vtable source:
  - IApplication is a dual interface, IID `452AC71A-B655-4967-A208-A4CC39DD7949`; coclass Application has CLSID `D7FAC39E-7FF1-49AA-98CF-A1DDD316337E`.
  - Slots 7–35, in this order: GetHierarchy, UpdateHierarchy, OpenHierarchy, DeleteHierarchy, CreateNewPage, CloseNotebook, GetHierarchyParent, GetPageContent, UpdatePageContent, GetBinaryPageContent, DeletePageContent, NavigateTo, NavigateToUrl, Publish, OpenPackage, GetHyperlinkToObject, FindPages, FindMeta, GetSpecialLocation, MergeFiles, QuickFiling, SyncHierarchy, SetFilingLocation, get_Windows, get_Dummy1, MergeSections, get_COMAddIns, get_LanguageSettings, GetWebHyperlinkToObject.
  - Every parameter is declared in full; optional ones are still vtable arguments. Types: BSTR, VT_BOOL, VT_DATE, and the enums.
  - The dump script is in the session scratchpad; the order is recorded here.
- Enum values: HierarchyScope self=0 children=1 notebooks=2 sections=3 pages=4; XMLSchema 2013=2; PageInfo basic=0; SpecialLocation backup=0 unfiled=1 defaultNotebookFolder=2; plus CreateFileType, NewPageStyle, PublishFormat, FilingLocation, FilingLocationType.
- Package versions: ModelContextProtocol 2.3.1, Microsoft.Extensions.Hosting 10.0.14, xunit.v3 4.0.2, xunit.runner.visualstudio 4.0.1, Microsoft.NET.Test.Sdk 18.11.0. The installed SDK is 10.0.103.
- The GitHub repo `pmcormier/onenote-mcp` already exists (private); work is on branch `story/1.2-scaffold`.

## Tasks & Acceptance

**Execution:**
- [ ] `global.json`, `nuget.config`, `Directory.Build.props`, `Directory.Packages.props`, `.gitignore`, `OneNoteMcp.slnx` -- SDK pin (10.0.100, roll forward to the latest feature band), nuget.org only, nullable, warnings as errors, central package versions, ignore bin/obj -- reproducible builds.
- [ ] `src/OneNoteMcp.Core/` -- `ErrorCode` (the full closed AD-9 list), `OneNoteException(code, message, hresult?)`, Core's own `HierarchyScope` enum, and `IOneNoteGateway.GetHierarchyAsync(startNodeId, scope, ct)` -- the port.
- [ ] `src/OneNoteMcp.Xml/OneNoteXml.cs` -- the 2013 namespace constant only -- seed for story 6.
- [ ] `src/OneNoteMcp.Com/Interop/IApplication.cs` (+ enums) -- `[ComImport, Guid, InterfaceType(InterfaceIsDual)]`, all 29 members in slot order with `PreserveSig=false`, `[MarshalAs(BStr)]` strings and `VariantBool` bools -- AD-1.
- [ ] `src/OneNoteMcp.Com/StaThread.cs`, `ComGateway.cs`, `IProcessProbe.cs` -- one STA thread draining a queue (CLR waits on an STA pump COM messages), lazily creating `Application` by CLSID only after the probe finds `ONENOTE`, mapping scope, wrapping `COMException` as `Internal` -- the minimal AD-2 actor.
- [ ] `src/OneNoteMcp.Server/` -- Program (stdio host, stderr logging, DI for the gateway) and `Tools/GetHierarchyTool.cs` (notebooks scope, returns the XML or an `isError` result with `code: message`; never throws) -- the tracer tool.
- [ ] `tests/OneNoteMcp.Tests/` -- a fake gateway; tool tests for the matrix rows; `ComGateway` with a fake probe returning not-running never creates COM; a reflection test that `IApplication`'s declared method order equals the slot list above -- CI guard.
- [ ] `tests/OneNoteMcp.Integration/VtableSmokeTests.cs` -- skipped unless `ONENOTE_MCP_INTEGRATION=1`. Read-only calls: GetHierarchy (notebooks, then pages under the first notebook), GetHierarchyParent, GetPageContent (basic) on one page, GetHyperlinkToObject, GetSpecialLocation, get_Windows count, and GetWebHyperlinkToObject (the last slot). Each asserts result shape -- proves the vtable.
- [ ] `.github/workflows/ci.yml` -- windows-latest; setup-dotnet 10; restore, build, test `OneNoteMcp.Tests` -- CI.

**Acceptance Criteria:**
- Given the repo, when `dotnet build` and `dotnet test tests/OneNoteMcp.Tests` run, then both pass with zero warnings.
- Given OneNote running, when the integration suite runs with the flag, then every smoke call succeeds and returns well-formed results.
- Given the server exe under a stdio client, when it calls `tools/list` and then `get_hierarchy`, then it gets one tool and notebooks XML, with nothing but JSON-RPC on stdout.
- Given the branch pushed, when CI runs, then it is green.

## Design Notes

- `InterfaceIsDual` keeps the IDispatch slots 0–6 implicit, so the first declared member is slot 7. Members whose types aren't needed yet (`QuickFiling`, `Windows`, `COMAddIns`, `LanguageSettings`) return `[MarshalAs(IDispatch)] object`, which keeps the slot without declaring those interfaces.
- `DateTime` parameters marshal as VT_DATE by default in built-in COM interop; callers must pass UTC (Conventions).
- Why not the PIA: its `IApplication` is a managed wrapper with overloads and a generated IID, can't be embedded (CS1756), and differs from the real vtable layout.
- Interim error text (`OneNoteNotRunning: OneNote is not running…`) is replaced by the AD-9 envelope in story 3.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: success.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass.
- `$env:ONENOTE_MCP_INTEGRATION=1; dotnet test tests/OneNoteMcp.Integration` -- expected: all pass against real OneNote.
- A scripted stdio JSON-RPC exchange against `OneNoteMcp.Server.exe` -- expected: notebooks XML.
- `gh run watch` on the pushed branch -- expected: green.

**Manual checks (if no CLI):**
- Phill: `claude mcp add` the local exe, then in a new session `get_hierarchy` lists his notebooks.
- Phill: with OneNote closed by him, `get_hierarchy` returns `OneNoteNotRunning` and Task Manager shows no `ONENOTE.EXE`.

## Implementation Notes

- Vtable proven: the hand-declared `IApplication` passes read-only calls at slots 7, 13, 14, 22, 25, 30 and 35 against the running OneNote (cloud notebooks), so the PIA fallback isn't needed. No Decision needed.
- `ComGateway` checks for `ONENOTE.EXE` before every call, not only before activation. It drops a stale reference when OneNote is gone, so a OneNote closed after startup reports `OneNoteNotRunning`, not `Internal`. This is not the AD-3 reconnect logic, which stays in story 5.
- Interop types are `internal`, with `InternalsVisibleTo` only for the two test projects, so the compiler enforces AD-2.
- xunit.v3 4.x defaults to Microsoft.Testing.Platform, which the .NET 10 SDK rejects for `dotnet test <dir>`. Both test projects set `IsTestingPlatformApplication=false` and run through VSTest.
- `spikes/Directory.Build.props` and `spikes/Directory.Packages.props` keep the frozen spike out of the root build settings.
- The installed type library has `XMLSchema.xs2026 = 3`, and `xsCurrent` is now 3. We always pass `xs2013` explicitly. Story 6 (vendored XSD) should know the 2026 schema exists.
- Edge-case audit: the call-path `COMException` wrap had no suite test, so `tests/OneNoteMcp.Integration/ComGatewayIntegrationTests.cs` was added. It covers the real gateway's happy path and an unknown start ID giving `Internal` with the HRESULT.
- CI actions bumped to `checkout@v5` and `setup-dotnet@v5`, because GitHub is retiring the Node 20 runner that v4 uses.
- Verified: build with 0 warnings; 17/17 unit tests; 12/12 integration tests with the flag; a stdio JSON-RPC exchange returns 4 notebooks, with only JSON-RPC on stdout; CI run 41762125551 green on `dd1a52a`.
- After review patches: 20/20 unit, 12/12 integration, 0 warnings; CI green on `1ac13bc`.
- Manual checks (2026-11-03, Phill present): (1) registered with `claude mcp add`, and in a new session `get_hierarchy` returned all 4 notebooks; it also worked against a resident OneNote with no window. (2) With no `ONENOTE.EXE` at 22:51:02, `get_hierarchy` returned `OneNoteNotRunning`, and there was still no process 9 s later. Phill marked the story done.

## Plan Change Log

## Review Triage Log

### Pass 1 (quick lens): medium 4, low 2, false 0, maybe-false 1

| # | Finding | Verdict | Route | Evidence / action |
|---|---------|---------|-------|-------------------|
| 1 | CI hasn't run the reviewed state: the v5 action bump and the new integration test are uncommitted | medium | patch | True; `origin` was at `dd1a52a`. Committed and pushed, and CI re-run on the new head. |
| 2 | A OneNote restart leaves `_app` stale, and every later call fails | medium | patch | `EnsureApplication` returns the cached RCW whenever the probe says running. Now released on 0x800706BA / 0x80010108; test `ServerUnavailable_DropsReference_SoNextCallRecreatesApplication`. |
| 3 | Exceptions other than `COMException` escape Com | medium | patch | Interop maps E_INVALIDARG to `ArgumentException`, etc. Both paths now wrap any non-`OneNoteException` with its HRESULT; 2 tests added. |
| 4 | The probe counts other sessions' OneNote, so it could activate silently | medium | patch | `GetProcessesByName` enumerates every session. Now filtered to the current `SessionId`. |
| 5 | Work queued during `Dispose` never completes | low | defer | Real, but only at host shutdown. Story 5 owns the lifecycle and timeouts. |
| 6 | `ComGateway.Dispose` can throw `AggregateException` | maybe-false | reject | `ReleaseApplication` guards with `IsComObject`, and `FinalReleaseComObject` on a separated RCW isn't shown to throw. It would only be low. |
| 7 | The slot-35 test skips on a machine with only local notebooks; `Target()` prefers a cloud notebook | low | reject | It ran and passed here (cloud notebooks), so the vtable is proven. Preferring cloud exists to make that slot run. The fix would add complexity. |
