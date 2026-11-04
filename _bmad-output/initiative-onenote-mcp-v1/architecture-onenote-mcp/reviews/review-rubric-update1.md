# Rubric review: spine update 1 (OneMore lessons)

- **Spine:** `architecture-onenote-mcp.md` (working copy, status `draft`)
- **Memlog:** last 7 entries, starting "Update run started 2026-11-03"
- **Source:** `epic-read-only-release/research-onemore-lessons.md` (rows 3, 4 and the xs2026 note)
- **Scope:** AD-6 "Grant key", AD-10 settings, stale rule and journal, AD-17 basis source, Deferred rows. Checked for consistency with the rest of the spine.
- **Reviewer:** rubric walker, read-only

## Verdict

**Revise before final.** The changes match the memlog decisions. The rest of the spine no longer keys anything by notebook ID. But the new key is not yet fully enforceable: different units could still compute it differently (F1). "Stale" now has two meanings (F2). And AD-8's restore path still depends on IDs that the update itself says don't survive (F3). None of these needs a new decision, only tighter wording.

## Checklist walk

| Check | Result |
| --- | --- |
| Changes faithfully reflect the memlog decisions | **Pass.** AD-6, AD-10 shape, journal `notebookPath`, AD-17 source, the removed Deferred row, the xs2026 row and AD-4 left unchanged all match decisions 1a, 2a and 3. The "trailing slash" in the memlog became "trailing separator", which is the better wording |
| Every changed AD Rule is enforceable and prevents its divergence | **Partial.** AD-17 is enforceable. AD-6/AD-10 are not, until normalization has one owner and an exact input (F1) |
| Nothing under Deferred lets two units diverge | **Pass, with a note.** xs2026: AD-4 pins xs2013 for every call, so no unit can drift. The `lastModifiedTime` row is safe for now because AD-17 pins one source. But it gives no fallback if epic 2 disproves it (F5) |
| No other AD, convention or table contradicts the change | **Partial.** Nothing left keys grants by ID. The gaps are AD-8 restore (F3), the two meanings of "stale" (F2), and the CLI/elicitation lookup by name (F4) |
| AD-7 access step | Consistent, but it doesn't say the resolution ends by reading the notebook's live `path` (folded into F1) |
| `list_grants` | Consistent, but takes on the "stale" ambiguity (F2) |
| Grant CLI | Ambiguous by name (F4) |
| AD-7 `Create` auto-grant | Key source unspecified: the argument or the live hierarchy (F1) |
| Cloud URL vs local path normalization | **Fail** (F1) |
| Section groups and sections | Risk: sections and section groups also carry a `path` attribute (F1) |

## Findings

### F1 — The grant key has no single normalizer and no exact source (should fix; the divergence this update was meant to prevent)

**Where:** AD-6 "Grant key"; AD-7 `Create` row (auto-grant); AD-10 stale rule; the `grant` CLI.

**Problem:** At least three units write or compare keys: the access check (AD-7 step 1), the `Create` auto-grant, and the CLI/elicitation. The rule says "normalized `path`: full local path or cloud URL, case-insensitive, no trailing separator", but leaves these open:
- **Which `path`.** `one:Section` and `one:SectionGroup` carry `path` attributes too (a `.one` file or a folder). A story that takes "the nearest path" in the resolution chain keys by the section.
- **The source on create.** The auto-grant could use the `OpenHierarchy` argument (possibly relative, a mapped drive, different casing, 8.3 or with a trailing `\`) or the `path` OneNote reports afterwards. These can differ, and then the new notebook's grant never matches.
- **URL forms.** For cloud URLs it doesn't say whether `%20` and a space are the same, whether the host is lower-cased, or how `/` and `\` are treated. For local paths it doesn't say whether to apply `GetFullPath`, resolve junctions or expand UNC versus mapped drives. AD-7 `ExternalWrite` does canonicalize (`GetFullPath`, junctions); the grant key says nothing.

Two stories applying different reasonable rules give a grant that silently fails to apply: the re-prompts and dead entries the memlog says this change prevents.

**Fix:** In AD-6, add:
- One Core function owns the key (e.g. `NotebookKey.From(string pathAttr)`), and every unit uses it: access, `Create`, the CLI, `list_grants` and the journal.
- Its input is always the `path` attribute of the `one:Notebook` element, read live from `GetHierarchy` (on create, after the `OpenHierarchy` call returns, never from the argument).
- Its transform is exactly: trim trailing `\` and `/`, then compare with `OrdinalIgnoreCase`. No percent-decoding, no `GetFullPath` (or state the opposite, but state it).
- AD-6 "Notebook resolution" ends by reading that attribute.

### F2 — "Stale" means two different things (should fix)

**Where:** AD-6 "If a notebook's path changes … its grant goes stale and the user is asked again". AD-10 "A grant whose path matches no open notebook is shown by `list_grants` as stale".

**Problem:** AD-10's test also matches a notebook that is merely **closed**, or a cloud notebook during its roughly 90 s refill after reopen (research row 4). A story implementing "stale → ask again", or tidying stale entries, would drop or re-prompt the grant of a notebook the user only closed. That is the lost grant the change exists to stop. The spine also never says whether stale entries are kept, pruned or replaced, or what happens to the entry when the notebook is permanently deleted.

**Fix:** In AD-10, define one status that `list_grants` reports, e.g. `active` (the key equals an open notebook's key) and `notOpen` (it doesn't). Say that entries are never removed or rewritten automatically: only `grant --revoke` removes one, and a moved notebook simply gets a new entry. Reword AD-6 to say "a moved notebook no longer matches its old grant, so the user is asked again", without using "stale".

### F3 — AD-8 restore still depends on IDs that the update says regenerate (should fix)

**Where:** AD-8 `restore_from_journal`: "a new page under the entry's `parentId`, or in a `Recovered` section of the same notebook if that parent is gone". AD-10 journal now has `notebookPath`.

**Problem:** After a close and reopen, `parentId` and `notebookId` are both dead (research row 4 flags "AD-8 journal: IDs go stale"). "The same notebook" has no defined lookup. One story could resolve it by `notebookId`, fail and report `ObjectNotFound`. Another could use `notebookPath`. The new field is recorded but nothing binds anyone to use it. The `Create` grant check for the restore ("write on the parent's notebook") has the same gap.

**Fix:** In AD-8, add: "If `parentId` is gone, the notebook is found by the F1 key of `notebookPath`, never by `notebookId`. If no open notebook matches, return `ObjectNotFound` with a `userAction` to open it. The grant check uses that notebook."

### F4 — The CLI and elicitation pick the notebook by name, but grants are now keyed by path (minor)

**Where:** AD-6 "the user types the notebook's name"; the `grant` CLI; the `name` field in AD-10.

**Problem:** Two open notebooks can share a display name, for example a local "Notebook" and a cloud "Notebook". Keying by path makes this visible: the CLI or dialog has to choose one, and the spine doesn't say how. Also, renaming a notebook's display name in OneNote doesn't change its `path`. AD-6's "(renamed or moved)" suggests it does. A story might then compare `name` and invalidate the grant.

**Fix:** Say that `name` is informational, like `lastSeenId`, and is never compared. Say that a typed name matching more than one open notebook is refused, or disambiguated by showing the paths. Change "(renamed or moved)" to "(moved, or its folder or URL renamed)".

### F5 — AD-17 "step 3's read" and the Deferred fallback (minor)

**Where:** AD-17 "Without a basis, it comes from step 3's read". The Deferred row "Which `lastModifiedTime` `UpdatePageContent` accepts".

**Problem:**
- For a `PageWrite`, step 3 may be a `GetHierarchy` read, used to find the section or check `locked`. The new rule binds only `meta`, so the no-basis value could still come from the hierarchy.
- The Deferred row says what epic 2 verifies but not what changes if the hierarchy value turns out to be the one COM accepts. That would change the public `meta` contract (AD-14) after epic 1 has shipped.

**Fix:**
- Say "from the root `lastModifiedTime` of step 3's `GetPageContent` read".
- In the Deferred row, add "if disproved, AD-17 changes the source of `dateExpectedLastModified` only. `meta.lastModifiedTime` stays as it is".

## No issue found

- AD-4 is unchanged. Keeping `xs2013` agrees with decision 3 and with the Stack table and conventions.
- The removed Deferred row ("Grant key stability …") is truly settled by AD-6.
- AD-10 still says "Keys follow AD-6's grant key", which keeps a single source of truth.
- The `sources:` frontmatter now lists the research note.
- `status: draft` is right while the update is in review.
