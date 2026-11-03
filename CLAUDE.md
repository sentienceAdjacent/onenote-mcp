# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

A local MCP server that lets an MCP client (Claude Code, Claude Desktop) read and manage OneNote desktop notebooks on this Windows machine. It drives the installed OneNote desktop app through its COM automation API (`OneNote.Application`).

**Status:** greenfield. No code, language, build system or git repo yet. The language, MCP SDK and tool surface will be decided in BMAD architecture. Don't assume a stack before then. Replace this note with real build/test/run commands once they exist.

## Workflow: BMAD

This project uses BMAD (core-tools + method 6.13.0-next, set up in `_bmad/`). After `npx skills update`, run `bmad setup` again.

- Planning: `bmad-architecture` + `bmad-spec` (add `bmad-prd` if needed). Planning docs go through the Fable final review before they're marked final (see the global BMAD rule, and the `_bmad/custom/` overrides).
- Board: `bmad-ticket` owns epics, stories and the ready queue.
- Building: one story per session with `bmad-build`, passing the ticket reference. Builds stop at `built`. Only Phill marks a ticket done.
- Use superpowers only for debugging, TDD and verification.

## OneNote COM constraints the design has to respect

- **Which OneNote:** COM exists only in OneNote desktop (OneNote 2016 / Microsoft 365, `ONENOTE.EXE`). The retired "OneNote for Windows 10" UWP app has no COM API. OneNote must be installed, and it starts on demand when the COM object is created.
- **API shape:** the interface is `IApplication`. You read with `GetHierarchy` and `GetPageContent`, which return XML in the OneNote 2013 schema (`http://schemas.microsoft.com/office/onenote/2013/onenote`). You write with `UpdateHierarchy`, `UpdatePageContent`, `CreateNewPage` and `DeleteHierarchy`. `FindPages` is the search call. Objects are addressed by OneNote object IDs, not by path, so name→ID resolution sits on top of `GetHierarchy`.
- **Write concurrency:** `UpdatePageContent` and `DeleteHierarchy` accept a last-modified date and fail if the page changed since then. Use this check. Don't blind-overwrite.
- **Threading:** COM calls must run on an STA thread. MCP servers are usually async or multi-threaded, so route every COM call through one dedicated STA thread.
- **Process context:** the server and OneNote must run at the same integrity level. If one is elevated and the other isn't, activation fails. A missing or bad registration shows up as `REGDB_E_CLASSNOTREG` / `0x80040154`. OneNote's HRESULTs (`0x8004200x`) should be mapped to readable MCP errors.
- **Search:** `FindPages` depends on Windows Search indexing, so its results can be incomplete when the index is stale.

## Testing note

Anything that touches COM needs a real OneNote install and a test notebook, so it can't run in plain CI. Keep XML parsing and generation separate from COM calls so that logic can be unit-tested against captured XML fixtures.
