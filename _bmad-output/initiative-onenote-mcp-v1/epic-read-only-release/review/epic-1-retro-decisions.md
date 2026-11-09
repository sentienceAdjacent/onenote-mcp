# Epic 1 retrospective: your decisions

The retrospective is the second file in this review. Its machine verdict is **accepted-with-open-items**: all five Done-when criteria are met, but the cross-story review found real defects in the shipped release.

The one that matters is **R1**. When a search starts inside a granted protected section and its result is big enough to be stored, `read_result` keeps serving that result's page names and IDs after you end the grant. That lasts until the stored result expires, which is 30 minutes by default. It exposes no page content. The check fails open where it should fail closed.

R2, R3 and R5 are small fixes on the same seam. I'd put all four in one bug ticket.

:::question
**1. The four release defects (R1, R2, R3, R5)**
- (a) One bug ticket now, before epic 2; then release v0.8.0-rc.2
- (b) Fold them into epic 2's first story
- (c) Only R1 now; the rest go to deferred work

Recommended: (a) One bug ticket now, before epic 2; then release v0.8.0-rc.2
:::

:::question
**2. The four hands-on AD-3 scenarios** (not running, modal dialog, elevated, restart). They passed on 2026-11-04. `ComGateway` has changed since, but not in the code these scenarios exercise.
- (a) Accept the 2026-11-04 run as the evidence
- (b) Re-run them on 0.8.0-rc.1 with you at the machine (about 15 minutes; it closes and restarts OneNote, so you drive it)

Recommended: (a) Accept the 2026-11-04 run as the evidence
:::

:::question
**3. Cleanup and spec wording** (the duplication, the code-style differences, the spec and epic wording, and the epic 2 work like the purge lock)
- (a) I add them to deferred-work.md now, as an "epic 2 first refactor" group, and the spec and epic wording goes through correct-course later
- (b) Leave them in the retrospective only

Recommended: (a) I add them to deferred-work.md now, as an "epic 2 first refactor" group, and the spec and epic wording goes through correct-course later
:::

:::question
**4. The verdict**
- Accept: accepted-with-open-items
- Override (add a note)

Recommended: Accept: accepted-with-open-items
:::
