---
id: 14
type: bug
title: "publish on a locked password-protected section asks for a read grant instead of saying it is locked"
parent: none
covers: [R8]
after: []
assignee: ""
refined: false
hitl: false
risk: low
severity: P3
estimate: ""
---

# publish on a locked password-protected section asks for a read grant instead of saying it is locked

## Description

Exporting a section that is password-protected and locked gives `ReadNotGranted`, not `SectionLocked`. So the agent asks the user for a read grant on a section it can't read anyway until the user unlocks it. The `userAction` also says "Exporting a page …" even when the target was a section. Publish should answer `SectionLocked` for a locked target, page or section, before the read-grant check, and word its advice for the kind of target.

## Reproduction

1. In MCP Test, the section group "Restore 3.5 Vault group" holds the password-protected section "Restore 3.5 Vault", which is locked (3.10 sitting, 2026-11-19).
2. Through onenote-dev, call `publish` on that section, as PDF, into an export root.
3. Actual: `ReadNotGranted`, with the userAction "Exporting a page from a password-protected section needs a read grant …".
4. Expected: `SectionLocked` ("unlock it in OneNote"). Nothing is written in either case.

## Cause Hypothesis

Publish runs its read-grant check (AD-21 Exports) before its lock check, so a locked protected target hits the grant refusal first. The advice text is a single constant worded for a page, and it is used for sections too.

## Acceptance Criteria

Verify:
- Unit tests show that `publish` on a locked protected section, and on a page in one, returns `SectionLocked`, with no grant prompt and nothing written.
- An unlocked protected target without a read grant still returns `ReadNotGranted`, and its `userAction` names the target's kind (page or section).
- Optionally, live: the reproduction above returns `SectionLocked`.

1. **The expected behavior holds**
   **Given** a locked password-protected section, or a page in one
   **When** `publish` is called on it
   **Then** the result is `SectionLocked`, nothing is written, and no read-grant prompt is raised
2. **The read-grant advice names the target**
   **Given** an unlocked protected section or page, and no read grant
   **When** `publish` is called on it
   **Then** the result is `ReadNotGranted`, and its `userAction` speaks of a section or a page, matching the target
3. **Tests cover the condition found and fixed**
   **Given** the unit test suite
   **When** it runs
   **Then** a test that fails on the defect and passes on the fix covers a locked protected section, and another covers a page in one
4. **Or: no change is needed, with proof**
   **Given** the reproduction
   **When** it is run on the current code
   **Then** `SectionLocked` already comes first, with the evidence recorded in Notes; this supersedes 1 to 3

## References

- parent — none
- architecture — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md, AD-21 Exports; AD-7 step 1.2 (the lock is checked before the read grant, as story 3.14 set for its writes)

## Notes

- Decision (Phill, 2026-11-19): filed as a backlog bug from the 3.10 sitting.
- The advice worded for a page is `ReadFirstUserAction`, in the publish use case.
