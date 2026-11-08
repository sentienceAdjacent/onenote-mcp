# Fable review: the AD-21 amendment (once = section for 5 minutes, per-request field name)

I checked all nine findings against the spine, the code, the README and the skill, and every one holds. Three need your choice; the other six have one obvious fix.

## Need your choice

:::question
**1. How long can `read_result` page a result made under a one-read grant?**
AD-21 now says the grant lasts 5 minutes. AD-20 and the code say its stored parts are readable for as long as the result is kept: `ttlMinutes` after the original call, default 30 minutes.
- (a) Keep AD-20 as is: the 5-minute window governs new reads, and a result already made stays pageable for its stored lifetime. You approved this in AD-20 on 2026-11-06.
- (b) Cut the stored result off when the window ends. The model may lose a large result halfway through paging it.

Recommended: (a)
:::

:::question
**3. A live 5-minute window is invisible and can't be ended early.** `list_grants` doesn't show one-read grants, and `--revoke` only edits `settings.json`.
- (a) State it in AD-21: a one-read grant isn't listed and can't be revoked; it ends with its 5 minutes or when the server stops.
- (b) `list_grants` shows this server's live one-read grants with their expiry. They are still not revocable.

Recommended: (b). It's cheap and lets you see what's open.
:::

:::question
**5. The search cache holds protected page text read under a grant.** `find_pages` under a grant reads protected pages, and the page-text cache keeps their text with no expiry. It is never served after the grant ends, because ungranted candidates are dropped first, but the text stays in memory.
- (a) Never cache pages from protected sections, even when a grant covers them. Searches inside a granted section re-read those pages each time.
- (b) Cache them, and drop that section's entries when its grant ends.

Recommended: (a). It's simpler, and protected text never outlives its grant.
:::

## Fixes with one obvious answer (the last question approves them)

2. **What the dialog and docs say.** The option still says "This read only", while it now opens the section for 5 minutes. The README (lines 34 and 39) and the skill (line 80) say the same. AD-21 will bind the wording to name the window, for example **"This section, for 5 minutes"**. The story updates the option, the README, the skill and the stale `grantNotSaved` texts ("covers only this call").
4. **Other server processes.** A one-read grant is held in one server process's memory, and AD-10 prevents "a grant that applies in one session but not another". AD-21 will say that other server processes don't see it and keep asking, and AD-10 will note the exception.
6. **The field-name rule.**
   - AD-21 will say the server reads the typed name only from the field it named in that request; any other key counts as a mismatch.
   - It will say the new name gives the client nothing to pre-fill from, without claiming the fix works.
   - A Deferred row will say: confirm in the next live check that Claude Code no longer pre-fills it.
7. **The 5 minutes.** It's fixed, not a setting. It starts when your answer arrives, and a call that starts inside the window finishes.
8. **Rationale in the Rule.** The "why" sentences move out of the Rule; the memlog already holds them.
9. **Mechanical.** `updated:` becomes 2026-11-07, and the long elicitation sentence is split in two.

:::question
**Apply 2, 4, 6, 7, 8 and 9 as described?**
- Yes, apply all six
- Let me pick (note which ones)

Recommended: Yes, apply all six
:::
