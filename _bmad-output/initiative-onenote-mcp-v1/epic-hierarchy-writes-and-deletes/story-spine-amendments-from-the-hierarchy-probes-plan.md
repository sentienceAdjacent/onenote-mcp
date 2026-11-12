---
title: 'Spine amendments from the hierarchy probes'
type: 'chore'
ticket: '2'
created: '2026-11-12'
status: 'in-review'
baseline_revision: 'fcb8663f676aaeb379f5690aeb427f1ba0963d9b'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-hierarchy-writes-and-deletes/spike-write-probes-part-2-hierarchy-delete-and-backup-findings.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/epic-round-2-external-and-structural.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Epic 5's entries 3–7 build hierarchy writes and deletes on AD-3, AD-6–AD-10, AD-17 and AD-21, but those rules predate spike 5.1's evidence: `DeleteHierarchy` checks the hierarchy date, `UpdateHierarchy` moves silently, a section group can't be backed up, COM can't delete a notebook, protecting a section changes its IDs, and a protected section's `Publish` backup is plaintext. Five Deferred rows wait on that evidence, and four "next spine story" items from stories 2.4, 2.6 and 2.7 are queued.

**Approach:** An architecture Update, as story 2.3 did: append the decisions to the spine memlog, amend the affected ADs in place (IDs stable), resolve or retarget the Deferred rows, run `lint_spine.py`; then the orchestrating session runs the Fable final review and Phill picks the fixes and approves. Docs only.

## Boundaries & Constraints

**Always:**
- Every amendment cites its evidence: a 5.1 constraint number, a Phill decision, or a deferred-work entry.
- AD IDs never renumbered; new rules go into the AD that already binds the unit.
- Spine `status: final` stays; `updated` changes only when Phill approves the Fable-reviewed amendment.
- No real notebook names; the OneDrive ID is `<cid>`.

**Never:**
- Any `src/` or `tests/` change (entries 3–7 and the remap build do that).
- Epic 3's own spine work (3.2): `ExternalWrite`, export roots, the move tool in AD-5/AD-14, AD-8's `restore_from_recycle_bin` wording, the export and unpacked-package rule in AD-21, merge snapshots.
- New error codes.

## Decisions (Phill, 2026-11-12; plan approved whole, all eleven recommendations)

- Phill 2026-11-12, epic 3 Q9/Q14: moves and merges out of a protected section (a protected section moved whole included) are refused; 3.2 may relax it on 3.1's evidence.
- Phill 2026-11-12, epic 3 Q10: 5.5 reads back a soft-deleted page's recycle-bin ID into its journal entry; AD-8 and AD-10 get the field.
- Phill 2026-11-12, 2.7 Q2: AD-9 remaps `0x80042017` → `InvalidArgument`, `0x80042024` → `ComBusy`, `0x8004202E` → `ObjectNotFound`.
- Phill 2026-11-12, 2.6 Q2: a typed name matching one open notebook other than the target makes no grant (`WriteNotGranted`).
- Phill 2026-11-12, 2.7 Q1: AD-3's 30 s call timeout stays for every write, cloud creates included.
- Q1 (a): a protected section's permanent delete is refused before COM with `NotSupported`; `userAction`: soft-delete it, then empty the recycle bin in OneNote. No backup of it is ever written (c.17).
- Q2 (c): a section group's permanent delete is refused unless step 3 reads it empty (no section or group beneath); then its hierarchy snapshot is the record (c.6).
- Q3 (a): a notebook's soft or permanent delete is refused before COM with `NotSupported`; `userAction`: "COM can't delete a notebook. Ask the user to close it in OneNote, then delete its folder in File Explorer or OneDrive." No `.onepkg` backup (c.18).
- Q4 (b): one basis per page. A page delete's caller passes the page XML date and `pageHash`; the server compares them with step 3's `GetPageContent` read, then sends `DeleteHierarchy` the page's hierarchy date from the same step 3. AD-17's "never replaces a caller's basis date" gets an exception for page deletes only (c.1).
- Q5 (a): a section-group rename in a notebook whose path is a URL is refused with `NotSupported`; `userAction`: rename it in OneNote (c.5).
- Q6 (a): deletes in or of a protected section need only the notebook's write grant (plus the permanent-delete grant for a permanent delete). A page soft delete in a protected section stays refused until 5.5's probe shows the page lands somewhere `encrypted`.
- Q7 (a): this story applies the Design Notes wording to epic 5's epic file and `tickets.toml` (Phill's explicit yes).
- Q8 (a): backups live in `<journal folder>\backups\`, named like journal entries, created before `Publish`, pruned with their journal entry (30 days, inside the 2 GB cap).
- Q9 (a): a permanent delete of a page in the recycle bin is refused before COM with `NotSupported`; `userAction`: empty the recycle bin in OneNote. `0x8004200C` stays `Internal` with 2.7's triage text.
- Q10 (a): 5.2 writes AD-21's plaintext-copies bullet with the backup half (Q1, never opening a protected section's backup, the backup folder counted protected); 3.2 adds exports and unpacked packages to it.
- Q11 (a): the hierarchy date check stays, called best effort; a `StaleWrite` straight after our own write means re-read and retry.
- Added by Phill's request (2026-11-12, relayed by the team lead): story 2.9's accepted answers land in this amendment. Q3 → AD-16: the XSD check is a hard gate for `UpdatePageContent` (2.9 settles it; its `InvalidXml` carries line, position and element name only). Q4 → AD-7 `PageWrite`: an OE sent under a different Outline from the one step 3 read it in is refused with `InvalidArgument`. Q5 → AD-17: an edit also compares the basis hash of each sent OE with an `objectID` outside every sent Outline (the title's OE). Q1/Q2 (`update_page_content(pageId, xml, basis?, removeObjectIds?)`, nested OEs removed with their parent, a fresh `meta.basis` in the result) get spine text only where AD-7, AD-14 or AD-17 would contradict them.

</frozen-after-approval>

## Code Map

- `architecture-onenote-mcp/architecture-onenote-mcp.md` -- the spine. AD-3 (l.66: timeouts, modal dialogs l.90, activation l.86), AD-6 (l.120: who can do what l.125, how a grant is created l.130), AD-7 (l.154: step 5 l.164, table l.186–195), AD-8 (l.197), AD-9 (l.220: codes l.237–253, table l.255–279), AD-10 (l.281, journal entry l.301), AD-11 (l.306), AD-14 (l.353), AD-16 (l.395), AD-17 (l.406: date source l.412, compares l.419, basis l.432), AD-18 (l.440), AD-21 (l.503: Writes l.543–548), Deferred (l.611–644). Frontmatter `sources` gains the 5.1 findings.
- `architecture-onenote-mcp/.memlog.md` -- the 5.1 constraints are l.206–223. Append with `uv run _bmad/scripts/memlog.py append --path <spine folder>/.memlog.md --type decision|event --text "..."`; follow 2.3's lines (l.178–200).
- `epic-hierarchy-writes-and-deletes/spike-…-findings.md` -- constraints 1–18 (l.45–173), Deferred answers (l.305), epic impact (l.315).
- `epic-round-2-external-and-structural/epic-round-2-external-and-structural.md` Notes l.84–98 -- epic 3's decisions; 3.2 owns their spine text except Q9, Q10, Q14.
- `deferred-work.md` l.247 (AD-14 `create_page`), l.259 (AD-6), l.283 (AD-9 triage sync, AD-3 WER rule, lock kept after a step-7 failure), l.286 (remaps), l.289 (30 s stays).
- `src/OneNoteMcp.Com/HResultMap.cs` `Triaged` (l.93) -- read only: every code has a row; `0x8004200B/0C`, `0x80042003`, `0x8004201E`, `0x80042024` carry `Retryable: true`.
- `~/.claude/skills/bmad-architecture/scripts/lint_spine.py --workspace <spine folder>` -- lint; the `{pid}`/`{guid}` hits are known false positives.

## Tasks & Acceptance

**Execution:**
- [x] `.memlog.md` -- one `decision` line per amendment and per answered question; one `event` line when written.
- [x] spine AD-17 -- date source by method, not target: page-content writes send the page XML date, `DeleteHierarchy` the target's hierarchy date (c.1, Q4); hierarchy dates' weakness and the late-moving date (c.2, Q11).
- [x] spine AD-7 -- step 5 per Q4. `Create`: relative names checked before COM (no `..`, `\`, `/`, or characters a Windows file name can't hold; a section name reaches COM ending `.one`), an existing name returns its ID with the warning `alreadyExisted` and no auto-grant, the parent a live notebook or section group, a notebook folder checked empty by the server (c.3, c.16, c.18). `HierarchyWrite`: refuse an ID not already under the node it is listed under (c.4, until epic 3's move tool); left-out siblings stay; sections before groups; names checked before COM and read back, `readBackDiffers` as a warning; a notebook rename sets `nickname` (c.4, c.18); Q5; moves and merges out of a protected section refused (Q9). `SoftDelete`: where each kind goes, the recycle-bin ID read-back (Q10), groups dissolve (c.8), notebooks per Q3, recycle-bin targets refused. `PermanentDelete`: `locked` checked first (`SectionLocked`, c.16), backup folder created first, a backup counts once its file exists, Q1, Q2, Q3, Q9. Verify against the hierarchy, never files (c.10). An ID gone after a protect is re-resolved by path or name (c.15).
- [x] spine AD-8, AD-10 -- `recycleBinId` field (Q10; stale after a reopen or protect, c.15); hierarchy entries of a protected section DPAPI-encrypted like page entries; `onenoteBackup` `userAction` names the file and promises no restore (c.7); backup location and retention (Q8).
- [x] spine AD-9 -- the three remaps; the Codes bullets describe 2.7's triage (every code has a row, `retryable` overrides) instead of "untriaged"; `NotSupported`, `BackupFailed`, `InvalidArgument`, `SectionLocked` sources widened for the AD-7 refusals; hierarchy HRESULTs confirmed (c.13).
- [x] spine AD-3 -- modal dialogs per hierarchy method and the two blocking dialogs (c.11); cloud creates 3.6–12.8 s, 30 s stays (c.12, 2.7 Q1); the WER rule (deferred-work l.283).
- [x] spine AD-6 -- the 2.6 clarification; Q6; IDs change on protect, path keys survive (c.15).
- [x] spine AD-11 -- a create can take focus (c.14). AD-14 -- `create_page` named by Phill (2.4 Q1). AD-16 -- 5.4 settles the XSD gate for `UpdateHierarchy` (c.4). AD-18 -- lock kept after a step-7 failure until the abandoned call settles or the deadline passes (deferred-work l.283).
- [x] spine AD-21 -- Binds and Writes per Q6; the move/merge refusal (Q9); the plaintext-copies bullet per Q1 and Q10; `encrypted` survives the recycle bin and a deleted protected section locks at once, relock isn't quick (c.16); IDs change on protect (c.15).
- [x] spine Deferred -- resolve: notebook/section-group delete, move/merge out of a protected section (for v1), `Publish` backup route, `DeleteHierarchy` date, HRESULT triage, the protected-section-deleted-whole half of "Pages deleted from a protected section". Keep open: protected page soft delete (5.5 probes it). Add: restore from a backup (c.7), OneDrive copy after a permanent delete, a stale date on a permanent delete (5.6 probes it), showing a page moving its date, create under a deleted parent, the cloud sync jam, group reorder among themselves.
- [x] epic 5 `epic-hierarchy-writes-and-deletes.md`, `tickets.toml` -- per Q7.
- [x] `deferred-work.md` -- "Resolved (rule)" entries for the five items taken, naming who builds them (remaps: entry 4).
- [x] lint; fix findings.
- [ ] (orchestrating session) Fable final review, Phill's fixes and approval, memlog `event`, `updated`.

**Acceptance Criteria:**
- Given the amended spine, when each 5.1 constraint 1–18 is traced, then it lands in an AD or a Deferred row, or is recorded as no change.
- Given entries 3–7, when each reads its ADs, then no question from the findings' "Open before final" section is left unowned.
- Given the four deferred "next spine story" items, when the spine is read, then each is in it.

## Implementation Notes

- 2026-11-12: merged `origin/main` (story 2.8, journal) first; AD-8/AD-10 wording matched to what 2.8 built (`{guid:N}` file name, temporary file then rename, optional members left out and unknown ones ignored, the `ONENOTE_MCP_JOURNAL_DIR` test override, pruning by the file name's timestamp, never the newest entry). 2.8 changed no spine text.
- Implemented directly in this lane, not through a subagent: docs only, edited with exact-match file edits (no shell escapes). An earlier Python edit to this plan turned `\b` in `\backups\` into a backspace byte; fixed, and the spine, memlog, epic 5 files and deferred-work were checked for control bytes (none).
- Story 2.9 Q3, Q4 and Q5 (Phill, 2026-11-12) were folded in at the team lead's request: AD-16 (hard XSD gate for `UpdatePageContent`), AD-7 `PageWrite` (no OE moves between Outlines), AD-17 (hashes of sent OEs outside sent Outlines). 2.9 Q1/Q2: AD-7 now names `removeObjectIds` and says nested OEs go with their parent; AD-17 records the fresh `meta.basis` in an `update_page_content` result. AD-14 holds no argument lists, so nothing there contradicted them.
- Beyond the plan's wording: AD-18 also records deferred-work's forward note (basis-checked pipelines check for an abandoned write right after step 2, `ComBusy`); AD-21's Writes bullet keeps "other hierarchy writes get no write-grant-alone exemption", so an `update_hierarchy` rename of a protected section itself is still unsettled (not asked in Q6).
- Known stale (Fable F15): `tickets.toml` entry 3's description still says "the parent-exists-and-not-locked check" and doesn't name AD-7's `OpenHierarchy` create rules (relative names, `alreadyExisted`, live notebook or group parent, empty folder or `FileExists`). Story 5.3 refines it at its start; this story left it alone because Q7 covered only the wording Phill approved.
- Lint: only the known `{pid}` false positive (the `.tmp` wording was rephrased to avoid a new `{name}` hit). The step-04 review lens wasn't run in this lane; the orchestrating session runs the Fable final review.

## Plan Change Log

## Review Triage Log

Fable final review of `1655b7b` (run by the orchestrating session, 2026-11-12): 21 findings. Phill decided F1, F2, F3 and F6 and chose to fix all the rest, as the reviewer suggested. Applied in this lane; each Phill decision is a memlog `decision by Phill` line.

| # | Finding | Route | Action |
|---|---|---|---|
| F1 | How `recycleBinId` gets into an entry already written once | Phill | The entry is rewritten once after the delete (temp file, then replace, under the lock); a failed read-back or rewrite leaves the field out with a warning and never fails the delete. AD-7, AD-8, AD-10 |
| F2 | `update_hierarchy` on a protected section had no grant rule | Phill | Rename or reorder of a protected section, or reordering its pages: write grant alone; a rename changes its path, so its read grant stops matching (fails closed). AD-6, AD-21 |
| F3 | Codes for the protected page soft delete and the move/merge refusal | Phill | `NotSupported`, `userAction` to do it in OneNote. AD-7, AD-9, AD-21 |
| F4 | `Create` rules written as if they covered `CreateNewPage` | patch | Scoped to `OpenHierarchy` create; `CreateNewPage`'s own rule restored (live section, not locked; 2.4 Q8) |
| F5 | The locked check before `Publish` is unreachable (a locked section is protected) | patch | Dropped; the order is stated |
| F6 | Soft delete of a locked protected section | Phill | `SectionLocked` until the user unlocks it |
| F7 | AD-17's hierarchy-date acceptance rests on one hidden-page row | patch | Reworded; Deferred row: 5.5 probes a displayed page with its hierarchy date |
| F8 | The recycle bin itself (`isRecycleBin`) as a target | patch | Refused as create parent, soft-delete and permanent-delete target |
| F9 | An empty section group's permanent delete was never run | patch | Deferred row; entry 6 probes it; empty or nested group soft delete unprobed |
| F10 | Existing-name create: class, grant, case | patch | Stays `Create`, grant checked; names compared ignoring case; the group case unprobed |
| F11 | Non-empty notebook folder had no code | patch | `FileExists`, AD-7 and AD-9 |
| F12 | AD-16 "either gate" unclear | patch | Reworded |
| F13 | Stray tool-markup lines at the plan's end | patch | Deleted |
| F14 | Modal-dialog claims overreach | patch | "of the methods tried", AD-3 and Deferred |
| F15 | `tickets.toml` entry 3 stale against AD-7 | note | Story 5.3 refines its description at start; recorded in Implementation Notes |
| F16 | `restore_from_recycle_bin` in the `HierarchyWrite` row reads as epic 5's | patch | Marked epic 3's |
| F17 | Path-based protection (backup folder) not cross-referenced | patch | AD-21 "What's protected" and AD-8 |
| F18 | Backup name and orphaned backups | patch | `.one` replaces `.json.gz`; the pruner removes a backup with no entry |
| F19 | Does `0x80042024` → `ComBusy` back off? | patch | Conservative reading: `ComBusy` at once, no backoff, like `0x8004201D`. AD-3, AD-9 |
| F20 | Three wrong citations | patch | `0x8004201B` cited to constraint 16; AD-17 no longer names a notebook delete; AD-7 stored-ID bullet rewritten |
| F21 | Placement and clarity in AD-17; `HierarchyWrite` read-back failure | patch | Basis bullet moved, two sentences split, `readBackFailed` added; AD-18 reworded as given |

Also added (Phill, 2026-11-12): from story 2.9's security review, basis limits without a read grant (AD-17, AD-21), user-gated file paths (AD-7, AD-6; refusal `WriteNotGranted`), DOCTYPE refused (AD-16); story 2.10 Q1, `delete_page_content` in a protected section needs the read grant (AD-21, AD-6, AD-7, AD-9); and write and read grants stay separate (memlog only).

Fable re-check of `9a33dfa` (orchestrating session, 2026-11-12): N1–N10. Phill decided N1, N4 and N6; the rest were fixed as suggested.

| # | Finding | Route | Action |
|---|---|---|---|
| N1 | A page permanent delete in a protected section could carry a `pageHash` without a read grant | Phill | Date-only rule covers it: a hash is `ReadNotGranted`. AD-6, AD-9, AD-17, AD-21 |
| N2 | The path exemption was loose | patch | Exempt only when unchanged on the same element (same `objectID`); no `objectID`, or a protected section without a read grant, means new; `pathCache` gated; schema terms |
| N3 | Step 1's order unstated | patch | Parse (DOCTYPE), read-grant check, write-grant dialog, path dialog; step 4 `WriteNotGranted`; AD-16 reconciled |
| N4 | The protected page soft-delete `userAction` | Phill | Points to a permanent delete. AD-7, AD-9, AD-21 |
| N5 | "existing `objectID`" undefined | patch | "any `objectID` attribute in the sent XML, or any `removeObjectIds` entry" |
| N6 | Page-reorder ID oracle | Phill | Accepted (GUIDs); `update_hierarchy` warnings counts-only in a protected section |
| N7 | No-elicitation path refusal retryable | patch | `retryable: false`, `userAction` "send the image as inline one:Data, or insert the file in OneNote yourself" |
| N8 | The path dialog's shape | patch | Accept or decline, naming each canonical path; AD-6's Limits apply |
| N9 | Entry 5 missing its probes and new outcomes | patch | Two probes, `SectionLocked`, `recycleBinIdMissing` added to `tickets.toml` |
| N10 | Leftover wording; DOCTYPE in the class table | patch | AD-6 and AD-21 wording fixed; "no DOCTYPE" in the `PageWrite`, `Create` and `HierarchyWrite` rows (the delete rows send no XML); `once` grant per process; `recycleBinIdMissing` needs no `userAction` |

## Design Notes

Draft epic 5 wording (Q7), assuming the recommended answers:
- Outcome: "… A section is permanently deleted only after a OneNote-format backup file of it exists. Restoring from that file isn't proven, and COM can't delete a notebook."
- Done when 1: "soft-deleting a page, section or section group".
- Done when 2: "… whose basis date has moved returns `StaleWrite` …; the check is best effort, since a rename doesn't move the renamed node's date (AD-17)." Entry 4 verify: "a rename with a moved basis date returns `StaleWrite`".
- Done when 4 and entry 6: "A section is deleted only after its backup file exists, and a failed backup refuses the delete. A non-empty section group, a notebook and a protected section are refused with the reason."
- Notes, decision 3: superseded for page writes by 2.3 Q3c; deletes follow Q6.

## Verification

**Commands:**
- `uv run ~/.claude/skills/bmad-architecture/scripts/lint_spine.py --workspace _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp` -- expected: only the known `{pid}`/`{guid}` hits.
- `git diff --cached | grep -i -c -E "<standing privacy pattern list>"` -- expected: 0.
