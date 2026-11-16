---
title: 'Spine amendments for round 2'
type: 'chore'
ticket: '2'
created: '2026-11-16'
status: 'draft'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/spike-write-probes-part-3-exports-move-merge-and-recycle-bin-restore-findings.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/epic-round-2-external-and-structural.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Epic 3's entries 3–13 build exports, moves, merges and recycle-bin restore on AD-3, AD-5, AD-7–AD-9, AD-11, AD-12, AD-14, AD-17 and AD-21, but those ADs still say "epic 3's spine amendments (3.2) add…" where the rules should be. Spike 3.1 (final, PR #54) now has the evidence: `pfOneNote2007` and `MergeFiles` crash OneNote, notebook export works only as a package, moves leave subpages and partial copies, the recycle bin is unguarded, packages keep protected sections encrypted, and `OpenPackage` opens no notebook.

**Approach:** An architecture Update, as stories 2.3 and 5.2 did: append the decisions to the spine memlog, amend the affected ADs in place (IDs stable), resolve or add Deferred rows, mark the related deferred-work entries, run `lint_spine.py`. Then the orchestrating session runs the Fable final review at `effort: low` (memory: Fable reviews at low effort), Phill picks the fixes and approves, and only then is the spine's `updated` set. Docs only.

## Boundaries & Constraints

**Always:**
- Every amendment cites its evidence: a 3.1 Implication number and its strength (one run or repeated), a 5.1 constraint, a Phill Decision, or a deferred-work entry.
- AD IDs never renumbered; new rules go into the AD that already binds the unit.
- Spine `status: final` stays; `updated` changes only when Phill approves the Fable-reviewed amendment.
- A rule resting on one run says so (the settle, the opened package's `locked`, labels).
- No real notebook names; the OneDrive ID is `<cid>`. Probe items keep their `probe …` names only where the findings already use them.

**Never:**
- Any `src/` or `tests/` change; each rule names the entry that builds it.
- A `merge_files` tool or `MergeFiles` on the port: 3.9 is dropped.
- Relaxing Q1, Q9, Q11 or Q14 beyond what Phill answers to H1 and H2.
- Epic 5's pending "Spine sync" deferred-work entries beyond those J7 settles.

## Decisions already made (Phill, 2026-11-16; memlog lines 286–287, `tickets.toml` entry 2)

1. `Publish(pfOneNote2007)` is refused (Implication 1).
2. Moves out of a locked section are refused (Implication 3).
3. Notebook export is package-only (Implication 8).
4. A recycle-bin parent is refused outside restore (Implication 7).
5. A page move warns that its subpages stay behind (Implication 11).
6. Partial-copy results are `outcomeUnknown` (Implications 4, 5).
7. Story 3.9 (`merge_files`) is dropped; no `MergeFiles` tool (Implication 2; E4 closed).

</frozen-after-approval>

## Code Map

- `architecture-onenote-mcp/architecture-onenote-mcp.md` -- the spine. AD-3 (l.67; timeouts l.73, modal dialogs l.103–104), AD-5 (l.137), AD-7 (l.184: steps l.189–201, deadline l.203, `HierarchyWrite` validation l.242–250, table l.271–280), AD-8 (l.282; Backups l.291, `restore_from_recycle_bin` l.318), AD-9 (l.320: Codes l.339–357, table l.359–383), AD-11 (l.412: Exports l.422–425), AD-12 (l.427), AD-14 (l.460), AD-17 (l.516; hierarchy dates l.539–541), AD-21 (l.629: What's protected l.634–638, Writes l.674–684, Copies outside OneNote l.685–688), Deferred (l.751; rows l.762, 764, 766, 767).
- `architecture-onenote-mcp/.memlog.md` -- l.286–288 are the 2026-11-16 Decisions and event. Append with `uv run _bmad/scripts/memlog.py append --path <spine folder>/.memlog.md --type decision|event --text "..."`.
- `epic-round-2-external-and-structural/spike-…-restore-findings.md` -- Results l.46–77, HRESULTs l.78–98, Implications l.154–186, Open before final l.188–199.
- `epic-round-2-external-and-structural/epic-round-2-external-and-structural.md` -- E1–E9 l.29–50; Decisions Q1–Q14 in Notes l.84–98.
- `deferred-work.md` -- l.238 (notebook re-resolved under the lock for a move), l.469 (rename onto a granted `SectionKey` refused; "epic 3's move tool needs the same check"), l.535 (backup folder counts as protected for every read check; deferred to 3.2), l.448 (shipped tool names `rename_hierarchy_object`, `reorder_hierarchy_children`), l.520 and l.532 (recycle-bin refusals already built for deletes).
- `src/OneNoteMcp.Com/HResultMap.cs` -- read only. `Triaged` l.100 (`0x80042004` → `ObjectNotFound`), l.102/l.120 (`0x80042006`, `0x80042018` → `Internal`, "a path"), l.108 (`0x8004200C` → `Internal`, retryable, "make it editable"), l.127 (`0x8004201F` → `Internal`), l.145 (`0x80042030` → `ModalDialogOpen`), l.146 (`0x80042031` → `Internal`, "choose another format").
- `src/OneNoteMcp.Core/ErrorCode.cs` -- the closed code list; this plan adds no code (J3).
- `src/OneNoteMcp.Core/Writes/HierarchyWrite.cs` l.166 -- rename and reorder already refuse a recycle-bin target with `ObjectNotFound` (`HierarchyRules.RecycleBinProblem`); rule 4 makes that the spine's rule for every hierarchy write.
- `~/.claude/skills/bmad-architecture/scripts/lint_spine.py --workspace <spine folder>` -- lint; `{pid}`/`{guid}` hits are known false positives.

## Tasks & Acceptance

**Execution:**
- [ ] `.memlog.md` -- one `decision` line per amendment, per answered Open Question (H1, H2, J1–J10), and per Implication taken; one `event` line when written.
- [ ] spine AD-11 Exports -- (rule 1) "`publish`'s format enum leaves out `pfOneNote2007`, and the gateway refuses the raw value with `NotSupported` before COM: it ended ONENOTE.EXE both times (Imp. 1, repeated)." (rule 3) "A notebook exports only as a package; every other format for a notebook is refused with `NotSupported` before COM, `userAction`: export its sections one by one (Imp. 8, repeated: `0x80042006`/`0x80042014`, nothing written)." A section group stays refused (Q7). A `Timeout` names the output path, which may hold a complete file; a retry needs a new name (`FileExists`; Imp. 14, one run). A package counts as written once its file exists and is stable across two polls, as AD-8's backup does (X2: lands 250–530 ms after return). A protected section's export needs its read grant and warns `exportNotEncrypted` (Q1).
- [ ] spine AD-7 -- `ExternalWrite` row: methods `Publish`, `OpenPackage` (drop `MergeFiles`); validation adds the format-by-scope check above, the folder created first, and `pfOneNote2007` never sent. `HierarchyWrite` row and validation: the move tool's checks (below) replace "No moves … until epic 3's move tool"; `MergeSections`; `restore_from_recycle_bin`. (rule 4) "Outside `restore_from_recycle_bin`, a hierarchy write whose target, source or new parent is the recycle bin (`isRecycleBin`), Deleted Pages (`isDeletedPages`) or anything in it (`isInRecycleBin`) is refused with `ObjectNotFound` before COM. OneNote doesn't guard it: a recycled section was renamed, a live section listed under `OneNote_RecycleBin` was soft-deleted, Deleted Pages was reordered, with no `0x8004202E` (Imp. 7, repeated)." (rule 2) "A move out of a locked section is refused with `SectionLocked` before COM (Imp. 3, one crash)"; J4 sets the other locked cases. (rule 5) "A page move leaves its subpages in the source, promoted to level 1; step 3 counts them and the result warns `subpagesLeftBehind` with the count (Imp. 11, repeated). No run lost a page, so a move takes the hierarchy snapshot of both parents only." (rule 6) "A move or merge that fails after OneNote may have copied part of it sets `outcomeUnknown`: `0x8004201F` from `MergeSections`, `0x80042004` from a cross-notebook section move (Imp. 4, 5). A step between 7 and 8, under the lock, re-reads both parents (a merge: the destination) by name, and the message reports what it found." Also: the settle (J5); the cloud-group refusal (J1); a move's notebook re-resolved under the lock (deferred l.238); a move that would land a section on a `SectionKey` holding a read grant or live `once` window is refused `InvalidArgument`, as a rename is (deferred l.469); a merge reports the source untouched and the copies under new IDs, and takes no page snapshot (Q12's condition not met, Imp. 4).
- [ ] spine AD-5, AD-14 -- the move tool as a promoted task beside the rename and reorder tools (Q2); `open_package` as a promoted task: `OpenPackage` (`ExternalWrite`) then `OpenHierarchy` of the folder (`AppState`, no grant given; Q6), returning `meta.completedSteps` on failure, waiting up to 5 s for pages and warning when none are listed (Imp. 10, repeated); tool names per J10.
- [ ] spine AD-8 -- `restore_from_recycle_bin`: by `UpdateHierarchy` of the target parent's XML listing the recycled item; a page returns under a new ID, which the result gives, and a section keeps its ID (Imp. 6, repeated); a protected section restores while locked with the write grant alone (Imp. 6; Q4); candidates returned to the model on an ambiguous name (Q3 rewording).
- [ ] spine AD-9 -- Codes and table: `0x80042030` confirmed for moves (`ModalDialogOpen`, nothing moved; Imp. 13, one run); `0x8004201F` stays `Internal` and gains `outcomeUnknown` on a merge; `0x80042004` on a move per J2; `0x80042018` on a move per J3; `0x80042006`/`0x80042014` from a notebook `Publish` refused before COM; `0x8004200C` per J6; `0x80042031` keeps its row "from the docs, unprobed" (Imp. 17); `NotSupported`, `SectionLocked`, `ObjectNotFound`, `InvalidArgument` sources widened for the new refusals.
- [ ] spine AD-3 -- modal dialogs: under File > Options, `Publish`, `OpenPackage` and `MergeSections` went through and a move returned `0x80042030` in 258 ms (Imp. 13); a move can raise OneNote's own dialog and block to the call limit (Imp. 16), which stays 30 s, `Timeout` with `outcomeUnknown`.
- [ ] spine AD-12 -- a move drops the moved page's old ID, and a merge the destination's pages, from the page-text cache.
- [ ] spine AD-17 -- a move's basis is the moved item's `lastModifiedTime`; a merge's, the source section's (5.1: a move doesn't move the node's own date).
- [ ] spine AD-21 -- What's protected: export roots and unpacked packages per H1; the backup folder for every read check (deferred l.535). Writes: moves and merges out of a protected section per H2; a section group holding a protected section counts as moving it whole (Q14). Copies outside OneNote: replace "epic 3's spine amendments (3.2) add their rule here" with the export rule (Q1 as H1 leaves it); `.one` backups stay under Q11 either way.
- [ ] spine Deferred -- resolve l.762 (move and merge semantics), l.764 (per H2), Q5/E4 (`MergeFiles` not exposed, dated Decision). Add: notebook PDF/XPS/EMF worked on cloud once (Imp. 8); hrMergeFailed's cause; the settle's length unmeasured; cloud→local section move to a root unprobed; moving a locked section unprobed; `OpenPackage` on cloud and opened-package `locked` one local run; labels unprobed.
- [ ] `deferred-work.md` -- "Resolved (rule)" for l.238, l.469's move half, l.535, naming the entry that builds each.
- [ ] lint; fix findings.
- [ ] (orchestrating session) Fable final review at `effort: low`, Phill's fixes and approval, memlog `event`, `updated`.

**Acceptance Criteria:**
- Given the amended spine, when each of the six approved rules and the 3.9 Decision is traced, then each is in the AD named above with its evidence.
- Given 3.1 Implications 1–17 and the HRESULT table, when each is traced, then it lands in an AD, a Deferred row, or a memlog line saying why not.
- Given entries 3–13, when each reads its ADs, then no "3.2 adds…" placeholder remains and no question from Open before final is unowned.
- Given the Fable review at `effort: low` has run and Phill has approved, then the spine's `updated` is set (ticket Verify).

## Open Questions

Each needs Phill's answer before this plan is approved; the answers go into the frozen block as Decisions.

**H1. Package read grants (Q1, Q11; Imp. 9).** A notebook package keeps protected sections encrypted (four runs, unlocked and locked, local and cloud), and an opened package shows them `encrypted` and `locked` with no prompt (one local run).
- a. Keep Q1 and Q11 as they are.
- b. Relax Q1 for notebook packages (making one needs no read grants), keep Q11 (every section of an opened package needs a read grant).
- c. Relax both for notebook packages: an opened package's sections follow the `encrypted` marker like any notebook; the export-root and backup-folder rule stays for everything else.
- Recommended: b. Q1's package half rested on exports being plaintext, and four runs show they aren't. Q11's open half rests on one local run, `open_package` takes packages from any path (Q6), which we can't vouch for, and unpacked packages land under an export root, where Q11 must still catch our own plaintext `.one` section exports. Q11 stays for `.one` backups whatever the answer.

**H2. Protected moves and merges (Q9, Q14; Imp. 12).** The rows confirm the leak: an unlocked protected section's pages, moved or merged into a plain section, land unencrypted (four runs). A protected section moved whole stays encrypted but changes its `SectionKey`.
- a. Keep both refusals.
- b. Keep Q9 (pages out), relax Q14: an unlocked protected section may move whole with the write grant; its read grants and journal entries are re-keyed to the new `SectionKey`.
- c. Relax both to the section's read grant.
- Recommended: a. A read grant is consent to read, not to strip the password: a `once` grant from a typed name would let the model put protected pages where anyone with the notebook, and the model later without a grant, can read them. b is safe on the evidence but buys little (a locked section is refused anyway, rule 2) and costs re-keying in entry 12. The user can still do both in OneNote.

**J1. Cloud-source section move into another notebook's group (Imp. 5, 15).** Failed `0x80042004`, nothing moved, both times; to a root it worked.
- a. Refuse before COM with `NotSupported`, `userAction`: move it to that notebook's root, then into the group.
- b. Try it and report per J2.
- c. Refuse every cross-notebook section move into a group.
- Recommended: a. Two of two failed; the two-step route uses moves that worked, and c refuses local→cloud moves that worked twice.

**J2. `0x80042004` on a move that moved nothing.**
- a. Always `outcomeUnknown` on a move, with the re-read's findings in the message.
- b. `outcomeUnknown` only when the re-read finds a copy; otherwise `ObjectNotFound`, "nothing moved".
- Recommended: a. The re-read is by name and best-effort, a cloud copy can land late, and with J1a the nothing-moved cases are mostly refused before COM.

**J3. `0x80042018` on a section-group move (one run, nothing moved).**
- a. The move pipeline turns it into `NotSupported`, naming the target, HRESULT kept, no `outcomeUnknown`.
- b. A new code `MoveFailed`.
- c. Leave `Internal`, with a move `userAction`.
- Recommended: a. No new public code for one row; `BackupFailed` set the precedent of a pipeline mapping a context's HRESULT.

**J4. Locked cases beyond rule 2.**
- a. Also refuse with `SectionLocked` before COM: a move into a locked section, moving a locked section whole, and a merge with a locked source or destination.
- b. Only rule 2.
- Recommended: a. A merge into a locked section returned `0x8004201B` three runs; the other two weren't probed; refusing costs nothing.

**J5. The settle after a section or group move that crosses storage or leaves a cloud notebook (Imp. 16, one crash, one block, one clean rerun each).**
- a. Fixed 10 s under the write lock, then a re-read; the result says so.
- b. A setting, `timeouts.moveSettleSeconds`.
- c. No settle; document the risk.
- Recommended: a. Holding the machine-wide lock delays every server's next write; a setting for a precaution nobody has measured is premature.

**J6. `update_page_content` on a recycled page returns `0x8004200C`, and the current `userAction` misleads (Imp. 7).**
- a. Write the rule now (refused before COM with `ObjectNotFound`, as `create_page` is; the triage `userAction` fixed), built by a backlog bug.
- b. Leave it for later.
- Recommended: a. It's a rule-4 neighbour, cheap, and the misleading advice is live today.

**J7. Epic 5's pending "Spine sync" entries in deferred-work (l.421–532, about 25).**
- a. Fold in only what these rules touch (shipped tool names, l.238, l.469, l.535); a separate chore syncs the rest.
- b. Fold all of them in.
- c. None.
- Recommended: a. All of them would double this story and its review; none of them would leave 3.2's text naming tools that no longer exist.

**J8. The new evidence against the approved rules (Open before final).**
- a. Package-only stands, with a Deferred row for the cloud-only PDF/XPS/EMF success; labels stay "unprobed"; no hrMergeFailed probe before entry 6.
- b. Probe notebook PDF/XPS/EMF locally before entry 7 writes its format list.
- Recommended: a. Package-only always worked; entry 7 can revisit with a probe.

**J9. Plan size (about 4,500 tokens, over the 1,600 guide; the Open Questions are a third of it and leave once answered).**
- a. Keep one story. b. Split exports and AD-21 copies from moves, merges and restore.
- Recommended: a. One spine edit, one Fable review, one approval; 5.2's plan was the same size.

**J10. Tool names (AD-14).**
- a. `move_hierarchy_object(objectId, parentId, position?, basis?)`, `merge_sections`, `restore_from_recycle_bin`, `publish`, `open_package`.
- b. Other names.
- Recommended: a. They follow epic 5's shipped `*_hierarchy_object` tools and AD-14's COM-method rule.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

- Evidence strength is the findings' own: "one run" or "repeated". A rule on one run says so in the AD text, so the Fable review and later stories can see what might move.
- Rule 6 follows `0x800706BE`'s precedent: the code stays what the map says, the pipeline adds `outcomeUnknown`.
- The end-of-story rule: the amended spine goes through the Fable final review at `effort: low` (Phill's 2026-11-16 override of the rules' `high`) before its status or `updated` is set. The review runs at build time, in the orchestrating session; this lane only drafts.
