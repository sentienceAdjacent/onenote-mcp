# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

A local MCP server that lets an MCP client (Claude Code, Claude Desktop) read and manage OneNote desktop notebooks on this Windows machine. It drives the installed OneNote desktop app through its COM automation API (`OneNote.Application`).

**Status:** pre-architecture. Spike 1 is done; `spikes/` holds its probes and evidence, and `_bmad-output/initiative-onenote-mcp-v1/spike-*-findings.md` holds the results. The spike recommends C# (.NET 10 + ModelContextProtocol). `bmad-architecture` resumes next and makes the final stack call. Replace this note with real build/test/run commands once they exist.

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

## Testing note

Anything that touches COM needs a real OneNote install and a test notebook, so it can't run in plain CI. Keep XML parsing and generation separate from COM calls so that logic can be unit-tested against captured XML fixtures.
