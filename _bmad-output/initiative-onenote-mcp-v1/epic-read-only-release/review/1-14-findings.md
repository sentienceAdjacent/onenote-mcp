# 1.14 refactor sweep: findings to choose from

The build passes: 0 warnings, 1232 unit tests and 87 integration tests (7 skipped, as before). Two reviews ran:
- the Fable final review of the spine edits: 11 findings, 10 confirmed;
- the code review lens on the whole diff: 9 findings, 8 confirmed.

I checked each one against the source. Merged by cause, they make the 15 below.

**Dropped:**
- Fable's "the 1.20 notes never name `FindPages`": `ProtectedSectionIntegrationTests.cs:80-82` reads the raw `FindPages` XML and logs the `encrypted` attribute, so the spine row is right.
- The code review's "Fable gate still open": this review closes that gate.

Most fixes are a sentence each. Only N adds code, and it's a test.

## Spine (AD-9 and the Deferred table)

- **A (medium).** AD-7 step 7 (spine:156) and AD-9's `outcomeUnknown` sentence (:215) still name only `Timeout` and `ComUnavailable`. The new `Cancelled` row promises `outcomeUnknown` for a write, so epic 2 would build from a contradiction. Fix: add `Cancelled` to both.
- **B (medium).** The corrected SDK sentence (:204) says a *misnamed* argument fails. Only a misnamed **required** argument fails; a misnamed optional one is silently ignored (for example `start_node_id` on `get_hierarchy`). Fix: narrow the sentence to what the tests show.
- **M (medium).** The acceptance check says every epic 1 item ends up resolved, moved or closed. Three Deferred rows still name no owner:
  - replacing an Outline that has an unavailable image (:577);
  - text blocks of 383,893–500,000 characters (:585);
  - the leftover cases in "pages deleted from a protected section" (:588).

  Also, the `encrypted` row points at "epic 2's search probe", but that probe entry doesn't list the check. Fix: name an epic 2 story in each row, and add the `encrypted` check to the probe entry.
- **D (low).** The header-line row gives "617 comparisons, largest 100,000" for both 1.18 and 1.20. 1.20 ran twice (617 and 620) and only recorded "none over 100,000". The same sentence has a dangling possessive. Fix: give each run its own figures and recast the sentence.
- **E (low).** The `ComUnavailable` row and the header-line row carry rationale, which already lives in the memlog. Fix: trim both to the decision.
- **F (low).** "`0x80080005` can only come back after the integrity check *matched*": with no OneNote running the check also matches. On the opt-in silent-activation path, a failed launch would then get "Restart OneNote". Silent activation is off by default. Fix: say "passed", and note the case in the silent-activation row. No code change.
- **G (low).** The `Cancelled` row says "the client cancelled". The code knows only that the call's own token fired, and that may also happen when the session ends. Fix: "the call's own token was cancelled (a client cancel, or the session ending)".
- **H (low).** The notebook-path row says "Access story" and the section-path row says "Epic 2's access story". Fix: use one name.
- **K (low).** "optimisation" should be "optimization" (spine:589).

## Guide skill and code

- **L (medium).** The skill's new `Cancelled` row says "repeat it if you still need the result", but its general rule says a `retryable` error is retried only after the user acts. The rule names only `ComBusy` and `Timeout` as exceptions, so Claude gets two contradicting instructions. Fix: name `Cancelled` in that exception sentence.
- **I (low).** The skill's `ComUnavailable` row says "The server already retried". The `0x80080005` that now maps to `ComUnavailable` is never retried. Fix: "may already have retried".
- **J (low).** The `HResultMap.cs` comment says `0x80080005` comes from "a running but stuck OneNote", which nobody has observed. Fix: "can still come back after the integrity check passed; cause not observed".
- **N (low).** The new SDK test checks only the tool *names* `tools/list` returns, while the task asked it to match the contract snapshot. Nothing checks that the schemas survive the wire. Fix: compare the wire `tools/list` against the snapshot's tools.

## Bookkeeping

- **O (low).** `deferred-work.md` closes "a pinned release could be deleted" on the strength of a tag-protection rule that doesn't exist yet. Fix: keep it open until you add the rule. Or add the rule, and I close it with the date.

:::question-multi
**Which of these should I fix?**
- A. Add Cancelled to AD-7 step 7 and AD-9's outcomeUnknown sentence
- B. Narrow the SDK sentence to a misnamed required argument
- M. Name an epic 2 story in the three Deferred rows; add the encrypted check to the probe entry
- D. Give 1.18 and 1.20 their own figures; recast the sentence
- E. Trim the rationale from the two rows
- F. Say "passed", and note the silent-activation case in its row
- G. Reword the Cancelled row to "the call's own token"
- H. Name the access story the same way in both rows
- K. Change "optimisation" to "optimization"
- L. Name Cancelled in the skill's retry exception
- I. Change the skill's "already retried" to "may already have retried"
- J. Fix the HResultMap comment
- N. Compare the wire tools/list against the contract snapshot
- O. Keep the pinned-release item open until the rule exists

Recommended: all of them
:::

## One question on the frozen plan

The plan's test-case row "Misnamed argument" expected "the same envelope as a missing `pageId`: InvalidArgument, not Internal".
- What happens instead: no tool runs, and the SDK returns its own error, `An error occurred invoking 'get_page_content'.`, with no envelope.
- `InvalidArgument` doesn't exist as an error code.
- It is "not Internal", which was the point of the row.
- It also exposes an older gap: the model isn't told *which* argument failed, and a misnamed optional argument is silently ignored.

:::question
**How should the "Misnamed argument" row be settled?**
- (a) Accept it as met (not Internal; the SDK rejects before the tool runs), and add a deferred-work entry for epic 2: reject unknown argument names, and say which argument failed
- (b) Fix it now in 1.14: reject unknown argument names with an envelope that names the argument (new behaviour, not cleanup)

Recommended: (a) Accept it as met (not Internal; the SDK rejects before the tool runs), and add a deferred-work entry for epic 2: reject unknown argument names, and say which argument failed
:::
