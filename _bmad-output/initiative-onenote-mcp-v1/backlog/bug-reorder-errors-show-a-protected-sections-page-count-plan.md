---
title: 'Reorder in a protected section needs a read grant held at the call start'
type: 'bugfix'
ticket: '7'
created: '2026-11-17'
status: 'draft'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `reorder_hierarchy_children` on a password-protected section with no read grant answers with counts ("N ID(s) aren't live children", "N child(ren) … are missing", "N page(s) would no longer follow their own parent page"). One made-up ID tells a write-grant-only caller the section's page count, the leak story 3.4 closed for `move_hierarchy_object`'s position (PR #67).

**Approach:** Phill's decision (Inbox, 2026-11-17), superseding his 2026-11-12 one (5.2 Fable N6, AD-7 "counts only"): reordering a protected section's pages without a read grant held at the call's start (stored grant or live `once` window, never prompted) is refused with `ReadNotGranted` before any list check, as 3.4 does for a position. The spine (AD-7, AD-21 and the "Who can do what" bullet) and its memlog record the change.

## Boundaries & Constraints

**Always:** The check is `ReadAccess.HoldsReadGrant(section, CallStart)`, the one `ReadMoreAsync` already makes; it never asks and never stores. Refusal order for a section target stays recycle bin → wrong kind → `SectionLocked` → this refusal → list checks. The refusal message names only the section's ID: no page ID, no count. Reordering a notebook's or section group's children is unchanged, whatever protected sections they hold. With a read grant, every current message and warning is unchanged.

**Never:** Prompt for a read grant (no `IGrantPrompt`). Change what a read grant allows, how grants are stored or matched. Touch OneNote in tests. Change step 1's empty/duplicate checks (they count the caller's own list, not the section).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| No grant, valid new order | Vault, `[V2, V1, V3]`, write grant only | Refused, no `UpdateHierarchy`, no journal entry | `ReadNotGranted`, message with Vault's ID only, `ProtectedReorderUserAction` |
| No grant, made-up / missing ID / broken subpage block / same order | Vault, `[V2,V1,V3,P1]`, `[V2]`, `[V3,V1,V2]`, `[V1,V3,V2]` | Same refusal; message identical for all four (no count, no page ID) | `ReadNotGranted` |
| No grant, locked protected section | Locked, `[V1]` | Unchanged: locked wins | `SectionLocked` |
| Read grant (stored, or live `once` window) held at call start | Vault, any list | Current behaviour, IDs named | Current messages |
| Grant only after call start | `once` window opened by the fake clock after `CallStart` | Refused | `ReadNotGranted` |
| Notebook / section group target | Notebook A reordering `[VaultId, …]`, no Vault grant | Current behaviour | None new |

</frozen-after-approval>

## Open Questions

1. **Fable review of the spine amendment.** The spine is already `final`; the Fable rule fires when a planning doc is set to final, and earlier amendments (3.2) still had one. Options: (a) a low-effort Fable review of just the amended bullets before the build is marked built; (b) none, since the decision is Phill's own and the edit only records it. Recommended: (a).

## Code Map

- `src/OneNoteMcp.Core/Writes/HierarchyWrite.cs` -- `ReorderHierarchyWrite` (~l.554-725): `ReadMoreAsync` sets `namesIds` from `HoldsReadGrant`; `ValidateChange`, `CheckPageBlocks`, `ReadBack` branch on it. Base `Validate()` (~l.150) runs recycle bin → kind → locked → `ValidateChange`. Class summary l.36-37 says "never a page ID".
- `src/OneNoteMcp.Core/Writes/MovePageWrite.cs` l.94 (`ProtectedPositionUserAction`) and l.372-380 -- the 3.4 refusal to mirror: `new OneNoteException(ErrorCode.ReadNotGranted, msg, userAction: …)`, default `retryable`.
- `src/OneNoteMcp.Server/Tools/ReorderHierarchyChildrenTool.cs` l.33 -- description says protected sections need only the write grant and give counts. `tool-contract.json` strips descriptions, so no snapshot change.
- `tests/OneNoteMcp.Tests/UpdateHierarchyToolTests.cs` -- Vault fixture l.110-154 (`V1`, `V2` level 1); `Reorder_NeverChangesAPath…` l.613 and `ReorderPages_InAProtectedSection_*` l.785-818 list Vault's pages; locked case l.944.
- `plugin/skills/guide/SKILL.md` l.60 (reorder row), l.229 (`ReadNotGranted` row); `plugin/skills/guide/references/hierarchy-writes.md` §3 (l.56-68).
- Spine `architecture-onenote-mcp.md`: l.159 ("Who can do what"), l.254 (AD-7 read-back "counts only"), l.257 (protected-sections bullet's last sentence), l.721-722 (AD-21 Writes), frontmatter `updated`. Memlog `.memlog.md` beside it.

## Tasks & Acceptance

**Execution:**
- [ ] `src/OneNoteMcp.Core/Writes/HierarchyWrite.cs` -- In `ReorderHierarchyWrite`: add `ProtectedReorderUserAction` (ask the user whether to grant the section's read grant: reading it with `get_hierarchy` scope `children` shows the dialog in a client that supports it, or they run the grant command with `--read-section`; then re-read and retry; never run the grant command yourself). At the top of `ValidateChange`, when the target is a section and no read grant was held at `CallStart`, throw `ReadNotGranted` ("Section {id} is password-protected, and reordering its pages needs a read grant for it held when the call starts, so nothing was reordered."). Rename `namesIds` to `readGranted`, drop the now-unreachable count-only branches in `ValidateChange`, `CheckPageBlocks`; update comments and the class summary l.36-37. -- the decision; dead branches would mislead.
- [ ] `src/OneNoteMcp.Server/Tools/ReorderHierarchyChildrenTool.cs` -- description: reordering a protected section's pages also needs its read grant, held at the call's start, else `ReadNotGranted`; drop "counts instead of page IDs". -- the tool is the contract.
- [ ] `tests/OneNoteMcp.Tests/UpdateHierarchyToolTests.cs` -- add a level-2 page `V3` under `V1` to Vault (fixture and `Parent`), fix the Vault lists in existing tests; replace the counts-only test with the matrix rows (theory over the five no-grant lists asserting the exact message, no `UpdateHierarchyCalls`, no entry files), a late-`once`-window case, and a granted successful reorder; keep the granted names-IDs test. -- Verify line.
- [ ] `plugin/skills/guide/SKILL.md`, `plugin/skills/guide/references/hierarchy-writes.md` -- reorder row and §3: reordering a protected section's pages needs its read grant (step 1's `get_hierarchy` read asks for it); `ReadNotGranted` row: from `reorder_hierarchy_children`, which never shows the dialog, read the section first, then retry.
- [ ] Spine -- amend l.159, l.254, l.257, l.721-722 to the new rule, citing "Phill, 2026-11-17, backlog 7, superseding 5.2 Fable N6 for reorders"; keep N6's ID-membership acceptance only where it still applies (none for page reorders now); set `updated: '2026-11-17'`.
- [ ] Memlog -- `uv run _bmad/scripts/memlog.py append --path _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/.memlog.md --type decision --by Phill --text "Backlog 7 (Phill, Inbox 2026-11-17): reordering a password-protected section's pages without a read grant held at the call's start is refused with ReadNotGranted before any list check, never prompted, as 3.4 does for a move position; supersedes the 2026-11-12 5.2 Fable N6 counts-only rule for reorders. Sections and groups unaffected."`

**Acceptance Criteria:**
- Given a protected section and no read grant at call start, when any `reorder_hierarchy_children` call targets it, then no response (error or warning) carries one of its page IDs or a count of them.
- Given the build, when `dotnet build -warnaserror` and the unit tests run, then both pass with no other test changed in meaning.

## Design Notes

Refusing up front, rather than scrubbing each count: a full `childIds` list can't be built without the page IDs, and `get_hierarchy` of the section already asks for the read grant, so an honest caller loses nothing, and every message after the guard can keep naming IDs. The refusal sits in step 4 (under the lock), after `SectionLocked`, matching 3.4's order; no pre-lock check is added.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass

## Implementation Notes

## Plan Change Log

## Review Triage Log
