---
title: 'Spine amendments from the hierarchy probes'
type: 'chore'
ticket: '2'
created: '2026-11-12'
status: 'draft'
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

## Decisions already made

- Phill 2026-11-12, epic 3 Q9/Q14: moves and merges out of a protected section (a protected section moved whole included) are refused; 3.2 may relax it on 3.1's evidence.
- Phill 2026-11-12, epic 3 Q10: 5.5 reads back a soft-deleted page's recycle-bin ID into its journal entry; AD-8 and AD-10 get the field.
- Phill 2026-11-12, 2.7 Q2: AD-9 remaps `0x80042017` → `InvalidArgument`, `0x80042024` → `ComBusy`, `0x8004202E` → `ObjectNotFound`.
- Phill 2026-11-12, 2.6 Q2: a typed name matching one open notebook other than the target makes no grant (`WriteNotGranted`).
- Phill 2026-11-12, 2.7 Q1: AD-3's 30 s call timeout stays for every write, cloud creates included.

</frozen-after-approval>

## Open Questions

Each needs Phill. Options in natural order; the recommendation follows.

1. **Backup of a protected section before its permanent delete (c.17).** `Publish` writes an unlocked protected section's text to disk in the clear. (a) Refuse the permanent delete of a protected section (`NotSupported`, `userAction`: soft-delete it, then empty the recycle bin in OneNote); (b) encrypt the backup with DPAPI (CurrentUser) and delete the plaintext `Publish` output, accepting that the plaintext sat on disk unscrubbed for a moment; (c) allow it only with an explicit opt-in that says the backup is plaintext. Recommended: (a). Restore from a backup is unproven (c.7), so a backup buys little here, and (b) still leaves plaintext on disk and needs a decrypt-to-plaintext step to use. Soft delete keeps `encrypted` in the recycle bin (c.16), so the user keeps a safe route.
2. **Permanent delete of a section group (c.6).** `Publish` fails for a group in every form. (a) Refuse it; (b) back up each section in it, nested groups included, then delete the group; (c) refuse unless step 3 reads it empty (no section or group beneath), where its hierarchy snapshot is the whole record. Recommended: (c). It is honest about the backup rule and still lets an agent delete a group's sections one by one, each backed up, then the empty group. (b) is a multi-step task (AD-5) with partial-failure states for v1 to own.
3. **Notebook deletes (c.18).** COM can't delete a notebook. Proposed wording: `SoftDelete` and `PermanentDelete` of a notebook are refused before COM with `NotSupported`, `userAction`: "COM can't delete a notebook. Ask the user to close it in OneNote, then delete its folder in File Explorer or OneDrive." No `.onepkg` backup is taken. (a) This wording; (b) refuse with no `userAction` hint. Recommended: (a).
4. **The date a page delete sends (c.1).** `DeleteHierarchy` accepts the hierarchy date, `UpdatePageContent` the page XML's. (a) A page delete's basis date becomes the page's hierarchy `lastModifiedTime` (the caller reads it with `get_hierarchy`); (b) the caller's basis stays the page XML date and `pageHash` from `get_page_content`; the server compares them with step 3's `GetPageContent` read itself, then sends `DeleteHierarchy` the hierarchy date read in the same step 3. AD-17's "never replaces a caller's basis date" is amended for page deletes only. Recommended: (b). One page basis for every page write; with (a) a model holding a `get_page_content` basis gets a spurious `StaleWrite` on any cloud page whose two dates differ.
5. **Renaming a section group in a OneDrive notebook (c.5).** The rename showed locally but OneDrive kept the old name 4.5 h later (one observation; a section rename beside it synced). (a) Refuse it on notebooks whose path is a URL (`NotSupported`, `userAction`: rename it in OneNote); (b) allow it with a warning asking the user to Sync Now and check OneNote for the web; (c) allow it silently. Recommended: (a); local notebooks and section renames stay allowed. Epic 3 assumes its group moves on OneDrive follow this answer.
6. **Grants for deletes in a protected section.** AD-21 gives deletes no write-grant-alone exemption until 5.2 decides; epic 5's Notes still say "the same rule" from inception decision 3 (both grants), which 2.3 Q3c superseded for page writes. (a) The write grant alone, as for page writes (plus the permanent-delete grant for a permanent delete): a delete returns no content, its snapshot is DPAPI-encrypted, and without a read grant the model sees only the section anyway; (b) the write grant plus the section's read grant. Either way, a page soft delete in a protected section is refused until 5.5's probe (with the hierarchy date) shows the page lands somewhere `encrypted`; if it lands in the unencrypted Deleted Pages, or is deleted outright, it stays refused. Recommended: (a).
7. **Epic 5's ticket-file wording.** The findings contradict the epic's Outcome ("a backup that can bring the item back", c.7), Done when 2 and entry 4's verify (stale renames, c.2), Done when 4 and entry 6 (group, notebook, protected section), Done when 1 (notebook soft delete) and the decision-3 note. Draft wording is in Design Notes. (a) Apply it in this story's commit after Phill approves it, as 2.3 refreshed epic 2's files; (b) log it in deferred-work for `bmad-ticket`. Recommended: (a).
8. **Where backups live and how long they stay.** (a) `<journal path>\backups\`, named like journal entries, created before `Publish` (c.6), pruned with their journal entry (30 days, counted in the 2 GB cap); (b) a separate folder, never pruned automatically. Recommended: (a): bounded disk, and nothing synced to OneDrive by default. The README says a backup goes after 30 days.
9. **A permanent delete of a page already in the recycle bin** returns `0x8004200C` hrPageReadOnly and does nothing (c.9, c.13). (a) Refuse it before COM with `NotSupported`, `userAction`: empty the recycle bin in OneNote; `0x8004200C` stays `Internal` with 2.7's triage text for other causes; (b) map `0x8004200C` to `ObjectNotFound`, as 2.4 Q8 does for recycle-bin targets. Recommended: (a).
10. **AD-21's one home for plaintext copies.** Epic 3 Q1 (exports need the read grant and warn) and Q11 (anything opened from an export root or the backup folder counts as protected) are 3.2's to write "beside 5.2's backup rule". (a) 5.2 adds an AD-21 bullet "Copies outside OneNote" holding the backup rule (Q1 above), never opening a protected section's backup, and Q11's backup-folder half; 3.2 adds exports and unpacked packages there; (b) 5.2 also writes Q1 and Q11 whole now. Recommended: (a), so each epic writes what its spike backs.
11. **How weak the hierarchy basis is (c.2).** A rename doesn't move the renamed node's date, and our own write can move a date about a second late. (a) Keep the date check, call it best effort and say how weak, and state that a `StaleWrite` just after our own write means re-read and retry; (b) add a hash of the node's hierarchy element (names, child order, no dates) to the basis of hierarchy writes and deletes, which changes the read contract (AD-14). Recommended: (a) for v1.

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
- [ ] `.memlog.md` -- one `decision` line per amendment and per answered question; one `event` line when written.
- [ ] spine AD-17 -- date source by method, not target: page-content writes send the page XML date, `DeleteHierarchy` the target's hierarchy date (c.1, Q4); hierarchy dates' weakness and the late-moving date (c.2, Q11).
- [ ] spine AD-7 -- step 5 per Q4. `Create`: relative names checked before COM (no `..`, `\`, `/`, or characters a Windows file name can't hold; a section name reaches COM ending `.one`), an existing name returns its ID with the warning `alreadyExisted` and no auto-grant, the parent a live notebook or section group, a notebook folder checked empty by the server (c.3, c.16, c.18). `HierarchyWrite`: refuse an ID not already under the node it is listed under (c.4, until epic 3's move tool); left-out siblings stay; sections before groups; names checked before COM and read back, `readBackDiffers` as a warning; a notebook rename sets `nickname` (c.4, c.18); Q5; moves and merges out of a protected section refused (Q9). `SoftDelete`: where each kind goes, the recycle-bin ID read-back (Q10), groups dissolve (c.8), notebooks per Q3, recycle-bin targets refused. `PermanentDelete`: `locked` checked first (`SectionLocked`, c.16), backup folder created first, a backup counts once its file exists, Q1, Q2, Q3, Q9. Verify against the hierarchy, never files (c.10). An ID gone after a protect is re-resolved by path or name (c.15).
- [ ] spine AD-8, AD-10 -- `recycleBinId` field (Q10; stale after a reopen or protect, c.15); hierarchy entries of a protected section DPAPI-encrypted like page entries; `onenoteBackup` `userAction` names the file and promises no restore (c.7); backup location and retention (Q8).
- [ ] spine AD-9 -- the three remaps; the Codes bullets describe 2.7's triage (every code has a row, `retryable` overrides) instead of "untriaged"; `NotSupported`, `BackupFailed`, `InvalidArgument`, `SectionLocked` sources widened for the AD-7 refusals; hierarchy HRESULTs confirmed (c.13).
- [ ] spine AD-3 -- modal dialogs per hierarchy method and the two blocking dialogs (c.11); cloud creates 3.6–12.8 s, 30 s stays (c.12, 2.7 Q1); the WER rule (deferred-work l.283).
- [ ] spine AD-6 -- the 2.6 clarification; Q6; IDs change on protect, path keys survive (c.15).
- [ ] spine AD-11 -- a create can take focus (c.14). AD-14 -- `create_page` named by Phill (2.4 Q1). AD-16 -- 5.4 settles the XSD gate for `UpdateHierarchy` (c.4). AD-18 -- lock kept after a step-7 failure until the abandoned call settles or the deadline passes (deferred-work l.283).
- [ ] spine AD-21 -- Binds and Writes per Q6; the move/merge refusal (Q9); the plaintext-copies bullet per Q1 and Q10; `encrypted` survives the recycle bin and a deleted protected section locks at once, relock isn't quick (c.16); IDs change on protect (c.15).
- [ ] spine Deferred -- resolve: notebook/section-group delete, move/merge out of a protected section (for v1), `Publish` backup route, `DeleteHierarchy` date, HRESULT triage, the protected-section-deleted-whole half of "Pages deleted from a protected section". Keep open: protected page soft delete (5.5 probes it). Add: restore from a backup (c.7), OneDrive copy after a permanent delete, a stale date on a permanent delete (5.6 probes it), showing a page moving its date, create under a deleted parent, the cloud sync jam, group reorder among themselves.
- [ ] epic 5 `epic-hierarchy-writes-and-deletes.md`, `tickets.toml` -- per Q7.
- [ ] `deferred-work.md` -- "Resolved (rule)" entries for the five items taken, naming who builds them (remaps: entry 4).
- [ ] lint; fix findings.
- [ ] (orchestrating session) Fable final review, Phill's fixes and approval, memlog `event`, `updated`.

**Acceptance Criteria:**
- Given the amended spine, when each 5.1 constraint 1–18 is traced, then it lands in an AD or a Deferred row, or is recorded as no change.
- Given entries 3–7, when each reads its ADs, then no question from the findings' "Open before final" section is left unowned.
- Given the four deferred "next spine story" items, when the spine is read, then each is in it.

## Implementation Notes

## Plan Change Log

## Review Triage Log

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
</content>
</invoke>
