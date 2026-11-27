---
id: 32
type: spike
title: "Do the grant dialogs work when Claude Code negotiates MCP 2026-07-28?"
parent: none
covers: [R4]
after: []
assignee: ""
refined: false
hitl: true
risk: medium
estimate: ""
---

# Do the grant dialogs work when Claude Code negotiates MCP 2026-07-28?

## Description

Claude Code now offers MCP revision 2026-07-28 to stdio servers by default, a revision that removes the server-to-client `elicitation/create` request our grant dialogs use; does our server, on the pinned ModelContextProtocol 2.3.1, still show its grant dialogs on such a connection? The current SDK docs say the legacy request stays allowed on 2026-07-28 stdio sessions, but neither 2.3.1's behaviour nor Claude Code's answer to it has been checked. Today a dialog that can't be delivered fails closed: no dialog shows and the write or protected read is refused, so a failure would look like grants that can't be given. Who waits: Phill, and every write and protected read in Claude Code sessions.

## Approach

1. Find out how to tell which revision a connection used: Claude Code's debug log, the server's own log of the negotiated version, or both; one passing run alone doesn't show it, since Claude Code can skip asking a stdio server for 7 days.
2. With Phill, on a test notebook, raise one write-grant dialog and one read-grant dialog with `MCP_PROTOCOL_NEGOTIATION=auto` and again with `legacy`, recording the revision each run used and the outcome.
3. If the dialogs fail on 2026-07-28, record what fixing it takes (an SDK upgrade, MRTR, or staying on the earlier handshake) as a follow-up for Phill to choose.

Time box: one sitting. Good enough: for each revision tried, the run log shows the revision and whether each dialog appeared and was honoured.

## Acceptance Criteria

1. **Which revision a run used can be told from its logs**
   Verify: the method is written in the findings doc beside this ticket
2. **Both dialogs are tried on each revision, with the outcome recorded**
   Verify: the findings doc's results table, one row per dialog and revision
3. **A failure has a recorded fix option and Phill's choice**
   Verify: a dated memlog line in the spine's `.memlog.md`, or a line saying no change was needed

Verify: the findings doc beside this ticket records, for each revision tried, the revision the run used and whether the write-grant and read-grant dialogs appeared and were honoured, and Phill's decision is a dated memlog line.

## References

- parent: none (standalone backlog leaf)
- `_bmad-output/initiative-onenote-mcp-v1/backlog/spike-one-shared-server-for-all-sessions-findings.md`: "The 2026-07-28 revision" and Follow-ups (spike 30, final)
- AD-6 (write grants) and AD-21 (read grants) in the spine

## Notes

- Filed 2026-11-27 from spike 30; Phill chose option A (keep one stdio server per session) and this follow-up (Inbox, 2026-11-27).
- Spike 30 suggests running this check first: no recent sitting is cited that showed a grant dialog under the newest Claude Code.
- Assumption: the sitting uses a test notebook only, and changing `MCP_PROTOCOL_NEGOTIATION` for the sitting's session is acceptable; confirm with Phill.
