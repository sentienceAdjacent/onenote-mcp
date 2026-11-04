---
type: research
title: "Lessons from OneMore"
parent: epic-read-only-release
status: draft
created: 2026-11-03
updated: 2026-11-03
---

# Lessons from OneMore

## Purpose and policy

[OneMore](https://github.com/stevencohn/OneMore) (Steven M Cohn, MPL-2.0) is a long-lived OneNote desktop add-in. It drives the same COM API and 2013 XML schema we do. Its [developer documentation](https://onemoreaddin.com/developers/Setup.htm) records years of measured OneNote behaviour and API defects.

How we use it (Phill, 2026-11-03):
- We learn from OneMore and write our own code. We don't copy its source.
- We credit it in the README for what we learned.
- We keep referring back to its code and docs while building, and especially when stuck. Check there before inventing a workaround.
- If a story ever needs to copy code: keep it in its own files, with the MPL-2.0 header and the original copyright, and list it in a third-party notices file. MPL's copyleft applies per file. It doesn't extend to the rest of the repo, but the copied files stay MPL-2.0.

Sources studied: the repo at commit `10d4a90a63853c08859497bbfa9fad587e46b581` (2026-11-03), and these developer pages: COM Layer, Page Identity, Pipeline, Full Text Search, Command Service, and the TechNotes on Editing Text, Hyperlinks, Interop, ID Stability, Window Focus, Styles, QuickFiling, COM Surrogate, plus OneNote Defects. In the paths below, `OneMore/` is the main project folder of that repo.

## Findings that bind or change our design

| # | Finding | Evidence | Affects | Owner |
|---|---------|----------|---------|-------|
| 1 | `UpdatePageContent` merges by objectID. Content left out stays on the page, and content with no objectID is added. A replace needs explicit `DeletePageContent` calls. | `OneMore/Commands/Search/SummarizeHighlightsCommand.cs:183`, `Commands/Compare/HierarchyDiffSync.cs:391` | AD-7 | epic 2 |
| 2 | Some structural edits to elements OneNote already holds are silently ignored. The workaround strips objectIDs, which gives the elements new IDs. | `Commands/Numbering/ConvertHeadingsCommand.cs:86`, `RestoreBackgroundImagesCommand.cs:107` | AD-7 (read-back check after write), AD-17 (hash keys) | epic 2 |
| 3 | `GetPageContent`'s page-level `lastModifiedTime` is always the current time. Only `GetHierarchy` reports the real value. | Defects page: "page lastModifiedTime does not match hierarchy page lastModifiedTime" | AD-17: take `meta.lastModifiedTime` from the hierarchy | story 8, spine update |
| 4 | Closing and reopening a notebook regenerates every notebook, section, page and object ID. A cloud notebook then refills over about 90 s. A move changes only the moved page's ID. | TechNote "OneNote ID Stability" | AD-6 grant key (Deferred item): key by `path`, not ID. AD-8 journal: IDs go stale. AD-12: a partial notebook can look like deletions | story 5/9, epic 2, spine update |
| 5 | The page GUID in `GetHyperlinkToObject`'s link survives moves and reopens. It costs about 14 ms per page and isn't always unique. | TechNote "OneNote ID Stability" | AD-8 restore targeting, future identity | epic 2 |
| 6 | Microsoft was asked (Oct 2025) for merged-cell support in the Interop schema. Tables with merged cells are flattened through the 2013 schema. The undocumented `xs2026` on this machine adds `Cell@colSpan`/`rowSpan` (our probe, 2026-11-03). It's inferred, not confirmed, that 2026 is Microsoft's answer. OneMore has no 2026 or merged-cell support. | Defects page: "Feature Request: Add Support for Merged Table Cells…" | AD-4 schema choice | spike before epic 2 |
| 7 | `0x800706BE` can mean bad XML (an equation in a linked paragraph), not a disconnect. A disconnected wrapper raises `InvalidComObjectException`. | `OneMore/OneNote.cs:440-445`, `:419-428` | AD-3 reconnect triggers | story 5 |
| 8 | Recycle-bin flags sit on the section group (`isRecycleBin`) and its sections (`isInRecycleBin`), not on pages. Windows Search mishandles symbols in queries. `FindMeta` is unreliable. | `SearchAndReplaceCommand.cs:292`, `OneNote.cs:2314`, `:2352-2366` | AD-12 | story 9 |
| 9 | OneMore validates every update against the 2013 XSD after pruning the XML to a partial update. That's evidence (not proof) that the XSD accepts partial XML. Its copy is `Reference/0336.OneNoteApplication_2013.xsd`: 47,957 bytes, SHA-256 `2f3a01f0767b636975406bb418a24c8b904dddc986e80ae2344c9f63f63bfc10`, no recorded provenance. | `OneNote.cs:1905-1920`, `:1956-2019` | AD-16 | story 6 |
| 10 | Release intermediate COM wrappers (`Windows`, `Window`) explicitly, because finalizer release can stall OneNote. Activation itself hits RPC errors too, so activation needs the same handling as calls. | Design "COM Layer" (issue #2455), `OneNote.cs:347-349` | AD-2, AD-3 | story 5 |
| 11 | Calling `new Application()` from an external process against Microsoft 365 OneNote can give a "blank second instance". Not reproduced here: the spike and story 1.2's 12 integration tests all worked. | `OneMoreCli/AddinBridge.cs:17-22` | AD-3 | story 5 (watch) |
| 12 | Side effects of a write on unchanged content: every child of a touched Outline gets a new `lastModifiedTime`; a local style duplicating its QuickStyleDef is dropped; a live selection across an equation can corrupt it; `&nbsp;` is rejected in CDATA (use `&#160;`). | Defects page; `Styles/ColorStabilizer.cs:14-20`; `StringExtensions.cs:404` | AD-7 validation, AD-17 (hash already excludes dates) | epic 2 |
| 13 | HRESULTs we don't map: `0x80042020` schema invalid, `0x80042022` content loss, `0x80042023` timed out, `0x80042024` recording in progress, `0x8004202E` edit failed in Recycle Bin. | `Helpers/ErrorCodes.cs:22-71` | AD-9 (closed list: amend to add) | story 3 |
| 14 | `NavigateToUrl` fails with `0x80042014` if called again within about 2 s. With two windows open on one page, the API targets the first window. | Defects page | AD-11 | epic 4 |

## Where we deliberately differ

- **Concurrency.** OneMore passes `DateTime.MinValue` with `force:true` and retries failed writes on a fresh connection. That suits a single interactive user. Ours are concurrent agents with a journal, so we keep AD-7's basis check, AD-17, and no automatic write retry. OneMore's practice means our path is untested, not wrong.
- **Lifetime.** OneMore creates a short-lived `Application` per command. We keep one STA-owned reference per process (AD-2), released when idle (AD-3).
- **Binding.** OneMore uses the PIA on .NET Framework 4.8. We hand-declare `IApplication` (AD-1, proven in story 1.2).

## Where to look first when stuck

- COM failures and retries: Design "COM Layer"; `OneMore/OneNote.cs` (`InvokeWithRetry`); `Helpers/ApplicationFactory.cs`.
- Page XML editing and styles: TechNotes "Editing Text" and "Styles"; `Models/Page.cs`, `Models/PageEditor.cs`.
- IDs, links and identity: TechNotes "OneNote ID Stability" and "Hyperlinks"; Design "Page Identity".
- Search and background reading: Design "Full Text Search" and "Pipeline" (throttling reads so OneNote stays responsive).
- Known API defects: the "OneNote Defects" page, which links Microsoft feedback threads.
- Tests without OneNote: `OneMoreTests/MockApplication.cs`, the same shape as our `FakeGateway`.
