# Epic 2 split: Fable findings

The Fable review covered:
- the draft breakdown;
- the amended initiative and its `tickets.toml`;
- the amended epic 2 envelope and the new epic 5 envelope;
- epic 3's new note.

I checked all 14 findings against the source, and all 14 hold. F9 is right that the unit test checks `readOnlyHint`, but the stress harness checks it too (`Program.cs:94`), so its fix names both.

Fable confirmed:
- every old Done-when item lands in exactly one epic or explicitly in both;
- R4, R5 and R6 reconcile across epics 2, 5 and 3;
- the build order 1, 2, 5, 3, 4;
- the facts behind question 3b.

Following F3, I've already rebalanced question 3b, in the second file. It now offers a fifth option (encrypt every journal entry), and it says plainly what DPAPI doesn't protect against.

**Approve** approves the breakdown with the fixes you tick and your 3b answer. I then write both `tickets.toml` files and the epics' Decision lines, and commit.

## High

- **F1.** Entry 3's list of spine sections to amend for writes in protected sections is incomplete. It also needs:
  - AD-6, whose "Who can do what" says writes need only a write grant;
  - AD-9, whose `ReadNotGranted` covers only reads;
  - AD-21, whose Binds and per-tool list name only read tools;
  - AD-10, whose journal entry shape needs a protection marker.
- **F2.** Writes in protected sections leave questions no entry settles:
  - Which dialog comes first: the write grant, then the read grant?
  - Does a 5-minute `once` read grant cover a write and its read-back?
  - Does `create_page` in a protected section need the read grant? It reads only the hierarchy and takes no snapshot.
  - What does restore do when the protected section is locked or gone?

  Fix: entry 3 settles all four, and entries 9 and 11 point at them.

## Medium

- **F3.** Question 3b was one-sided. Applied: see above.
- **F4.** Entries 7 and 8 both edit the pipeline runner, with no path between them. Entry 7 adds `outcomeUnknown`, and entry 8 adds the snapshot step. Fix: setting `outcomeUnknown` moves into entry 4, which builds the runner. Entry 7 keeps the gateway-side rules.
- **F5.** The initiative's 2026-11-03 Decision still says "four epics … read-only release, safe writes, round 2, round 3". Fix: add "Amended 2026-11-09: five epics; order 1, 2, 5, 3, 4."
- **F6.** Three spine Deferred rows still send soft delete and its dates to "epic 2's soft-delete story" (spine lines 577, 581 and 590). Fix: retarget them to epic 5 in this change. They're pointers, not rules.
- **F7.** Epic 2's Notes don't record decisions 2–5, and epic 5 says nothing about decision 3, though its soft delete follows it. Fix:
  - Decision lines in epic 2's Notes;
  - a pointer in epic 5's Notes;
  - a line saying you tag each release after its sweep.
- **F8.** Creating "MCP Test Cloud" is a setup step with no owner, and entry 2 still says "if decision 4 is (a)". Fix: make it entry 2's first hitl step.

## Low

- **F9.** "The stress harness's tool check" names only one of the two checks. Fix: name both the harness check and the unit test `NavigateToToolTests.cs:114`.
- **F10.** Entries 6, 9 and 12, and epic 5's entry 7, need you in their checks but aren't marked hitl. Fix: mark them.
- **F11.** Two envelope wordings are loose:
  - Epic 5 claims "the `Create` pipeline", but AD-7 has one `Create` class. Fix: say "the hierarchy part of `Create`".
  - Epic 5's Done when 1 omits `UpdateHierarchy` add, and both R5 lines and the draft's table omit `HierarchyWrite` snapshots. Fix: add both.
- **F12.** AD-17 doesn't say what deleting one element compares. Fix: entry 10 compares the target element's hash, and entry 3 amends AD-17 to say so.
- **F13.** Wording:
  - "2.3 (the code)" should name the `InvalidArgument` error code.
  - Epic 5's References lack AD-16, which its entry 4's XSD check uses.
  - Epic 5's entry 7 doesn't say what the harness gets.
- **F14.** Entry 1 splits `ReadAccess` (551 lines of access control) but isn't rated high. Fix: rate it high and add it to the pre-merge security review.

:::question-multi
**Which of these should I fix?**
- F1. Entry 3: amend AD-6, AD-9, AD-10, AD-21 too
- F2. Entry 3 settles the four protected-write questions
- F4. outcomeUnknown moves into entry 4
- F5. Initiative: "five epics; order 1, 2, 5, 3, 4"
- F6. Retarget three spine Deferred rows to epic 5
- F7. Decision lines in epic 2 and epic 5 Notes
- F8. MCP Test Cloud as entry 2's first hitl step
- F9. Name both readOnlyHint checks
- F10. Mark entries 6, 9, 12 and 5.7 hitl
- F11. Envelope wording: Create, add, HierarchyWrite snapshots
- F12. Element delete compares its hash
- F13. Wording and references
- F14. Entry 1 high risk, security review

Recommended: all of them
:::
