---
id: 1
type: spike
title: "What can the OneNote desktop COM API do on this machine?"
parent: epic-read-only-release
covers: []
after: []
refined: true
hitl: true
risk: medium
---

# What can the OneNote desktop COM API do on this machine?

## Description

The architecture spine for the OneNote MCP server is paused. Its central constraint is that the server reaches OneNote through the desktop COM API (`OneNote.Application`). We found no current Microsoft statement that the M365 desktop app still supports that API. This spike finds out by running probes against the installed app: which reads, writes and searches work, how the API fails, and whether C# or Python hosts it better inside an MCP server. The architecture run waits on the answer, and so does every epic sliced after it. Phill does the steps that need a person: closing OneNote, running elevated, opening a dialog, and locking a section.

## Approach

Time box: two working sessions. At the end, record the best answer so far, even if some rows are still open.

1. **Set up a throwaway notebook.** Create a notebook called "MCP Spike" through COM (`OpenHierarchy` with `cftNotebook`) in a scratch folder outside the repo and outside OneDrive. Every write probe first checks that its target ID sits inside this notebook, and refuses otherwise.
2. **Capability probes in PowerShell.** These need no build setup and don't commit to a language.
   - Read: `GetHierarchy` at each scope; `GetPageContent` with and without binary content (images, attachments); the XML schema namespace returned; timing on Phill's largest real notebook (read-only).
   - Write: `CreateNewPage`; `UpdatePageContent` to append an outline, and to edit existing text, both with and without `dateExpectedLastModified`; deliberately trigger the last-modified conflict; create and rename a section with `UpdateHierarchy`; `DeleteHierarchy`, then check whether the item lands in OneNote's recycle bin.
   - Search: `FindPages` straight after a write, and again later, against Windows Search index state.
   - Failure modes: OneNote closed (does creating the COM object start it?); the probe elevated while OneNote isn't; a modal dialog open in OneNote; sync in progress; a password-protected section, both locked and unlocked.
   - Extras: `Publish` to PDF and to docx; `NavigateTo` a page.
3. **Host probes.** Build a minimal MCP server in each of C# (the ModelContextProtocol NuGet package) and Python (the `mcp` package, or FastMCP). Each exposes one tool that calls `GetHierarchy`, run from Claude Code over stdio. Note how each one gets the COM call onto an STA thread, how much code that takes, and whether parallel tool calls break it. Confirm current SDK versions on nuget.org and pypi.org before installing; the web search on 2026-11-03 gave conflicting numbers.
4. **Write it up.** Write the findings doc and log each finding that binds the architecture as a `constraint` entry in the architecture memlog.

Good enough: every probe in step 2 has a result (works, fails with a HRESULT, or not tested with a reason), both host probes run, and the doc recommends a stack with its reasons.

## Acceptance Criteria

1. **Every COM method the MCP server would rely on has a recorded result from a real call on this machine**
   Verify: the capability matrix in `spike-what-can-the-onenote-desktop-com-api-do-on-this-machine-findings.md`, one row per probe, giving the result and the HRESULT where it failed
2. **The failure modes are reproduced, and each one maps to a HRESULT or an observed behaviour**
   Verify: the failure-modes table in the findings doc
3. **No pre-existing notebook was changed by the spike**
   Verify: the probe log shows every write targeting an ID under the spike notebook, and Phill confirms his notebooks are untouched
4. **Both MCP hosts were run from Claude Code calling `GetHierarchy`, and their STA handling is compared**
   Verify: the probe projects are in a scratch folder named in the findings doc, and the comparison and recommendation are in its Stack section
5. **The findings that bind the architecture are logged where the architecture run will find them**
   Verify: `constraint` entries dated during the spike in `architecture-onenote-mcp/.memlog.md`
6. **The findings doc passed the Fable final review, and Phill accepted it**
   Verify: a dated `Decision:` line in this ticket's Notes naming Phill, written after the confirmed Fable findings were resolved

## References

- parent — _bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/epic-read-only-release.md
- architecture (paused) — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/.memlog.md
- project constraints — CLAUDE.md, section "OneNote COM constraints the design has to respect"
- Fable final-review rule — _bmad/custom/fable-final-review.md

## Notes

- Assumption: the M365 Click-to-Run `Office16\ONENOTE.EXE` is the only OneNote installed here. On 2026-11-03 the registry check showed `OneNote.Application` mapping to it and found no UWP OneNote.
- Decision (2026-11-03, Phill): risk stays medium. Deletes only target the spike notebook, behind the ID guard in Approach step 1.
- Decision (2026-11-03, Phill): the speed probe reads his largest real notebook, read-only.
- Decision (2026-11-03, Phill): ticket approved as refined.
- Decision (2026-11-03, Phill): confirmed he edited none of his own notebooks during the spike. Together with the guard logs and the before/after comparison at 13:07 and 13:50 (0 of 35 changed), this closes AC3.
- Decision (2026-11-03, Phill): accepted the findings doc after the Fable final review. Every confirmed Fable finding was fixed, including an indexed-location search re-run, and CLAUDE.md's COM constraints were updated to match. The findings doc is marked final. Recommended stack: C# with a hand-declared `[ComImport]` IApplication. The architecture run makes the final call.
