# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

A local MCP server that lets an MCP client (Claude Code, Claude Desktop) read and manage OneNote desktop notebooks on this Windows machine. It drives the installed OneNote desktop app through its COM automation API (`OneNote.Application`).

**Status:** epic 1 (read-only release) is done, released up to `v0.8.0-rc.2`. Epic 2 (safe page writes) is under way: the opening refactor and the page-write probes spike are done, and its spine amendments (2.3) follow them; epic 5 (hierarchy writes and deletes) runs its probe spike alongside. Ask `bmad-ticket` for the current board. The binding design is the spine at `_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md` (AD-1…AD-21), with its rationale in the `.memlog.md` beside it. The ticket tree is under `_bmad-output/initiative-onenote-mcp-v1/`. Spike evidence is in `spikes/` and `epic-read-only-release/spike-*-findings.md`.

## Build, test, run

- Build: `dotnet build OneNoteMcp.slnx -warnaserror`. Warnings are errors, and the SDK is pinned by `global.json`.
- Unit tests (CI): `dotnet test tests/OneNoteMcp.Tests`
- Integration tests (opt-in, real OneNote must already be running, read-only): `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration`
- Run the server: `src/OneNoteMcp.Server/bin/Debug/net10.0-windows/OneNoteMcp.exe` speaks MCP over stdio. To try a build in Claude Code, register it under a name of its own (`claude mcp add --scope user onenote-dev -- "<path>\OneNoteMcp.exe"`) or pass it with `--mcp-config` for one session. Don't rebuild while a Claude session has it running: the exe is locked.
- Pack the plugin: `pwsh -File scripts/pack-plugin.ps1` publishes the self-contained exe and writes `dist/onenote-<ver>-win-x64.zip` (plugin at the zip root, version from `Directory.Build.props`). Validate with `claude plugin validate --strict dist/stage/onenote`. The marketplace catalog lives in its own repo, `sentienceAdjacent/plugins` (local clone `D:\Development\sentienceAdjacent-plugins`); validate it there with `claude plugin validate --strict .`. Local loop: `claude --plugin-dir dist/onenote-<ver>-win-x64.zip` loads the zip for one session.
- Layout: `src/OneNoteMcp.{Core,Xml,Com,Server}` and `tests/OneNoteMcp.{Tests,Integration}`. COM interop types are `internal` to Com (AD-2).

## Workflow: BMAD

This project uses BMAD (core-tools + method 6.13.0-next, set up in `_bmad/`). After `npx skills update`, run `bmad setup` again.

- Planning: `bmad-architecture` + `bmad-spec` (add `bmad-prd` if needed). Planning docs go through the Fable final review before they're marked final (see the global BMAD rule, and the `_bmad/custom/` overrides).
- Board: `bmad-ticket` owns epics, stories and the ready queue.
- Building: one story per session with `bmad-build`, passing the ticket reference. Builds stop at `built`. Only Phill marks a ticket done.
- Use superpowers only for debugging, TDD and verification.

## OneNote COM constraints (checked by spike 1, 2026-11-03; details in the spike findings doc)

- **Which OneNote:** COM exists only in OneNote desktop (M365, `ONENOTE.EXE`). Creating the COM object when OneNote isn't running starts a windowless `-Embedding` instance. Don't do that silently: report "OneNote is not running", or make it a setting.
- **Binding:** IDispatch late binding fails, because the type library is registered for Win32 only. pwsh 7 gets `E_FAIL` and pywin32 gets `TYPE_E_LIBNOTREGISTERED`. Use early binding: a hand-declared `[ComImport]` `IApplication` in C#.
- **API shape:** read with `GetHierarchy` and `GetPageContent`, which return XML in the 2013 schema. Write with `UpdateHierarchy`, `UpdatePageContent`, `CreateNewPage` and `DeleteHierarchy`. Objects are addressed by ID. A child's ID doesn't contain its parent's, and a soft-deleted page gets a new ID.
- **Write concurrency:** `dateExpectedLastModified` only protects pages that are open in OneNote. For a page that isn't open, `lastModifiedTime` doesn't advance after COM edits, so a stale write goes through. Pass UTC dates and add a content read-compare-write. Even then, the check is best-effort.
- **Threading:** one STA thread owns every COM call and serialises tool calls, so every call needs a timeout. Reconnect once on `RPC_S_SERVER_UNAVAILABLE` or `RPC_E_DISCONNECTED`. Release the COM reference when idle. Never kill or close OneNote.
- **Calls the user sees:** `NavigateTo` takes keyboard focus. Call it only when the user explicitly asks.
- **Process context:** run at OneNote's integrity level, and refuse to run on a mismatch. A mismatch hangs for about 30 s and then fails with `0x80080005`, so detect elevation at startup.
- **Errors to map:**
  - `0x80042030`: a modal dialog is open in OneNote. Retryable once the user closes it.
  - `0x8004201b`: the section is locked. Check the section's `locked` attribute before writing.
  - `0x8004200f`: the binary content isn't available, as with cloud-notebook images. Degrade the read; don't fail it.
  - `0x80042010`: the last-modified date didn't match.
- **Search:** `FindPages` with indexed pages only can miss or lag new pages. Including unindexed pages returns everything unfiltered, recycle-bin pages too. Post-filter the results, and report them as possibly incomplete.

## Prior art: OneMore (check it before inventing)

[OneMore](https://github.com/stevencohn/OneMore) (MPL-2.0) is a mature OneNote add-in on the same COM API and 2013 schema, and its [developer docs](https://onemoreaddin.com/developers/Setup.htm) record measured OneNote behaviour and API defects. Our findings from it so far are in `_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/research-onemore-lessons.md`.

- When a story touches COM behaviour, page XML, IDs, search or a OneNote quirk, check OneMore's docs (especially "OneNote Defects", "OneNote ID Stability", "COM Layer", "Editing Text") and its code before designing a workaround. Do this especially when stuck.
- Learn from it and write our own code. Don't copy its source. If copying is ever unavoidable, the copied file keeps OneMore's MPL-2.0 header and copyright and gets listed in a third-party notices file.
- Credit it in the README for what we learned. When a story uses a new lesson, add it to the research note.
- We deliberately differ on concurrency: OneMore writes with `force:true` and retries writes; we keep AD-7/AD-17's basis check and never auto-retry writes.

## Testing note

Anything that touches COM needs a real OneNote install and a test notebook, so it can't run in plain CI. Keep XML parsing and generation separate from COM calls so that logic can be unit-tested against captured XML fixtures.

<!-- claude-reflect: learned rules (saved from the band; edit freely) -->
## Learned rules
- Do not insist on running bmad-walkthrough before merging; suggest it only when it seems important and leave the decision to the user.
<!-- end claude-reflect -->
