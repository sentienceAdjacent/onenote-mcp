---
title: 'Findings: write probes, part 1: pages, paths and search'
type: 'spike-findings'
findings_for: '2'
status: 'final'
created: '2026-11-10'
machine: 'ONENOTE.EXE 16.0.20527.20150 (M365 Click-to-Run), Windows 11 Pro 10.0.26300, not elevated'
evidence: '%LOCALAPPDATA%\OneNoteMcp-Test\probes\write-probes-*.jsonl (outside the repo)'
---

# Findings: write probes, part 1: pages, paths and search

**Short answer:** page writes behave predictably once four rules are known. Rules 1 and 2 change the spine (AD-7's PageWrite); rule 3 confirms AD-17 and rule 4 narrows AD-3:

1. **A sent Outline replaces its OE list.** `UpdatePageContent` with an Outline that names only some of its OEs deletes the OEs it leaves out. Outlines left out of the page XML are kept. So a page write must send every OE of each Outline it touches.
2. **`DeletePageContent` deletes page-level objects only.** An Outline goes; an OE gives `0x8004200e` and stays. Removing a paragraph is "resend its Outline without it".
3. **The date OneNote checks is the page XML's, not the hierarchy's.** Where the two differed (cloud pages only; on local pages they were always equal), the hierarchy date was rejected and the page-XML date accepted. This confirms AD-17's source-by-target rule.
4. **A modal dialog blocks only `UpdatePageContent`.** With File > Options open, `DeletePageContent`, `CreateNewPage` and the three reads tried (`GetPageContent`, `FindPages`, `GetHierarchy`) went through.

One probe crashed OneNote: `GetPageContent` in `xs2007` on a page just rewritten in `xs2010` (see Failure modes). The gateway's reconnect then started a hidden `ONENOTE.EXE -Embedding`, which breaks AD-3's "never start OneNote silently". The probes now stop on a crash instead; the gateway fix is logged in `deferred-work.md` (Spike 2.2 live probe run).

**How it ran.** Four opt-in scenarios in `tests/OneNoteMcp.Integration/WriteProbes/`, run on 2026-11-10 with Phill present, on MCP Test (local) and MCP Test Cloud (OneDrive). Every write went through `TestNotebook.WriteAsync` and its guard (AD-16); no write reached any other notebook. Each row below cites a probe name: search for it in the JSONL logs. The OneDrive account ID is written `<cid>`.

**Which run counts.** Local: `write-probes-local-20261110-131118` up to `hr-read-xs2007` (merge, ignored edits, side effects, width, pathCache); its rows after that call came from the hidden OneNote started after the crash and don't count. Then `-132944` (dates, first modal run), `-143038` (modal, Outline target) and `-155135` (untouched children). Cloud: `write-probes-cloud-20261110-141059`. Search: `write-probes-search-20261110-141213`. Paths: `write-probes-paths-20261110-151701`, `-172452`, `-185620`.

The Paths probes also answer the spine's two open questions on whether notebook and section paths are stable (constraint 9).

## Constraints for the architecture

Logged as `constraint` lines in `architecture-onenote-mcp/.memlog.md`.

1. **AD-4 / AD-7 (PageWrite): send whole Outlines.** A page write that touches an Outline sends all of that Outline's OEs, with their objectIDs, as read. Omitting one deletes it (`merge-omit-oe`, `side-untouched-lmt-partial`). Validate this in step 4: an Outline in the request must list every OE the step-3 read has under it, unless the caller asked to remove it.
2. **AD-7 (PageWrite): removal.** To remove an OE, resend its Outline without it. `DeletePageContent` is for whole page-level objects; only an Outline was deleted here (`merge-delete-outline`), and image and ink are the API docs' claim, not probed. On a freshly read OE it returns `0x8004200e` (`dates-*-DeletePageContent-*` with `targetPrepared: true`, the cloud text rows, `modal-delete-page-content` in `-132944`). `merge-delete-oe` doesn't count: its OE had already been removed by `merge-omit-oe` (`bGone: true`).
3. **AD-17: the date.** `dateExpectedLastModified` must be the page XML's root `lastModifiedTime`. The hierarchy's value was rejected (`0x80042010`) whenever it differed (`dates-cloud-text-*-hierarchy`, `dates-cloud-table-UpdatePageContent-hierarchy`). AD-17 already says this; these rows are the evidence.
4. **AD-17 / AD-12: the date doesn't move on pages not shown.** After an accepted `UpdatePageContent`, the page XML's `lastModifiedTime` stayed the same on every page that wasn't displayed (local and cloud), and advanced on the displayed cloud page (`dates-cloud-table-UpdatePageContent-pageXml`: 14:11:35 → 14:11:49, hierarchy caught up too). The displayed local page was inconclusive (`dates-local-displayed-UpdatePageContent-pageXml`: 13:50:05 before and after, the same second), so "advances when displayed" rests on that one cloud row. This repeats spike 1's row 18: the date check alone can't catch a second writer on a page that isn't shown. The hash check stays necessary.
5. **AD-17: hashes.** Resending a whole Outline bumps `lastModifiedTime` on every OE in it, edited or not (`side-untouched-lmt-whole-outline`). `ElementHash` already excludes `lastModifiedTime`, so hashes stay stable; anything else keyed on an OE's time would not.
6. **AD-7 (PageWrite): the cloud-image refusal can go.** Resending the Outline that holds an image with no local binary kept the image (same `CallbackID`, still `0x8004200f` afterwards), and a copy in a new Outline kept it too (`cloud-image-resend-outline`, `cloud-image-new-outline`). The validate rule "refuse to replace an Outline holding an image with no local binary" protects against a loss that didn't happen. Entry 3 decides whether to drop it or keep it as caution (one image, one run).
7. **AD-7 (PageWrite): read back what OneNote may ignore.** Changing an existing OE's `quickStyleIndex` is silently ignored (`ignored-quickstyleindex`: `applied: false`, page hash unchanged). Moving an OE to another Outline deletes it and creates a copy with a new objectID (`ignored-oe-move`). Unlocked column widths sent when creating a table are replaced: 80 and 120 read back as 37.11 and 37.11, while the locked 105.51 was kept (`width-fresh-mcp-test-table`). A write tool must re-read and compare, and treat objectIDs as possibly replaced after a move.
8. **AD-3: modal dialog.** `0x80042030` came only from `UpdatePageContent` (`modal-update-page-content`). The retryable `ModalDialogOpen` error (AD-9) therefore applies to page edits; the other methods can't be assumed to fail when a dialog is open.
9. **AD-6 / AD-21: keys.** This answers the spine's open questions on notebook and section path stability.
    - Close and reopen: every notebook, section and section group got a new ID; every `path` stayed (`paths-after-close-reopen`, local and cloud).
    - Rename in OneNote: a notebook's display name is its `nickname`; its `name`, `path` and ID stay. A section rename renames its file, so the section's `path` changes and its ID stays (`paths-after-rename-in-onenote`; the section renamed was Probes, in both notebooks). Renaming back restored every path.
    - Local folder move (close, rename the folder, reopen): the notebook's `name` follows the folder, and every `path` and ID changes (`paths-after-rename-local-folder`). Moving back restored the paths, not the IDs.
    - OneDrive rename with the notebook open: OneNote picked it up with no sync error; the `name` and every `path` changed and the IDs stayed (`paths-after-rename-onedrive-item-back`, the rename back). For the first rename Phill closed and reopened the notebook without waiting for a sync error, so that row's ID change comes from the reopen (`paths-after-rename-onedrive-item`); only the rename back shows a live rename.
    - So keying on the path (`NotebookKey`, `SectionKey`) is right: it holds through close and reopen and through OneNote display renames, where IDs don't. A folder move, a OneDrive rename or a section rename changes the key, `list_grants` (AD-10) shows the grant as `notebookNotOpen` or `sectionNotOpen`, and the user grants again. That fails closed, as AD-21 already says for sections.
    - A grant left on an old path applies to whatever notebook is later opened at that path, as AD-6's Limits already accept.
    - Forms: local notebook `C:\…\MCP Test` (no trailing separator), section `…\MCP Test\Seed.one`, section group `…\MCP Test\Ballast Tree`. Cloud notebook `https://d.docs.live.net/<cid>/Documents/MCP Test Cloud/` (trailing slash), section `…/Probes.one`, section group `…/Group A/` (trailing slash).
10. **AD-12: search.**
    - `FindPages` and `FindMeta` with unindexed pages return every page under the start node, unfiltered (`search-symbol`: 524 of 524 for every query; `findmeta-*`: 525). The post-filter is the whole filter.
    - Indexed-only: a fresh page was missed at 0 s and found after 30 s, for text and for meta (`search-symbol-indexed-later`, `findmeta-fresh-page`).
    - Symbols: every query of the 20 found its page once indexed. `a+b`, `a-b` and `a/b` also matched 3 other pages, so OneNote splits words at `+`, `-` and `/` (`search-symbol-indexed-later`). AD-12's own text rule decides the result, so this only widens candidates.
    - `FindMeta` is exact once indexed: 503 of 503 and 100 of 100 Ballast pages, no recycle-bin pages (`findmeta-ballast`).
11. **AD-21: protected sections in search.** `encrypted="true"` is on Ballast Vault in the hierarchy, `FindPages` and `FindMeta` XML, locked and unlocked; `locked="true"` only while locked. Locked: no pages listed anywhere. Unlocked: `FindPages` and `FindMeta` list its 3 pages (`search-encrypted-vault-first`, `-second`). So `encrypted` is the reliable marker, and search does return protected pages once unlocked, as AD-21 assumes.
12. **AD-4: merged cells.** Writing a table with `colSpan` in `xs2026` is accepted, and a `xs2013` or `xs2010` round trip of the unchanged Outline keeps the merge (`hr-content-loss-*`, `merged-*`). `0x80042022` (future content loss) never appeared. `xs2013` stays safe for tables with merged cells, provided the Outline is sent back as read. Editing inside a merged table in `xs2013` was not probed (see Evidence gaps).
13. **AD-9: error codes.** The page-write HRESULTs map to AD-9 codes as the logs record: `0x80042010` → `StaleWrite`, `0x8004200e` and `0x80042014` → `ObjectNotFound`, `0x80042030` → `ModalDialogOpen`, `0x80042000` and `0x80042001` → `InvalidXml`, `0x800706BE` → `Internal`. No new code is needed.
14. **AD-16: the XSD gate.** The spine makes XSD a hard gate only once a story proves the XSD accepts partial-update XML. This spike didn't validate against the XSD, so that question stays with the story that builds PageWrite. What it adds: partial-update XML that OneNote accepts can still delete content (an Outline naming only some OEs, constraint 1). Schema validity can't catch that; the step-4 Outline check in constraint 1 does.

## Results, one row per Matrix question

| Area | Question | Result | Probes |
|---|---|---|---|
| Merge | Partial update omitting an OE: kept? | **No, deleted.** Within a sent Outline the OE list is replaced. Outlines not sent are kept. | `merge-omit-oe`, `merge-add-outline-no-objectid` |
| Merge | OE without objectID: added? | Yes, added in the sent Outline, in order. A new Outline without objectID is added beside the others. | `merge-add-oe-no-objectid`, `merge-add-outline-no-objectid` |
| Merge | Does a replace need `DeletePageContent`? | No. Resending the Outline replaces its OEs. `DeletePageContent` removes a whole Outline (its OEs go with it) and refuses an OE (`0x8004200e`). | `merge-delete-outline`; for the OE: `dates-*-DeletePageContent-*`, `modal-delete-page-content` (`-132944`) |
| Ignored edits | OE moved between Outlines | Not ignored: it is deleted from the old Outline and re-created in the new one under a new objectID. Hashes change. | `ignored-oe-move` |
| Ignored edits | Table column count | Applied (a third Column and Cell added). | `ignored-table-columns` |
| Ignored edits | Position/Size | Applied (both). | `ignored-position-size` |
| Ignored edits | quickStyleIndex on an existing OE | **Silently ignored**; page hash unchanged, so only an attribute read-back catches it. Setting it on a new OE works. | `ignored-quickstyleindex`, `ignored-quickstyleindex-setup` |
| Cloud image | Replace the Outline holding Image Page's undownloaded image | Kept: same `CallbackID`, binary still unavailable. A copy into a new Outline also kept it. No error. | `cloud-image-precheck`, `cloud-image-resend-outline`, `cloud-image-new-outline` |
| Side effects | Untouched children's `lastModifiedTime` | Whole Outline resent: every OE's time bumps. Partial: omitted OEs are deleted (above). | `side-untouched-lmt-whole-outline`, `side-untouched-lmt-partial` (run `-155135`) |
| Side effects | Local style duplicating its QuickStyleDef | Dropped: an OE `style` equal to QuickStyleDef `p` doesn't survive; a differing style does. | `side-local-style-duplicate` |
| Side effects | `&nbsp;` in CDATA | All three forms (`&nbsp;`, `&#160;`, a literal U+00A0) read back as the text `&nbsp;`, never the character. | `side-nbsp-entity`, `-numeric`, `-literal` |
| Modal dialog | HRESULT per method with File > Options open | `UpdatePageContent` `0x80042030` (not applied). `DeletePageContent` (Outline) ok, applied. `CreateNewPage`, `GetPageContent`, `FindPages`, `GetHierarchy` ok. Story 1.5 had already seen CreateNewPage pass (`LifecycleScenarioTests.cs:145-146`). Only this dialog was tried. | `modal-*` (run `write-probes-local-20261110-143038`) |
| HRESULTs | Each error and its trigger; `0x80042022` | See the HRESULT table. `0x80042022` not triggered. | `hr-*` |
| Dates | Which date each write method accepts | Page-XML date: accepted by `UpdatePageContent`. Hierarchy date: rejected `0x80042010` where it differed from the page-XML date (cloud Text Page, not displayed, both methods; cloud Table Page, displayed, `UpdatePageContent`), accepted where equal. Stale (1 h older): rejected `0x80042010`. Local pages never had differing dates. For `DeletePageContent`, "accepted" is inferred: every target was an OE, so the call then failed `0x8004200e`, and OneNote checks the date first. Gaps under Evidence gaps. | `dates-local-*` (run `-132944`), `dates-cloud-text-*`, `dates-cloud-table-*` |
| Width | Do unlocked `Column/@width` values alternate? Locked? | No. 20 reads each of the cloud Table Page and a fresh MCP Test table: one value set, one page hash. OneMore's alternation didn't reproduce here. On create, the locked width 105.51 was kept and the unlocked 80 and 120 were replaced by 37.11 each (constraint 7). | `width-cloud-table-page`, `width-fresh-mcp-test-table` |
| pathCache | Does `InsertedFile/@pathCache` change every read? | No. 20 reads of a Ballast page with 6 attachments: one value, one page hash. | `pathcache-ballast-files` |
| Merged cells | 2013 round trip of Table Page; `xs2026` with `colSpan` | Merge survives the 2013 round trip. `xs2026` update accepted. In 2013 XML the merged row shows as 3 cells with one emptied; 2026 shows 2 with `colSpan=2`. | `merged-precheck`, `merged-xs2026-update`, `merged-xs2013-roundtrip` |
| Paths | `path` after close/reopen, rename in OneNote, local folder rename, OneDrive rename | Close/reopen: IDs change, paths stay. OneNote rename: notebook `nickname` only; section path changes, ID stays. Local folder move (needs a reopen): name, paths and IDs change. OneDrive rename while open: no sync error, name and paths change live, IDs stay. Local and cloud alike. | `paths-initial`, `paths-after-*` (runs `write-probes-paths-20261110-151701`, `-172452`, `-185620`) |
| Search (Q5) | `FindPages` with symbols | 20 symbol queries: all found once indexed; `+ - /` split words. Unindexed: unfiltered. | `search-symbol`, `search-symbol-indexed-later` |
| Search (Q5) | `FindMeta` reliability | Exact once indexed; a fresh page needs indexing (missed at 0 s, found at 30 s). Unindexed: unfiltered. | `findmeta-fresh-page`, `findmeta-ballast` |
| Search (Q5) | `encrypted` on Ballast Vault in both XMLs | Present in hierarchy, FindPages and FindMeta XML, locked and unlocked. | `search-encrypted-vault-first`, `-second` |
| Search (Q5) | Skill's list and image `alt` claims | Lists are `one:List/one:Bullet` and `one:List/one:Number` on the OE, as claimed; a UI-made bulleted list on Text Page reads the same. Image `alt` is kept. | `skill-list-claim`, `skill-list-claim-ui-page`, `skill-image-alt-claim` |

## HRESULTs observed

| HRESULT | Name | Trigger | Probes |
|---|---|---|---|
| `0x80042000` | hrMalformedXML | unclosed `one:Outline` | `hr-malformed-xml` |
| `0x80042001` | hrInvalidXML | 2010-namespace XML passed as `xs2013`; 2013-namespace XML passed as `xs2007`; an unknown `one:Bogus` element | `hr-xs2010-as-2013`, `hr-xs2007-edit`, `hr-unknown-element` |
| `0x8004200e` | hrPageObjectDoesNotExist | an Outline objectID not on the page (update); an unknown or already-removed objectID, or an OE (delete) | `hr-unknown-objectid`, `hr-delete-unknown-objectid`, `merge-delete-oe` (already removed), `dates-*-DeletePageContent-*` (OE) |
| `0x8004200f` | hrBinaryObjectDoesNotExist | the cloud image's binary, before and after the rewrite | `cloud-image-*` |
| `0x80042010` | hrLastModifiedDateDidNotMatch | stale date; hierarchy date that differs from the page XML's | `dates-*` |
| `0x80042030` | hrAppInModalUI | `UpdatePageContent` with File > Options open | `modal-update-page-content` |
| `0x800706BE` | RPC_S_CALL_FAILED | OneNote crashed during the call | `hr-read-xs2007` |
| `0x80042014` | hrObjectDoesNotExist | the next read, served by the hidden instance started after the crash | `hr-read-xs2010` (first run) |

The 2010-namespace edit passed as `xs2010` was accepted (`hr-xs2010-edit`).

## Failure modes

- **OneNote crash on an `xs2007` read.** 08:15:13 local: APPCRASH in `mso20win32client.dll` (OneNote 16.0.20527.20150) during `GetPageContent(…, xs2007)` of a page just rewritten in `xs2010`. The call returned `0x800706BE`. Not repeated; the probe now leaves `xs2007` out. AD-4 already passes only `xs2013`, so the server doesn't hit this, but a tool must never offer a schema choice.
- **Hidden OneNote after the crash.** 08:15:16: `ONENOTE.EXE -Embedding` started by the probes' gateway reconnect, because the crashed process still existed while Windows Error Reporting wrote its dump, so the process check passed. Phill had to end it in Task Manager before OneNote would start. Logged as a gateway bug in `deferred-work.md`. The probes now pin the OneNote process set and refuse a second activation (`PinnedOneNoteProbe`).

## Not probed

- **Equation-selection side effect** (OneMore lessons row 12): not probed (Phill, plan Q2). Our tools can't control the user's selection; the skill gets a caveat.
- **Other modal dialogs.** Only File > Options. Other dialogs may block more methods.
- **A newer-than-current date.** Only stale (older) and equal dates were sent.
- **Renaming a OneNote notebook's folder from OneNote itself, or moving a section between notebooks.** Only the renames listed under constraint 9.
- **`xs2007` reads.** Dropped after the crash.

## Evidence gaps

- **`DeletePageContent` dates.** Never run on a displayed page with differing dates (the displayed cloud page's dates had already caught up), and never succeeded with the page-XML date (every target was an OE). Its acceptance of that date is inferred from error order.
- **Date on a displayed local page.** Same second before and after, so inconclusive; "advances when displayed" rests on one cloud row.
- **Editing inside a merged table in `xs2013`.** Only unchanged round trips ran (`hr-content-loss-xs2013-roundtrip`, `merged-xs2013-roundtrip`).
- **Cloud image.** One image, one run.
- **Page-level deletes other than an Outline.** Image and ink are the API docs' claim.

## Deviations from the plan

- Probe fixes during the live run, all in `tests/`: the crash breaker (`PinnedOneNoteProbe`, `ProbeSession`); `xs2007` read removed; the modal delete retargeted from an OE to an Outline and re-run; the untouched-children probe's order swapped (the partial update ran first and deleted the siblings the whole-Outline check needed) and re-run.
- Paths ran in three sittings. In run `-151701`, `paths-rename-in-onenote` timed out (nobody at the screen) after close and reopen was captured; the run was stopped. In run `-172452` the folder and OneDrive steps timed out (nobody at the screen) and their rename-back steps were skipped; run `-185620` repeated those four steps with `paths-initial`.
- During run `-185620`, two read-only integration tests (`SearchToolsIntegrationTests`, `ProtectedSectionIntegrationTests`) failed `0x80042014` because they ran while MCP Test was closed for the folder move. Re-run afterwards, both passed.
