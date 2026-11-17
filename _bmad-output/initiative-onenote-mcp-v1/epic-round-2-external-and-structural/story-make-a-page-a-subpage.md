---
id: 14
type: story
title: "Make a page a subpage"
parent: epic-round-2-external-and-structural
covers: [E10]
after: [4]
refined: false
hitl: true
risk: medium
---

# Make a page a subpage

## Description

The user can make subpages two ways, as E10 sets: a new `set_page_level` tool indents or outdents an existing page, and `create_page` takes an optional parent page and puts the new page after that page's last subpage, one level below it. Both refuse, before COM, any level that wouldn't sit under a parent page. Phill takes part in the live sitting at the end.

What it covers:

- **`set_page_level`**, a `HierarchyWrite` through the AD-7 pipeline, like `reorder_hierarchy_children`:
  - The new level is 1, 2 or 3. The 2013 schema allows no other value (Notes).
  - The first page in a section stays at level 1. Anywhere else, a page's new level can be at most one more than the level of the page right before it, so every subpage still follows a parent page.
  - The page's own subpages move with it (Q2): each shifts by the same amount, and the change is refused if any of them would end up outside 1 to 3.
  - Every other page in the section is sent with its level as read, since OneNote flattens subpages sent without a level (5.4 sitting L6).
  - The basis check, the hierarchy journal entry for the section and the read-back follow what a page reorder does (AD-7, AD-17). A level that differs on read-back gives `readBackDiffers`.
  - Refusals before COM: a page in the recycle bin or Deleted Pages is `ObjectNotFound`; an ID that isn't a page, or a level outside 1 to 3 or with no parent page before it, is `InvalidArgument`; a locked section is `SectionLocked`; in a protected section, see Q1.
- **`create_page` with a parent page**:
  - A new optional argument names the parent page (Q5). Without it, `create_page` behaves exactly as today.
  - The parent must be a live page in the named section, and below level 3. Otherwise the call is `InvalidArgument` before COM, and no page is created.
  - After `CreateNewPage`, one `UpdateHierarchy` on the section, still under the write lock, moves the new page to just after the parent's last subpage and sets its level to the parent's level plus one. Every other page keeps its level and order.
  - If the page was created but the placement failed, see Q4.
  - Both tool descriptions say how to make a subpage.

## Acceptance Criteria

Verify: unit tests cover every level rule and refusal and the XML sent, with each page's level kept; then, with Phill, on MCP Test a page is indented to level 2 and 3 and outdented back, a new page is created as a subpage of a page that already has subpages and lands after them, the levels hold after Sync Now on MCP Test Cloud, and a level change in the locked protected section is refused as E10 says.

## Boundaries

- Must not change: `create_page` without a parent page (end of the section, level 1, the write grant alone in a protected section); `reorder_hierarchy_children` and its whole-block rule; `move_hierarchy_object`, whose moved page still lands at level 1.
- Out of scope: moving a page to another section (that is `move_hierarchy_object`); any level above 3; collapsing or expanding subpages (`isCollapsed`); changing a page's level as a side effect of any other tool.

## References

- parent — _bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/epic-round-2-external-and-structural.md, Requirement E10
- architecture — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md, AD-5 (`UpdateHierarchy` tools), AD-7 (`Create` validation for `CreateNewPage`, `HierarchyWrite` validation, Moves), AD-17, AD-21 (Writes)
- sibling — _bmad-output/initiative-onenote-mcp-v1/epic-hierarchy-writes-and-deletes/story-rename-and-reorder-update-hierarchy-plan.md, Q6 (subpage blocks) and the 2026-11-15 sitting (L6)
- sibling — story 3.4's plan, `story-move-a-page-plan.md` in this folder once 3.4 merges: the subpage handling and the security fix that makes a `position` in a protected target need a read grant
- prior art — _bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/research-onemore-lessons.md

## Notes

- Decision (Phill, 2026-11-17, Inbox): both ways, as one story: `set_page_level` for an existing page, and an option on `create_page` to create the page as a subpage of a given page, placed after that page's last subpage at the parent's level plus one.
- Checked 2026-11-17: the 2013 schema we validate against, `src/OneNoteMcp.Xml/Schema/OneNoteApplication_2013.xsd` (lines 143–150), restricts `one:Page@pageLevel` to an integer from 1 to 3. The same element also has an `isSubPage` boolean; this story never writes it. No spike has sent OneNote a level outside 1 to 3, or a level with a gap (level 3 straight after level 1); this story refuses both before COM, so it doesn't need to know.
- Checked 2026-11-17, OneMore (read, not copied): `OneNote.CreateChildPage` (OneMore/OneNote.cs) calls `CreateNewPage`, moves the new page to right after its parent in the section's hierarchy XML, sets `pageLevel` to the parent's plus one on both the hierarchy entry and the page's own XML root ("must set level on the hierarchy entry and on the page itself"), then calls `UpdateHierarchy`. It puts the child first under its parent, not last, and doesn't cap the level at 3. `SectionEditor` (OneMore/Models/SectionEditor.cs) re-levels a page placed under a parent the same way. The build adds this lesson to the research note.
- Evidence already here: the integration fixture sets a subpage's level to 2 through `UpdateHierarchy` alone and reads it back from the hierarchy (`FixtureCapture`, subpage seed), and 3.4's live test does the same. So the hierarchy route works at level 2; level 3 and the page's own XML are not checked yet.
- Unknown (the build's live sitting settles each before the rules are final): whether a level change keeps the page's ID (the search cache drops it if not); whether `GetPageContent` then shows the new level, or OneNote also needs it on the page's own XML as OneMore does; what OneNote does to a subpage block when its parent's level changes; and whether a level change syncs on MCP Test Cloud.
- Assumption: E10 maps to R8 ("structural"), since epics 2 and 5, which own R6's `UpdateHierarchy` writes, are done.
- Assumption: setting a page to the level it already has returns `ok: true` with no COM call.
- Assumption: outdenting a page can make the pages after it its subpages (P at 1, A at 2, B at 2; A goes to 1, so B now sits under A). That is allowed, and the result doesn't warn about it; the sitting checks that OneNote shows it that way.
- Assumption: the placement step of `create_page` with a parent takes the hierarchy journal entry a reorder takes, so the section's order before it is on record.
- Open question Q1, protected sections. A level change, or a new page placed under a parent, in a password-protected section:
  - (a) needs the section's read grant, held at the call's start and never prompted for, else `ReadNotGranted` before anything is read, as 3.4's fix does for a `position` in a protected target;
  - (b) needs only the write grant, as `reorder_hierarchy_children` does, with refusals that name no level or neighbour.
  - Recommended: (a). The level rules depend on the neighbouring pages, so even a bare yes or no tells a caller without a read grant how the section's pages are nested, which is the leak 3.4's fix closed for positions. `create_page` without a parent keeps needing only the write grant.
- Open question Q2, a page that has subpages changes level:
  - (a) its subpages move with it, each shifted by the same amount, refused if any would leave 1 to 3;
  - (b) only the page changes, and its subpages keep their levels.
  - Recommended: (a). It keeps the page's subtree under it, matches 5.4's rule that a subpage block moves whole, and is what we expect OneNote's own Make Subpage to do (not checked; the sitting checks it). With (b), indenting a parent makes its subpages its siblings.
- Open question Q3, the shape of `set_page_level`:
  - (a) `set_page_level(pageId, level, basis?)` with the level wanted, 1 to 3;
  - (b) `direction: indent | outdent`, one step at a time.
  - Recommended: (a). One call reaches any level, and a retry after a lost reply does nothing new instead of indenting twice.
- Open question Q4, `create_page` made the page but the placement failed (the `UpdateHierarchy` failed, or the parent was gone when the section was re-read under the lock):
  - (a) `ok: true` with `meta.pageId` and a warning that the page is at the end of the section at level 1, with a `userAction` to call `set_page_level` or `reorder_hierarchy_children`;
  - (b) an error, with the page ID in the message.
  - Recommended: (a). The page exists, and an error invites a retry that makes a second page.
- Open question Q5, how `create_page` names the parent:
  - (a) keep `sectionId` required and add an optional `parentPageId`, refused with `InvalidArgument` when that page isn't in the section;
  - (b) make `sectionId` optional when `parentPageId` is given, and take the section from the parent.
  - Recommended: (a). It only adds an argument, so the existing contract and every current caller stay as they are, and the model states the section it means.
- Open question Q6, the spine: the new tool goes into AD-5 and AD-14's tool lists, its rules into AD-7's `HierarchyWrite` validation, and Q1's answer into AD-21's Writes. Recommended: the build writes these as a spine sync, as 5.4 did, with Phill approving the rules in the plan, rather than a separate spine-amendment story with its own Fable review; no new AD is needed.
- Size: on the large side for one session: a new tool, a change to `create_page`, a spine sync and a live sitting. It stays one story as Phill decided. If the plan comes out too big, the natural split is `set_page_level` first, then `create_page` with a parent, which reuses its level rules.
- Epic 3's entry 10 (skill, README and harness) should cover both tools; recorded in the epic's Notes.
