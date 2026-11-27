---
title: 'One shared server for all sessions: design comparison'
type: 'chore'
ticket: '30'
created: '2026-11-27'
status: 'built'
route: 'oneshot'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
baseline_revision: 'ca2aa5917f837015a83d9557bb4c7f9d0758df59'
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Every Claude session starts its own stdio `OneNoteMcp.exe`. Backlog 30 asks whether one shared server (for example on localhost HTTP) that every session connects to is worth it, and what it would change.

**Approach:** Write a findings doc beside the ticket, `spike-one-shared-server-for-all-sessions-findings.md`, status draft, comparing per-session stdio with one shared server on safety (write lock and journal across sessions, AD-6/AD-18; grant dialogs shown to the right session, AD-21; the single STA thread, AD-1/AD-3), security (localhost access, client authentication), install and plugin config, lifecycle, and effort. Ground claims in the spine and the code; cite current external docs for MCP transports and Claude Code plugin config, and label anything unverified. End with a recommendation and the decision Phill needs to make. No code, no prototype, no OneNote probe, no memlog line.

</frozen-after-approval>

## Implementation Notes

Oneshot: the deliverable is one Markdown document and no code. Assumptions made unattended (bmad-build would have asked):

- The plan path follows `tickets.py find`'s `plan` (beside the ticket), resolved into this worktree; `find` printed paths under the session's own checkout.
- The quick review lens ran (one subagent, same model) on the doc; the Fable final review the main session runs comes after it.
- The ticket's acceptance asks for a dated memlog line; that waits for Phill's decision (task instruction), so this build stops short of it.
- Files: `spike-one-shared-server-for-all-sessions-findings.md` (new) and this plan. Surprise: the ticket's premise that each server has its own write lock is wrong (AD-18's mutex and the journal are already shared), so the doc leads with it.
- External facts come from context7 (MCP spec 2026-07-28, the MCP C# SDK docs, Claude Code docs) on 2026-11-27.

## Review Triage Log

Pass 1 (quick lens): 9 findings; high 0, medium 4, low 4, false 0, maybe-false 0, plus 1 by design.

- medium, patched: Short answer 3 implied 2026-07-28 removes `elicitation/create` for HTTP only and contradicted the stdio note. Now says it is a property of the revision, that initialize-era connections keep it, and that stdio may need MRTR too (unverified).
- medium, patched: option C's "minus the MRTR rework" rested on an unlabelled premise (the shim relays whatever revision Claude Code offers). Now conditional and marked unverified.
- medium, patched: `IssuedPathCaches` (per process, exempts issued paths from the file-path dialog) and the one-waiting-picker `quick_filing` limit were missing; added to the table, grant-dialog and STA sections, and option B's work. Verified in `IssuedPathCaches.cs` and spine AD-7 `AppState`.
- medium, patched: gap 1's fix was called backlog-sized without noting that the spine releases the lock at the deadline on purpose. Now framed as an open trade-off.
- low, patched: "machine-wide" quoted as evidence though `Local\` is per logon session; corrected.
- low, patched: `Local\` scope was labelled verified from the spine; relabelled recalled.
- low, patched: AD-12 missing from option B's amendment list; added.
- low, patched: "one log stream" and the signed `requestState` need were unlabelled inferences; labelled.
- by design, no change: the ticket's Verify needs a dated memlog line; the task defers it until Phill decides, and the doc and plan say so.

Pass 2 (Fable final review, run by the main session; Phill chose to fix all 13 findings and the editorial suggestions, 2026-11-27): all patched in the findings doc. External facts re-checked through context7 the same day (csharp-sdk MRTR and stateless docs and `StreamableHttpHandler.cs`; code.claude.com mcp, env-vars and plugins/manifest-reference).
