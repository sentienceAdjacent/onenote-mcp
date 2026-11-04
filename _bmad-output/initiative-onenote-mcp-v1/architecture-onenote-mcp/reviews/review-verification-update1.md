# Verification review: spine update 1 (2026-11-03)

Lens: is every committed decision in the update backed by web research, the existing project, or a probe, rather than asserted from training data? Could any of it be out of date without having been checked?

Scope: AD-6 grant key, AD-10 settings and journal shape, AD-17 `lastModifiedTime` source, and the two new Deferred rows (`xs2026`; which `lastModifiedTime` `UpdatePageContent` accepts).

Sources checked:
- OneMore TechNote "OneNote ID Stability" (fetched 2026-11-03; dated Oct 5 2026, updated Oct 26 2026)
- OneMore "OneNote Defects" page (fetched 2026-11-03; last updated Sep 7 2026)
- Microsoft Learn "Application interface (OneNote)" (fetched 2026-11-03)
- `spikes/results/write.jsonl` and `focus.jsonl`, and the scripts `spikes/com-capabilities/02-write.ps1` and `05-focus.ps1`
- `spikes/host-python/.venv/.../comtypes/gen/_0EA692EE_..._0_1_1.py` (the type library comtypes generated), and `src/OneNoteMcp.Com/Interop/Enums.cs`
- The memlog entries from "Update run started 2026-11-03", and `research-onemore-lessons.md`

**Verdict:** mostly sourced, but three claims say more than the evidence shows. First, the TechNote measured that IDs change on reopen, but nobody measured that `path` stays the same. Second, nobody checked how stable `path` is when a notebook is renamed or moved, or what form it takes for cloud notebooks, and that includes the normalization rule. Third, AD-17 reads a direction ("the hierarchy lags") into a divergence seen on 2 pages. Neither of today's two probes left a script or a log, which breaks the project's own evidence practice. Two downstream docs now contradict the amended AD-17.

---

## Claim-by-claim

### AD-6: grants keyed by normalized `path`, never by ID

| Claim | Evidence | Does it support the rule as written? |
|---|---|---|
| Closing and reopening a notebook regenerates every ID, including the notebook ID | TechNote, "What changes when a notebook is closed and reopened": 3 notebooks (2 cloud, 1 local), every page, section and notebook ID changed | **Yes.** Measured, current (Oct 2026), and covers both local and cloud. This is a strong reason not to key by ID. |
| `path` is stable across close and reopen | TechNote lists what did *not* change: "Names, titles, created times, modified times, page levels". `path` isn't on that list. The TechNote's design section *recommends* keying containers "for a notebook, its path (or name as a fallback)". That's advice, not a measurement. | **Partly.** Path stability is plausible: you reopen a notebook by its path, so it's close to definitional for local notebooks. But no source measured it, and no probe here checked it. Spike 1 row 9 ("reopening an existing path returns the same ID") is `OpenHierarchy` on a notebook that was already open, not a close and reopen, so it's no evidence either way. Nobody should cite it for this. |
| Rename or move changes the path, so the grant goes stale | TechNote, "What was not measured": "Renaming a notebook, section, or page, and moving or renaming sections and section groups." No other source. | **Not verified.** It's asserted. For cloud notebooks the behaviour is genuinely uncertain. Renaming in OneNote desktop may change only the display name (`nickname`), leaving `path`, and so the grant, unchanged. That would be good, but it's unverified. Renaming or moving the folder in OneDrive or SharePoint changes the URL. Whether OneNote desktop then updates `path`, keeps the old URL and shows a sync error, or needs a reopen is unknown. The spine's parenthetical "(renamed or moved)" states as fact a behaviour no one has observed. |
| Normalization: full local path or cloud URL, case-insensitive, no trailing separator | None. No spike log records a real notebook's `path` value (I searched `spikes/results/*.jsonl` and `spike-state.json`: no cloud `path` strings are recorded). | **Not verified.** The rule was designed without seeing a real cloud `path`. Things it doesn't cover: percent-encoding (`%20` against a space), `d.docs.live.net/<cid>/` against `onedrive.live.com` or SharePoint forms, and whether OneNote ever reports the same notebook in two URL forms. Case-insensitive comparison suits NTFS and is probably right for SharePoint paths, but nothing confirms it. |
| The last-seen ID is stored for information only | Follows from the reopen finding | Yes, consistent. |

Additional issue: path reuse (outside this lens, but it comes from the same change). Under a path key, a *different* notebook that later appears at a granted path inherits the grant silently. Examples: a notebook deleted and recreated with the same name, or notebook B renamed into A's old folder. Keying by ID ruled this out by accident. Keying by path brings it back. Nothing in AD-6 or AD-10 addresses it. One possible mitigation: store `lastSeenId` together with the notebook's `name` and creation information, and re-prompt when the path matches but the stored identity signals don't. Another is to at least name this as an accepted risk under AD-6's Limits.

### AD-10: settings and journal shape

- `notebooks{<normalizedPath>: {name, lastSeenId, ...}}` and the journal's `notebookPath` follow mechanically from AD-6. No new external claim, so there's nothing more to verify. The findings on AD-6's evidence apply here too.
- Wording: "A grant whose path matches no open notebook is shown by `list_grants` as stale." This merges two states: notebook closed (the grant is still valid once it's reopened) and notebook renamed or moved (the grant is dead). The TechNote's ~90 s refill window also means a notebook may be open but partly loaded. That doesn't affect path matching, since the notebook node appears first, but it's worth checking. Suggest two labels: "not open" and "stale (unknown path)". Or leave it, and record that the two can't be told apart.

### AD-17: `meta.lastModifiedTime` comes from the page XML root, never from the hierarchy

| Claim | Evidence | Does it support the rule? |
|---|---|---|
| The page XML root `lastModifiedTime` is not the read time on this machine | Memlog probe: 12 of 205 pages, never the read time. **Independent support from spike 1:** `write.jsonl` `append-outline-withdate` passed the page XML root value (`Get-PageDate` in `02-write.ps1` reads `DocumentElement.lastModifiedTime` from `GetPageContent`) as `dateExpectedLastModified`, and it succeeded. `lmt-displayed-stale-write` and `lmt-trace-displayed-stale-write` were rejected with `0x80042010` against the same source. If the value were the read time, a fresh date would essentially never match. | **Yes, for this machine and build.** This is stronger than the update says: spike 1 already exercised the page XML value against the date check. The update doesn't cite this. |
| OneMore's report ("always returns the current time") wasn't reproduced | Defects page confirmed: Feedback Portal item dated Aug 23 2026, "diagnostic PowerShell 5 script ... Page-level XML lastModifiedTime attribute always returns the current time". The memlog's "PS5" is accurate. "Other build" is inferred, because the report gives no build number. | Accurately reported. But it's a credible contrary observation from two months ago, on an unknown build. A OneNote update could bring that behaviour to this machine, and nothing in the spine would notice. |
| "The hierarchy's value can lag" | 2 of 12 pages had a page XML value later than the hierarchy value, by weeks to months | **As an existence claim ("the two can differ"), yes**: one counterexample is enough to rule out treating them as interchangeable, which is what the rule needs. **As a directional, causal claim ("the hierarchy lags"), no.** Twelve pages, with no recorded selection method, notebook type (cloud or local) or sync state, can't show which value is the "true" one. They also can't show why the two differ: a hierarchy cache not yet refreshed? A page root that takes the latest child element's time after a sync from another device? Both fit. What matters for the write path is which value OneNote compares `dateExpectedLastModified` against on a divergent page, and the spine rightly defers that. So the rationale should say what was observed, not interpret it. |
| The rule binds `HierarchyWrite`, while saying the basis "is never the hierarchy value" | AD-17 "Binds" includes `HierarchyWrite`, `SoftDelete` and `PermanentDelete` | **Gap.** Sections, section groups and notebooks have no page XML. Their only `lastModifiedTime` *is* the hierarchy's. As written, the rule either forbids a basis for hierarchy targets or is silently scoped to pages. The page-only scope should be stated explicitly. |

### Deferred row: which `lastModifiedTime` `UpdatePageContent` accepts

- Microsoft Learn says only that the update proceeds "only if the value you pass matches the actual date and time the page was last modified". It doesn't say which representation that is. So the question is real and correctly left open.
- The row understates what's known. Spike 1 already showed the page XML value is accepted on fresh spike pages, where the two values are almost certainly equal. The open question is narrower: **on a page where the page XML and hierarchy values differ, which one avoids `0x80042010`?** That needs a divergent page, and the two found today are on real notebooks, which spike policy keeps read-only. The row should say so, and name the fixture or method (for example, a cloud page in the test notebook edited from another device or the web, then re-read).
- If the answer turns out to be "the hierarchy value", the failure mode is safe: displayed pages get spurious `StaleWrite`/`0x80042010` errors, and undisplayed pages aren't date-checked anyway (spike 1 rows 17 and 18). So the risk is availability, not data loss. It's worth saying so in the row, so it's clear why deferring is acceptable.

### Deferred row: `xs2026`

| Claim | Evidence | Supported? |
|---|---|---|
| The type library has `xs2026 = 3`, and `xsCurrent = 3` | The comtypes-generated module, lines 38-42, and `Enums.cs:18` | **Yes**, checked against this machine. |
| Undocumented | Microsoft Learn's `GetHierarchy`/`GetPageContent` parameters list "2013, 2010, 2007, or the current version". Learn recommends passing an explicit version such as `xs2013`. | **Yes.** It also supports keeping AD-4 on `xs2013`. |
| Namespace `.../onenote/2026/onenote`; hierarchy identical apart from the namespace; pages add `AuthorDef` and `Cell@colSpan`/`rowSpan` | Memlog "Probe" only | **Not reproducible.** No script, log or captured XML exists in `spikes/` or anywhere else. The sample size, and whether any page actually had merged cells, aren't recorded. |
| Linked to OneMore's Oct 2025 merged-cells request | Defects page confirmed: "Feature Request: Add Support for Merged Table Cells in OneNote Interop API XML Schema", Oct 23 2025 | The request exists. The link to `xs2026` is labelled inferred, which is correct. |
| The default `xsSchema` of `GetPageContent`/`UpdatePageContent` is `xsCurrent` (Learn), which is now 2026 on this machine | Learn's syntax blocks | Not mentioned in the row. AD-4 already passes `xs2013` everywhere, so there's no defect today. But it's the concrete reason AD-4's rule matters more than it did, and a test asserting that every gateway call passes the schema explicitly would lock it in. Optional. |

---

## Cross-document consistency (these are now out of date because of this update)

- `epic-read-only-release/epic-read-only-release.md:67` still says "`GetPageContent`'s page `lastModifiedTime` is the read time, so `meta.lastModifiedTime` must come from `GetHierarchy`. Confirm it, then amend AD-17." That is the opposite of the amended AD-17.
- `research-onemore-lessons.md` row 3 still recommends "AD-17: take `meta.lastModifiedTime` from the hierarchy", with no note that the local probe contradicts it.

A story-8 builder reading the epic would implement the rejected rule.

## Process

Spike 1 set the project's evidence standard: every probe is a script in `spikes/` and logs JSONL to `spikes/results/`. Today's two probes, `lastModifiedTime` on 12 pages and the `xs2026` comparison, exist only as memlog sentences. The AD-17 amendment and the `xs2026` Deferred row both rest on them. The scripts and outputs should be committed, read-only and with notebook names and IDs redacted as needed. The record should also say which 12 pages were picked, how, from which notebooks (cloud or local), and the two divergent values.

---

## Findings (ordered)

1. **AD-6 "Grant key": path stability is inferred, and path behaviour on rename or move is unverified.** The TechNote measured that IDs change on reopen. It didn't measure `path` (not on its "did not change" list) and explicitly didn't test renames. Cloud rename or move behaviour, and the real form of a cloud `path`, were never observed, so the normalization rule is a guess.
   *Fix:* Change "(renamed or moved)" to say this is expected but unverified. Add a Deferred row for the access story: capture real `path` values for a local and a cloud notebook; close and reopen and compare `path`; rename in OneNote (display name) and rename the folder in OneDrive, and record what `path` does. Settle the normalization rule (percent-encoding, URL forms) from those captures.
2. **AD-6/AD-10: path reuse lets a different notebook inherit a grant.** Keying by path means a notebook recreated at, or renamed into, a granted path gets write access without a prompt.
   *Fix:* Re-prompt when the path matches but the stored identity signals (`name`, `lastSeenId` together with creation information) all differ. Or list it as an accepted risk under AD-6 Limits.
3. **AD-17: the "hierarchy can lag" rationale overreads 2 of 12 pages, and the rule doesn't fit `HierarchyWrite`.** The probe shows only that the two values diverge, not which is right or why. Non-page targets have no page XML.
   *Fix:* Change the wording to "the two values can differ (probe 2026-11-03: 2 of 12 pages); the basis is taken from the page XML for consistency". Cite spike 1's `append-outline-withdate` and `lmt-*-stale-write` as evidence that the page XML value works with the date check. Scope the rule to page targets, and state that hierarchy targets use the hierarchy value. Consider a cheap guard: if a page's root `lastModifiedTime` is within a few seconds of the read time while the hierarchy value is older, log a warning, because that is OneMore's defect showing up.
4. **Deferred row "which `lastModifiedTime` `UpdatePageContent` accepts" is broader than the real unknown.** Spike 1 already shows the page XML value is accepted when the two agree.
   *Fix:* Narrow it to "on a page where the page XML and hierarchy values differ", name how to produce such a page in the test notebook, and note that a wrong answer fails safe (spurious `StaleWrite` errors on displayed pages).
5. **The probes behind AD-17 and the `xs2026` row aren't recorded, and two docs contradict the amendment.**
   *Fix:* Commit both probe scripts and their outputs under `spikes/` (redacted). Update `epic-read-only-release.md:67` and research note row 3 to match the amended AD-17.
