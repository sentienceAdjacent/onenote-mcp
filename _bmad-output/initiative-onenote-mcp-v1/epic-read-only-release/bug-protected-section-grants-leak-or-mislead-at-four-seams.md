---
id: 22
type: bug
title: "Protected-section grants leak or mislead at four seams in the 0.8.0-rc.1 release"
parent: epic-read-only-release
covers: [R1, R2]
after: [20]
assignee: ""
refined: true
hitl: false
risk: high
severity: P1
estimate: ""
---

# Protected-section grants leak or mislead at four seams in the 0.8.0-rc.1 release

## Description

The epic 1 retrospective found four defects where the read grant for password-protected sections (AD-21) meets older code:
- **R1:** a stored search result keeps serving a protected section's page names and IDs after the user ends the grant.
- **R2:** the plugin hook lets the model run the purge that deletes every grant.
- **R3:** a read refused because its grant ended tells the model to retry, which can never succeed.
- **R5:** a page's text stays in the search cache after its section becomes protected.

After the fix, ending a grant stops every read it allowed, only the user can purge, the error tells the model what will work, and protected text never stays cached.

## Reproduction

All against `v0.8.0-rc.1`, using MCP Test's protected section (Ballast Vault) unlocked, with OneNote's add-in access option on.

1. **R1.**
   1. Grant the Vault with `OneNoteMcp grant --read-section` and "until revoked".
   2. In settings.json, set `results.inlineMaxChars` low enough that the next result is stored rather than returned inline.
   3. Call `find_pages` with `startNodeId` set to the Vault section's ID and a query that matches its pages. The result comes back stored, with a `resultId`.
   4. Revoke the grant with `OneNoteMcp grant --revoke`.
   5. Within `results.ttlMinutes`, call `read_result` with that `resultId` and `expand: "/"`.
   - **Actual:** the Vault's page names and IDs come back.
   - **Expected:** `ReadNotGranted`.
   - The same happens when the search starts at a page in the Vault, and for any broad result stored without an index.
   - Shown by reading the code. Not run live, because Phill's add-in access option is currently off.
2. **R2.** With the plugin installed, have Claude Code run `& "<plugin>\OneNoteMcp.exe" uninstall --purge --yes`.
   - **Actual:** the PreToolUse hook allows it, and settings.json is deleted.
   - **Expected:** the hook denies it, as it denies `OneNoteMcp grant`.
3. **R3.** Follow R1 steps 1–3, but grant for 1 hour instead. After the hour, call `read_result` on the stored result.
   - **Actual:** `ReadNotGranted` with `retryable: true`. Retrying the same call fails every time, even after a new grant.
   - **Expected:** `retryable: false`, with a userAction that says to repeat the original call.
4. **R5.**
   1. With the Vault unprotected (or with any section), run `find_pages` so its pages' text is cached.
   2. Protect the section with a password.
   3. Search again.
   - **Actual:** the pages' text stays in the cache until it's evicted or the server restarts. It's never returned, because protected pages are withheld before any cache lookup.
   - **Expected:** the text is dropped from the cache.
   - This assumes a page keeps its ID when its section is protected, which hasn't been checked.

## Cause Hypothesis

- **R1:** when `read_result` re-checks a stored broad result, it checks a grant only if that grant's section is in the stored result's span tree. A search started at a section has that section as its root, whose ID isn't indexed, or has no section element at all, so the re-check finds nothing and is skipped. It fails open where AD-21 needs it to fail closed.
- **R2:** the hook's shell-command check matches only the `grant` subcommand.
- **R3:** `ReadNotGranted` is retryable for every cause, but a grant that ended by expiry stays ended for that stored result.
- **R5:** search skips the cache for protected pages without evicting what was already cached.

## Acceptance Criteria

1. **Ending a grant stops every stored result it allowed.**
   - **Given** a broad or search result stored under an `hours` or `untilRevoked` grant on a protected section, whether the search started at the section, at a page in it, or above it, and whether the result is stored with an index or without one
   - **When** the grant is revoked or expires and any part of the result is read back
   - **Then** every part that could hold that section's pages fails with `ReadNotGranted`, and when the section's place in the result can't be determined, the read fails.
2. **Only the user can purge.**
   - **Given** the plugin's PreToolUse hook
   - **When** the model runs a shell command that invokes OneNoteMcp's `uninstall --purge`, in any form the hook already recognises for `grant`
   - **Then** the hook denies it with a reason that says the user runs it.
3. **A lapsed grant isn't retryable.**
   - **Given** a stored result whose grant ended
   - **When** `read_result` refuses it
   - **Then** the error has `retryable: false`, and a userAction that says to repeat the original call after granting again.
   - Other `ReadNotGranted` errors keep their current flag.
4. **Protected page text doesn't stay cached.**
   - **Given** a page whose text is cached
   - **When** a later search finds the page in a protected section, granted or not
   - **Then** its text is no longer held in the cache.
5. **Tests cover the conditions found and fixed.** In the unit suite, a test fails on each defect and passes on the fix, for each case in criteria 1–4. The search-at-a-section, search-at-a-page and no-index cases of criterion 1 are separate tests.
6. **Or: no change is needed, with proof.** If running a reproduction on the current code shows the expected behaviour already holds, record the evidence in Notes. That supersedes the criterion for that defect.

## References

- parent — `_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/epic-read-only-release.md`
- retrospective — `_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/epic-read-only-release-retrospective.md`, Findings R1, R2, R3, R5 and Action items 1–4
- architecture — `_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md`, AD-6 (the plugin guard), AD-12 (protected pages never cached), AD-20 (stored results), AD-21 (read grants)

## Notes

- Decision: one bug ticket for all four, built before epic 2, followed by release `v0.8.0-rc.2` (Phill, 2026-11-09, retro decision 1).
- Decision: filed as epic 1 entry 22 rather than in `backlog/`, because in an initiative with epics every leaf sits under one (bmad-ticket rule). Epic 1 shipped the defects.
- Decision: severity P1, not P0 (Phill, 2026-11-09). R1 is a security exposure, but a narrow one: page names and IDs (no content) of a section the user had granted, for at most `results.ttlMinutes` (30 by default) after the grant ends, and only for a search started inside that section.
- Risk is high because this is access control. Check beyond the ticket's own tests: the stress harness on MCP Test (0 mismatches, protected sections still withheld), plus a security review of the diff before merge.
- Open question: whether a page keeps its ID when its section is password-protected (R5). The fix shouldn't depend on it.
