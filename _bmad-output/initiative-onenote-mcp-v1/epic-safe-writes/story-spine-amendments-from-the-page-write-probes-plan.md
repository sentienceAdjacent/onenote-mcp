---
title: 'Spine amendments from the page-write probes'
type: 'chore'
ticket: '3'
created: '2026-11-10'
status: done
baseline_revision: '1bf82b7b8637fc6dee0e47b780a1212d6bf3d0b0'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/spike-write-probes-part-1-pages-paths-and-search-findings.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-safe-writes/review/epic-2-breakdown-draft.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Entries 4–11 build page writes on AD-4, AD-6–AD-10, AD-16, AD-17 and AD-21, but those rules predate spike 2.2's evidence (whole-Outline replace, OE deletes refused, ignored edits, path stability) and say nothing about writes inside password-protected sections (inception decisions 3 and 3b).

**Approach:** An architecture Update: append the decisions to the spine memlog, amend the affected ADs in place (IDs stable), resolve or retarget the Deferred rows the spike answered, run `lint_spine.py`, then the Fable final review; Phill picks the fixes and approves. Docs only.

## Boundaries & Constraints

**Always:**
- Every amendment cites its evidence: a findings constraint number, an inception decision, or a Phill answer below.
- AD IDs never renumbered; new rules go into the AD that already binds the unit.
- Spine `status: final` is kept only after Phill approves the Fable-reviewed amendment; `updated` becomes the approval date.
- No real notebook names; the OneDrive ID is `<cid>`.

**Never:**
- Any `src/` or `tests/` change (entries 4–11 build these rules).
- New error codes beyond `InvalidArgument` (constraint 13: none needed for page writes).
- Moving `ExternalWrite`, hierarchy or delete rules that epic 5 owns.

## Decisions (Phill, 2026-11-10, `review/2.3-plan-questions.md`)

- Q1: drop the cloud-image refusal; the read-back step checks each image in the sent Outlines is still there and reports a lost one as a warning.
- Q2: `ElementHash` excludes `InsertedFile/@pathCache` and unlocked `Column/@width` (no `isLocked="true"`); a concurrent width-only change goes undetected.
- Q3 (round 2, option c; supersedes inception decision 3's "both grants"): in a protected section the notebook's write grant alone lets a write run, including its own internal reads (step-3 read, read-back, snapshot), which never return content to the model. Read tools still need the section's read grant (AD-21 scope unchanged), so without one the model writes with `noBasis`.
- Q4: moot for writes under Q3 (c); the `once` rule stays as AD-21 has it for reads.
- Q5: superseded by Q3 (c): `create_page` in a protected section needs only the write grant.
- Q6: restore of a protected entry: locked → `SectionLocked` (retryable after unlock); gone or no longer protected → `ObjectNotFound`, `userAction` naming the entry file.
- Q7: exact and case-only duplicate keys alike: file `SettingsInvalid`; `grant --revoke` runs anyway and removes every entry matching the key.
- Q8: v1 `delete_page_content` deletes Outlines only; other targets → `InvalidArgument`; Deferred row for image and ink.

</frozen-after-approval>

## Code Map

- `architecture-onenote-mcp/architecture-onenote-mcp.md` -- the spine. Touch AD-3 (l.65), AD-4 (l.91), AD-6 (l.115, "who can do what", "unverified" l.141), AD-7 (l.144, PageWrite row l.167), AD-8 (l.175), AD-9 (l.191, codes l.217–248), AD-10 (l.250, journal entry l.270), AD-12 known gap (l.308), AD-16 (l.371), AD-17 (l.373), AD-21 (l.466), Deferred (l.564).
- `architecture-onenote-mcp/.memlog.md` -- append via `uv run _bmad/scripts/memlog.py append --workspace <folder> --type decision|event`. Spike constraints already at l.169–177.
- `epic-safe-writes/spike-…-findings.md` -- evidence, constraints 1–14, Evidence gaps.
- `epic-safe-writes/review/epic-2-breakdown-draft.md` l.80–116 and `review/fable-findings.md` F1, F2, F12 -- decisions 3/3b and the four protected-write questions.
- `deferred-work.md` l.37, l.43 (width, pathCache), l.181 (exact duplicate grant key), l.193 (hidden `-Embedding` after a crash) -- rules this entry decides; entries 6/7/9 build them.
- `src/OneNoteMcp.Xml/PageBasis.cs:16` -- `elementHashes` covers only Outline and OE (bears on Q8).
- `_bmad/scripts/lint_spine.py` -- deterministic spine lint.

## Tasks & Acceptance

**Execution:**
- [x] `.memlog.md` -- one `decision` line per amendment below and per answered question -- the memlog is the spine's authority.
- [x] spine AD-4 -- stays `xs2013` (constraint 12); no tool offers a schema choice (`xs2007` read crashed OneNote).
- [x] spine AD-7 -- PageWrite validate: an Outline sent must list every OE step 3 read under it, except ones the caller removes (c.1); `DeletePageContent` targets page-level objects only, an OE refused with `InvalidArgument` before COM (c.2, Q8); new read-back step between 7 and 8, under the lock: re-read, compare what was sent, report what OneNote ignored or re-created as a warning (c.7); image-refusal per Q1; in a protected section the write grant alone covers the write and its internal reads, which return no content (Q3 c).
- [x] spine AD-17 -- an edit compares the hash of every Outline it sends (whole-Outline replace would delete a sibling added since the basis); element delete compares the target's hash (F12); hash exclusions per Q2; page-XML date rule cites c.3/c.4.
- [x] spine AD-6, AD-21 -- path keys verified (c.9), "unverified" sentence removed, path forms recorded; a write in a protected section needs only the write grant and its internal reads return no content (Q3 c); AD-21 states that its read grant gates read tools only, and a write grant still never opens a section for reading; duplicate-key rule per Q7.
- [x] spine AD-8, AD-10 -- protected entries DPAPI (CurrentUser), 30 days, a protection marker (`sectionKey`) on the entry, restore only into the same protected section, locked/missing per Q6 (decision 3b).
- [x] spine AD-9 -- add `InvalidArgument` (not retryable; unknown argument names, entry 5; arguments the server refuses); a write failing `0x800706BE` sets `outcomeUnknown` (OneNote may have crashed mid-call); page-write HRESULT rows confirmed (c.13).
- [x] spine AD-3 -- reactivation must not start a new instance while a crashed `ONENOTE.EXE` is still listed (deferred-work l.193; entry 7 builds it); `ModalDialogOpen` seen only from `UpdatePageContent` (c.8).
- [x] spine AD-16 -- schema validity can't catch a partial Outline deleting OEs; AD-7's Outline check does (c.14).
- [x] spine AD-12 -- undisplayed-page date didn't advance after accepted updates (c.4).
- [x] spine Deferred -- resolve: xs2026, image refusal, page-XML date (UpdatePageContent), notebook and section path rows, FindMeta `encrypted` row. Add: editing inside a merged table in `xs2013` (entry 9), `DeletePageContent` date gaps (entry 10), page-level image/ink deletes, other modal dialogs, a newer-than-current date.
- [x] `deferred-work.md` -- append "Resolved (rule)" entries for width, pathCache, duplicate key, hidden instance, naming the owning entry.
- [x] run `lint_spine.py`; fix findings.
- [x] (Run by the orchestrating session after code review, not the implementer.) Fable final review (read-only, `bmad-review`, sources: findings, breakdown draft, fable-findings, epic file); confirmed findings to Phill as `:::question-multi`; apply chosen; memlog `event`.

**Acceptance Criteria:**
- Given the amended spine, when each findings constraint 1–14 and inception decisions 3/3b are traced, then each lands in an AD or a Deferred row, or is recorded as no change.
- Given entries 4–11, when each reads its ADs, then no protected-write question (F2) is left open.
- Given the Fable review, when Phill approves, then the spine stays `final` with `updated: <approval date>`.

## Design Notes

Whole-Outline replace changes what a basis must cover. Today an edit naming only OEs passes when the Outline moved; with replace semantics, an OE another writer added under that Outline since the caller's read would be silently deleted unless the Outline's own hash is compared. Step 4's "every OE step 3 read" check can't see that addition, because step 3 already includes it.

## Verification

**Commands:**
- `uv run _bmad/scripts/lint_spine.py <spine>` -- expected: no errors.
- leak grep over the staged diff (standing pattern list plus the CID) -- expected: only known false positives.

## Review Triage Log

Pass 1 (quick lens), 2026-11-10. Verdicts: high 0, medium 3, low 4, false 0, maybe-false 0; 1 rejected (plan edit).

| # | Finding | Verdict | Route | Evidence / action |
|---|---|---|---|---|
| 1 | Epic Notes (decision 3), `tickets.toml` entries 4, 7, 9, 11 predate Q1/Q3c and the new 2.7 crash-detection and 0x800706BE work | medium | defer | Confirmed at `epic-safe-writes.md:77`, `tickets.toml:102`. A build never writes ticket files; Phill refreshes them with `bmad-ticket`. Logged in deferred-work.md; the Fable brief names them as known-stale |
| 2 | AD-3 crash rule: "never starts a hidden instance" is false with `allowSilentActivation` on | medium | patch | Reactivation follows first-activation rules, which activate silently when the setting is on. State the exception in the spine bullet and the deferred-work entry; append a memlog correction |
| 3 | AD-9 `ObjectNotFound` retry column: "the AD-8 restore case" now ambiguous | low | patch | Two restore cases in Source; only the `notebookPath` one is fixed by opening the notebook. Direct wording fix |
| 4 | AD-17: whether supplied OE hashes are still compared, and what a missing Outline hash does, is unstated | medium | patch | "Element edits compare `elementHashes`" was removed; the partial-basis rule (no check + `partialBasis`) still applies to a missing Outline hash. Make both explicit; whether a missing Outline hash should refuse goes to Phill with the Fable findings |
| 5 | AD-7 read-back: false `readBackDiffers` from known OneNote normalisations | low | patch | Findings Results: `side-local-style-duplicate` (style equal to QuickStyleDef dropped), `side-nbsp-*`. Limit the comparison to attributes and objectIDs and exempt those normalisations |
| 6 | Deferred "`DeletePageContent` and dates" probes on MCP Test, which never had differing dates | low | patch | Findings: differing dates seen only on cloud pages. Name MCP Test Cloud |
| 7 | deferred-work width entry claims "resolved" though the locked state of the original columns is unknown | low | patch | deferred-work l.37–38 doesn't record `isLocked`. Say it resolves the unlocked case only |
| 8 | Plan's lint command path and arguments are wrong | low | rejected | Fix is to edit this build's plan. The correct command (`~/.claude/skills/bmad-architecture/scripts/lint_spine.py --workspace <folder>`) ran clean apart from the known `{pid}`/`{guid}` hits |
