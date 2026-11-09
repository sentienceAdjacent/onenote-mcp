# Retro wording change: Fable findings

The Fable review covered the applied edits. I checked all 12 findings against the source, and all 12 hold.

Fable confirmed the main claims:
- The C2 evidence holds: no hierarchy use case sets a basis.
- The A2 and A3 line references are right.
- Done-when 2 lists exactly what AD-4 allows.
- No AD ID changed.
- The hierarchy-basis rule works for epic 2: every notebook, section group and section in all 10 MCP Test hierarchy fixtures carries `lastModifiedTime`.

**Approve** signs off the change proposal (the second file), together with the fixes you tick.

## Medium

- **F1.** The guide skill still says the XML passes "through unchanged" (`SKILL.md:85`). That's the same C1 wording, and the proposal missed it. Fix: reword it as in the README, and add the skill to the proposal.
- **F2.** The Boundaries text calls `navigate_to` "the whole of `NavigateTo`", but the code always passes `fNewWindow: false` (`ComGateway.cs:223`, AD-11). Fix: "all of `NavigateTo` the spine allows (`fNewWindow` stays false, AD-11)".
- **F3.** My AD-17 edit dropped the `?` marks: `{lastModifiedTime, pageHash, elementHashes}`. AD-20's index response leaves out `elementHashes` (`ResultResponses.cs:203`), and story 1.8 omits `lastModifiedTime` when it's absent. Fix: restore the optional marks. Also put the page bullet before the hierarchy bullet, as in the proposal.

## Low

- **F4.** The README says the result "says which" pages it left out, but for withheld pages it names the section (`meta.withheld`), not the pages. Fix: "the result names the pages it filtered or the sections it withheld".
- **F5.** The new AD-2 rule doesn't mention the test projects. They see the interop types through `InternalsVisibleTo` (`OneNoteMcp.Com.csproj:11-12`) for the vtable smoke test and `FakeApplication`. Fix: add one sentence.
- **F6.** README:28 and `SKILL.md:35` say `navigate_to` shows "a notebook, section or page". The tool also shows section groups. Fix: add "section group".
- **F7.** The epic Notes line says R9 "no longer owns" `NavigateTo`, but R9 still lists it, marked as shipped. Fix: say "R9 marks it shipped".
- **F8.** The proposal is stale in places:
  - its status is still draft;
  - section 5 says "after approval";
  - the option (a) texts are still there;
  - the Bookkeeping line should say that only three items needed memlog decisions.

  Fix: trim to what was chosen and add the "Fable review applied" memlog event.
- **F9.** AD-17, AD-19, AD-20 and AD-21 have no `[ADOPTED]` tag, unlike the other ADs. This predates the change. Fix: tag them.
- **F10.** AD-17 doesn't say where a caller holding only a parent ID gets its basis. `get_hierarchy_parent` returns only an ID. Fix: add "(`get_hierarchy` with scope `self` gives it)".
- **F11.** In an oversized hierarchy result, the index entries carry no `lastModifiedTime`, so the caller has to read the node from the stored result to get its basis. That's consistent but not written down. Fix: one clause in AD-17.
- **F12.** Epic 2 inherits the new hierarchy-basis rule, but its file doesn't mention it. Fix: one Notes line pointing at the memlog decision.

:::question-multi
**Which of these should I fix?**
- F1. Skill: "unchanged" wording
- F2. Boundaries: fNewWindow stays false
- F3. AD-17: restore the optional basis fields, reorder
- F4. README: names filtered pages or withheld sections
- F5. AD-2: test projects via InternalsVisibleTo
- F6. README and skill: add "section group"
- F7. Epic Notes: "R9 marks it shipped"
- F8. Proposal: trim to the chosen options, memlog event
- F9. [ADOPTED] tags on AD-17, AD-19, AD-20, AD-21
- F10. AD-17: self scope gives a parent's basis
- F11. AD-17: oversized hierarchy results
- F12. Epic 2 Notes line

Recommended: F1–F8 and F10–F12
:::

Why not F9: whether a decision counts as `[ADOPTED]` is a statement about all four ADs, and this change touches only AD-17. It belongs in a spine-wide pass, not in a wording fix.
