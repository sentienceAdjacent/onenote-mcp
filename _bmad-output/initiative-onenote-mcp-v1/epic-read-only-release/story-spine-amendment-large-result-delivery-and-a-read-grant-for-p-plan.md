---
title: 'Spine amendment: large-result delivery and a read grant for protected sections'
type: 'chore'
ticket: '16'
created: '2026-11-06'
status: 'built'
baseline_revision: 'ee2e9743fa1bff9afa638b87f783117926b7a660'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/CLAUDE.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Spike 1.15 measured two gaps.
- Claude Code saves any tool result over 50,000 characters to a file, and the model sees only a preview, or nothing at all past the token cap. That hit 11–34% of page reads on real notebooks, and every whole-notebook listing.
- Password-protected sections are fully readable as soon as the user unlocks them in OneNote, because the server guards only `locked`.

**Approach:** Amend the spine, through `bmad-architecture`'s Update path with a Fable review, with two new ADs, AD-20 (large-result delivery) and AD-21 (protected-section read grant), plus the consequent changes to AD-4, AD-6, AD-9, AD-10, AD-13 and AD-19. The memlog records the options weighed and Phill's decisions.

## Boundaries & Constraints

**Always:**
- No functionality is lost because of result size (Phill, 2026-11-06). Disk-backed results are acceptable if they're cleaned up, and XML libraries are acceptable.
- Sections with `encrypted="true"` are unreadable without an explicit grant, even when unlocked (Phill, 2026-11-06). The grant covers all nine read tools, and the model can't grant itself.
- Build the grant on AD-6's machinery properly: no settings-file allowlist (Phill, 2026-11-06).
- AD IDs stay stable. Amend in place, add AD-20 and AD-21, and renumber nothing.
- Decision (Phill, 2026-11-06), AD-20 takes the hybrid route:
  - **Mid-size results arrive whole:** each read tool declares `_meta["anthropic/maxResultSizeChars"]`. Measured: up to 380k chars of XML-like text arrived whole with it set to 500,000, and 600k still spilled.
  - **Above a configurable inline threshold** (default about 100k chars), the tool returns a structural index (outlines, tables and images, with element IDs and sizes), and the model reads the elements it wants, unchanged, through an element-scoped read. AD-4 gets a carve-out for subtree reads.
- Decision (Phill, 2026-11-06): the text block carries a one-line meta/warnings header plus the raw XML, not the whole JSON envelope. structuredContent keeps the full envelope. This changes AD-9 and the tool contract, so it needs a version bump.
- Decision (Phill, 2026-11-06): a read grant is per protected section, given by typing the section's name in the elicitation dialog or through the `OneNoteMcp grant` CLI.
- Decision (Phill, 2026-11-06): the user picks the grant's lifetime when granting. The choices are one-time (it covers only the call that asked), N hours, or until revoked.

**Never:** no code under `src/` or `tests/`. Stories 1.18 and 1.20 implement this amendment.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Small result | under the inline limit | unchanged from today | — |
| Oversized page | 862k-char page XML | the model can read every part through the tools; no single result exceeds the limit | per AD-20 |
| Protected section, no grant | read inside an `encrypted="true"` section | withheld, with a code that names the grant route | per AD-21 |
| Protected section, granted | same, after Phill grants | read normally | — |

</frozen-after-approval>

## Code Map

- `_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md` -- the spine. The highest AD is AD-19, and frontmatter `status: final`, `updated: '2026-11-05'`. Lines touched:
  - AD-4 (:90-100), where "never adds, changes or removes" XML and only search removes whole `one:Page` elements, so chunking needs a carve-out;
  - AD-6 (:111-138), where "Reads are allowed on every notebook" (:117). The elicitation dialog types the notebook name, the `OneNoteMcp grant` CLI refuses to run without a terminal, grants are keyed by the `NotebookKey` path, and "No tool can create a grant";
  - AD-9 (:186-235), whose closed code list has no read-grant or size code and whose text block repeats the whole envelope (:194);
  - AD-10 (:237-256), with the `notebooks{}` grant shape and the `Local\OneNoteMcp.Settings` mutex;
  - AD-13 (:283-290), where note content on disk is only the journal and exports;
  - AD-19 (:384-394), whose `--purge` list a new on-disk store must join.
- `.../architecture-onenote-mcp/.memlog.md` -- append-only, written with `uv run _bmad/scripts/memlog.py append --path <memlog> --type <decision|constraint|event> --text`. Past amendments kept `status: final` and logged a review event (memlog :114-125).
- `src/OneNoteMcp.Server/ToolRunner.cs:71-82` -- `ToResult` serialises the envelope once into both structuredContent and the text block. This is the single choke point for size. Reference only, not changed here.
- `src/OneNoteMcp.Xml/LockedSections.cs` and `Core/UseCases/GetHierarchyUseCase.cs:23-37` -- the pattern an `EncryptedSections` guard would copy. Reference only.
- `spikes/stress-harness/FakeServer.cs` -- `STRESS_MAX_RESULT_CHARS` sets the tool's `_meta["anthropic/maxResultSizeChars"]`, for the evidence probe.

## Tasks & Acceptance

**Execution:**
- [x] `spikes/stress-harness/FakeServer.cs` and `probe-output-cap.ps1` -- probe whether `_meta["anthropic/maxResultSizeChars"]` (documented ceiling 500,000) lifts both the 50k persist limit and the token cap for XML-like text, and record the result in the spike 1.15 findings doc -- evidence for AD-20.
- [x] The spine -- add AD-20 and AD-21 with Binds, Prevents and Rule. Amend AD-4 (the chunking carve-out), AD-6 (reads are allowed except in protected sections), AD-9 (new codes), AD-10 (read-grant storage), AD-13 and AD-19 (a result store, if any, joins purge and the README). Set `updated`.
- [x] The memlog -- the options weighed, Phill's decisions, and a review event.
- [x] The Fable review per the BMAD rule -- show confirmed findings to Phill, then apply the agreed ones.

**Acceptance Criteria:**
- Given the amended spine, when a builder reads AD-20, then it can implement story 1.18 without inventing the delivery design, the size threshold, the file lifetime or the cleanup.
- Given the amended spine, when a builder reads AD-21, then it can implement story 1.20: the grant route, scope, storage, lifetime, the codes, and the per-tool behaviour for all nine tools.
- Given the Fable review, when its confirmed findings are resolved, then Phill approves the spine and it stays `final`.

## Implementation Notes

- Probe: with `_meta["anthropic/maxResultSizeChars"]` = 500,000, XML-like text blocks of 60,693 / 101,093 / 202,093 / 383,893 chars arrived whole; about 600,700 spilled with a 2 KB preview. Recorded as an addendum in the spike 1.15 findings. `probe-output-cap.ps1` gained `-MaxResultChars`.
- Spine: AD-20 and AD-21 added; AD-4, AD-6, AD-9, AD-10, AD-12, AD-13, AD-17, AD-19 and AD-7's Read row amended (AD-7, AD-12 and AD-17 were consequent edits the plan didn't list). No ID renumbered.
- Agent-proposed choices (in-memory result store, `read_result`, 100k default threshold, hours 1-168, dialog only on target-scoped reads, `once` covering its own call's parts) approved by Phill 2026-11-06.
- Fable review: 15 findings, all confirmed and all applied with Phill's approval (2026-11-06). Ticket 18's verify and the 1.15 Recommendation 1 updated (F6).

## Plan Change Log

## Review Triage Log

Pass 1 (quick lens): high 0, medium 5, low 1, false 0, maybe-false 0, rejected 1.
| # | Finding | Verdict | Route | Evidence / action |
|---|---|---|---|---|
| 1 | AD-21 names a nonexistent `all` scope; section start at notebooks/sections/self unclassified | medium | patch | Wire enum has self/children/notebooks/sections/pages (ToolArguments.cs). Classify children/pages as target-scoped, others allowed. |
| 2 | Runs sized at index time can exceed inlineMaxChars once the part header is added | medium | patch | Header (elementHashes, meta.result) unknown at index time. Reserve header allowance; oversize run returns its members' index. |
| 3 | bodyMax circular for the index response | low | patch | Index response has no body. Size index pages on the serialized envelope JSON. |
| 4 | "parts never overlap" contradicts ref-on-parent vs child and offset reads | medium | patch | Restate as: entries within one index never overlap. |
| 5 | Hash of an Outline bigger than bodyMax is never delivered | medium | patch | No part wholly contains it. The expand index response carries the element's own hash. |
| 6 | Broad search relies on `encrypted` in FindPages/FindMeta XML, unverified | medium | patch | Only GetHierarchy evidence exists (spike 1). Detect via a GetHierarchy of the searched scope; Deferred row for 1.20. |
| 7 | Ticket 20 description contradicts amended AD-21 | medium | patch | Description says content tools drop results and parent/link tools refuse; AD-21 differs. Rewrite bullets. |
| 8 | Plan checkboxes and notes not updated | — | rejected | Fix edits this build's plan (rejected per step-04); bookkeeping updated anyway. |

## Verification

**Manual checks:**
- Every amended AD keeps its ID, and AD-20 and AD-21 each have Binds, Prevents and Rule.
- A grep of the spine and memlog for notebook names, the employer name and personal URLs finds nothing.
