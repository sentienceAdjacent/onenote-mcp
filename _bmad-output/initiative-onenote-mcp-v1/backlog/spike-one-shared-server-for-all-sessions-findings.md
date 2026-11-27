---
title: 'Findings: one shared server for all sessions'
type: 'spike-findings'
findings_for: '30'
status: 'draft'
created: '2026-11-27'
evidence: 'Desk research only: the spine (architecture-onenote-mcp.md, updated 2026-11-27), the code at ca2aa59, deferred-work.md (the 2026-11-10 shared-server note), and current external docs read through context7 on 2026-11-27 (MCP specification 2026-07-28, the MCP C# SDK docs, the Claude Code docs). No code, no prototype, no OneNote probe.'
---

# Findings: one shared server for all sessions

Backlog 30 asks whether one long-running `OneNoteMcp.exe` that every Claude session connects to (for example over localhost HTTP) is worth building instead of today's one stdio server per session, and what it would change. This is a desk comparison. Labels: **verified** means read this session in the file or doc named; **inferred** means reasoned from those sources, not observed; **unverified** means not checked and could be wrong.

## Short answer

1. **The ticket's premise is partly wrong: the write lock and the journal are already shared across sessions.** AD-18's lock is the named mutex `Local\OneNoteMcp.Write`, and it "Prevents: two writers, in one process or in two" (verified: spine AD-18; `src/OneNoteMcp.Core/Writes/WriteLock.cs`, `DefaultName`). The spine's heading calls it "machine-wide", but a `Local\` name is scoped to one Windows logon session (recalled: Windows kernel object namespaces), so it is shared by every Claude session of the signed-in user, which is what matters here. The journal is one folder of files that every process writes and prunes (AD-8, AD-10), and settings and grants are one `settings.json` behind `Local\OneNoteMcp.Settings` (AD-10). AD-10 already says "Several server processes, one per client, may run at once." So a shared server would not add one lock or one journal; it already exists.
2. **What is really per process today** is smaller: the COM reference and its STA thread (AD-3), the record of an abandoned write (AD-3, AD-18), `once` read grants (AD-21, the one exception AD-10 names), the result store (AD-20), the search page-text cache (AD-12), and each process's idle-release timer. Only two of these cause a real cross-session gap (Safety, below); both are narrow and could be closed without a shared server.
3. **Grant dialogs already go to the right session**, because each server has exactly one client. In a shared server that stops being automatic. Separately, on connections that negotiate the newest MCP revision (2026-07-28), protocol sessions and the server-to-client `elicitation/create` request are gone, replaced by multi round-trip requests (MRTR); initialize-era connections keep the old flow. A shared HTTP server would need the grant dialogs on MRTR and a new way to scope `once` grants. Whether today's stdio server is already on 2026-07-28 with Claude Code is unverified (Safety: grant dialogs), so MRTR may be needed for stdio too in time.
4. **Security gets harder, not easier.** A localhost port is reachable by every local process and by every Windows user on the machine; the spec says the server MUST validate `Origin` and SHOULD authenticate. Today's stdio pipe needs neither.
5. **Install and lifecycle get much harder.** A plugin can declare an HTTP server by URL, but nothing in a plugin starts a long-running process for it; something else must start, stop and upgrade it, and the plugin's exe would be locked while it runs.
6. **Recommendation: don't build a shared server now.** Keep per-session stdio, and if wanted, file two small fixes for the real cross-session gaps. If a shared server is ever wanted, prefer a stdio shim in front of a named-pipe daemon over localhost HTTP (Options, below).

## What is shared today and what isn't

| State | Today | Source |
|---|---|---|
| Write lock | Shared: named mutex `Local\OneNoteMcp.Write`, held AD-7 steps 2 to 8 | AD-18; `WriteLock.cs` (verified) |
| Settings and stored grants (write, permanent delete, `hours`/`untilRevoked` read grants) | Shared: one `settings.json`, read-modify-write under `Local\OneNoteMcp.Settings`, re-read at every access check | AD-10 (verified) |
| Journal and backups | Shared: one folder; each entry written once to a temporary name then renamed; each process prunes and tolerates entries vanishing | AD-8, AD-10 (verified) |
| COM reference and STA thread | Per process: one STA thread owns every COM call and serialises that process's tool calls | AD-2, AD-3; `src/OneNoteMcp.Com/StaThread.cs` (verified) |
| Abandoned write (a timed-out write still running in OneNote) | Per process: new non-read pipelines in the same process return `ComBusy`; the lock is kept "until the gateway reports none outstanding or the pipeline's deadline runs out, whichever comes first" | AD-3, AD-18 (verified) |
| `once` read grants | Per process, in memory | AD-21, AD-10 ("holds only in the process that asked"); `OnceWindows.cs` ("This process's once windows") (verified) |
| Result store (`read_result`) | Per process, in memory | AD-20 (verified) |
| Issued attachment paths (`pathCache` values exempt from the file-path dialog) | Per process, in memory | AD-7 `PageWrite`; `IssuedPathCaches.cs` (verified) |
| `quick_filing` one-waiting-picker limit | Per process | AD-7 `AppState` (verified) |
| Search page-text cache | Per process, in memory; another server's write can leave it stale (a stated known gap) | AD-12 (verified) |
| Idle release | Per process: each server drops its COM reference 5 s after its last call | AD-3 (verified) |
| Memory | 17.9, 20.4, 48.4 and 54.5 MB working set for four servers, idle CPU near zero | `deferred-work.md`, 2026-11-10 note (measured then, not re-measured) |

## Comparison

### Safety: write lock and journal (AD-6, AD-8, AD-18)

- **Per-session stdio:** one lock and one journal for all sessions already (table above). Two real gaps remain, both inferred from the spine (two smaller per-process items, `IssuedPathCaches` and the one-picker limit, matter only to a shared server and are under the next two headings):
  1. *An abandoned write is invisible to other processes after the deadline.* If session A's write times out, A keeps the mutex until its call returns or the pipeline's deadline runs out (AD-18). After the deadline, session B can take the lock and write while A's call may still be changing OneNote. Inside one process the `ComBusy` check after step 2 catches this; across processes nothing does.
  2. *Another server's writes can leave the search cache stale* (AD-12's known gap). `search.cacheMaxMB: 0` avoids it, and the README says so.
- **Shared server:** both gaps close, because there is one gateway and one cache. Nothing else in this area gets safer: the lock and journal are already shared.
- **Narrow alternative (inferred, not designed):** gap 1 could be closed by recording an outstanding abandoned write somewhere every process checks, for example a marker file or named object that a process's `ComBusy` check after step 2 also reads. It is an open trade-off, not a free fix: the spine releases the lock at the deadline on purpose, so a call that never returns can't block writes forever, and a cross-process marker needs the same escape (an age limit, or clearing when the owning process exits). Gap 2 is already documented with a setting that avoids it. Each looks backlog-sized, but that is an estimate, not a design.

### Safety: grant dialogs reach the right session (AD-6, AD-7, AD-21)

- **Per-session stdio:** correct by construction, as long as the connection supports `elicitation/create` (see the note at the end of this heading). `ElicitationGrantPrompt` calls `ElicitAsync` on the `McpServer` of the call, and a stdio server has one client (verified: `src/OneNoteMcp.Server/Grants/ElicitationGrantPrompt.cs`). A `once` grant holds only in that process, so only in that session. So do the attachment paths a read issued: `IssuedPathCaches` exempts a `pathCache` value the server itself returned from AD-7's file-path dialog, "in memory only, for this server's lifetime" (verified: `src/OneNoteMcp.Core/Writes/IssuedPathCaches.cs`, registered as a singleton in `ServerSetup.cs`).
- **Shared server, initialize-era stateful HTTP:** the C# SDK gives each HTTP session its own `McpServer`, so a dialog raised in a tool call goes back to the session that made the call (inferred from the SDK docs on sessions and `ConfigureSessionOptions`; unverified in code). `once` grants would have to be keyed by session, or one session's grant would open the section to every session for its window, which weakens AD-21. The same holds for `IssuedPathCaches`: shared, a path issued to one session's read would skip the file-path dialog for another session's write.
- **Shared server, MCP 2026-07-28:** the revision removes `Mcp-Session-Id` and protocol sessions, and removes the server-to-client `elicitation/create` request; a tool asks for input by returning an `input_required` result, and the client retries the call with `inputResponses` and the server's `requestState` (verified: MCP spec 2026-07-28, changelog and `tools/call`). In the C# SDK, `ElicitAsync` throws "Elicitation is not supported in stateless mode" for every Streamable HTTP request served under 2026-07-28; the replacement is throwing `InputRequiredException` (verified: csharp-sdk docs, elicitation and MRTR pages). Consequences (inferred):
  - Every grant dialog (write grant, read grant, permanent-delete confirmation, file-path acceptance, notebook location) must be rebuilt as a two-round call, and the pipeline must resume from `requestState`. AD-7 step 1's "raise the dialog, then continue" becomes "return, then re-enter step 1".
  - `requestState` comes back from the client, so it would need integrity protection (signed and bound to the call's target), or a forged retry could claim a grant answer for a different target (inferred; the spec says only that the client echoes it).
  - With no session, a `once` grant has nothing to be scoped to except a server-minted handle passed as a tool argument, which the spec suggests for cross-call state (verified: spec changelog, SEP-2567). That is a contract change (AD-14).
- **Note that applies either way:** the removal of `elicitation/create` is a property of the 2026-07-28 revision, not of HTTP (verified: spec changelog). Claude Code now offers 2026-07-28 to local stdio servers by default and uses it with those that support it, with `MCP_PROTOCOL_NEGOTIATION=legacy` as the opt-out (verified: Claude Code changelog, env-vars and "MCP client runtimes" docs). The C# SDK says stdio servers and initialize-handshake sessions keep the `elicitation/create` flow (verified: csharp-sdk elicitation docs); read with the spec, that means connections that stayed on the earlier handshake (inferred). Whether SDK 2.3.1, which we pin (`Directory.Packages.props`), answers Claude Code's 2026-07-28 probe, and so whether our dialogs still work under the newest Claude Code, is **unverified**. That deserves its own small check regardless of this decision.

### Safety: the single STA thread (AD-1, AD-2, AD-3)

- **Per-session stdio:** one STA per process; a hung call poisons only that session's gateway (AD-3), and the other sessions keep working.
- **Shared server:** one STA for every session. Consequences (inferred from AD-3):
  - One session's 300 s `Publish`, or any hung call, queues every other session's reads behind it; a timeout poisons the thread and fails every queued call with `ComBusy`, in every session. After two abandoned threads the gateway returns `ComUnavailable` to everyone.
  - One crash ends every session's OneNote tools at once.
  - OneNote is an out-of-process COM server, so it may already serialise calls from several clients; then per-session processes buy less parallelism than they appear to (inferred, unprobed).
- **One picker at a time:** AD-7 `AppState` makes a second `quick_filing` call `ComBusy` while "one of this process's" calls waits for the user (verified: spine AD-7 `AppState`). Across stdio servers each session has its own allowance; in a shared server one session's open picker would block every session's `quick_filing` for up to 300 s unless the rule were re-scoped (inferred).
- **Benefit:** one COM client instead of N. Backlog bug 3 ("cleaning up" after a restart) showed that a held reference keeps `ONENOTE.EXE` alive; N servers mean N chances to hold one. The 5 s idle release (AD-3, backlog 3) already narrows this, and a sitting has not yet checked it (AD-3, Deferred).

### Security: localhost access and client authentication

- **Per-session stdio:** the client starts the server and owns its pipes; no other process can talk to it. The threat model in AD-6's Limits ("accidents and casual misuse, not a hostile local process") holds as written.
- **Localhost HTTP:**
  - The spec requires: validate `Origin` on every connection and answer 403 when it is present and invalid (DNS rebinding); bind to 127.0.0.1, not 0.0.0.0; and "SHOULD implement proper authentication for all connections" (verified: MCP spec 2026-07-28, Streamable HTTP, Security). Tokens go in `Authorization: Bearer`, never in the URL (verified: spec, Authorization).
  - A TCP port on 127.0.0.1 is reachable from every Windows logon session on the machine, not only the user's (recalled: standard Windows networking). Our named mutexes are `Local\` (verified: spine Consistency Conventions), which Windows scopes to one logon session (recalled), and the journal's DPAPI protection is per user (verified: AD-8). A shared HTTP server without authentication would let another local user, or any local process, read and write the user's notebooks as the user. So a token is mandatory, not optional.
  - Where the token lives: Claude Code expands `${VAR}` in a server's `url` and `headers`, and supports `headersHelper`, a command run at each connect that prints headers (verified: Claude Code MCP docs). So the plugin could run `OneNoteMcp.exe token` as a headers helper that reads a per-user secret (for example DPAPI-protected under `%LOCALAPPDATA%`). Whether a plugin's `.mcp.json` accepts `headersHelper` and expands `${CLAUDE_PLUGIN_ROOT}` in it is **unverified**.
  - Integrity level: AD-3 refuses a server whose integrity differs from OneNote's. A daemon started from an elevated session would refuse every call for every session; an elevated client could still reach a normal daemon over HTTP (inferred).
- **Named pipe (only with a shim, below):** a pipe can carry a DACL limited to the current user, so no token is needed and other logon sessions are kept out (inferred; Windows named-pipe security, not checked against our setup).

### Install and plugin config

- **Today:** `plugin/.mcp.json` declares `desktop` as `"type": "stdio"`, `"command": "${CLAUDE_PLUGIN_ROOT}/OneNoteMcp.exe"` (verified). Claude Code starts it at session start and stops it at session end (verified: Claude Code docs, plugin MCP servers, "Automatic lifecycle").
- **HTTP:** a plugin's `.mcp.json` can declare `"type": "http"` with a `url` and `headers` (verified: Claude Code plugin and MCP docs). But Claude Code then only connects; it starts nothing. Something else must start the daemon: a `SessionStart` hook can start a background program (verified: Claude Code hooks docs), but every session would race to start it, the hook needs a single-instance guard, and a session whose hook failed connects to nothing. A login task or tray app is a second install step outside the plugin.
- **Upgrades:** the daemon runs the exe from the plugin folder, so a plugin update meets a locked exe, or the old daemon keeps serving a newer skill and tool contract (inferred; CLAUDE.md already notes that a running exe is locked). A version handshake between plugin and daemon would be needed (AD-14).
- **Port:** a fixed port can collide; a dynamic one needs a discovery file the `url` can't read without a helper (inferred).

### Lifecycle: who starts and stops it

- **Today:** Claude Code. One process per session, ended with the session. `uninstall --purge` warns about other running servers (verified: `Program.cs` comment and AD-19).
- **Shared:** the daemon needs its own start (first session or login), single-instance guard, idle exit when no session is connected, crash restart, and a clean stop for upgrades and `uninstall`. Logs would no longer go to a session's stderr (AD-13 assumes stdio); only the rolling file log would remain (today each process already keeps its own rolling file, `src/OneNoteMcp.Server/Logging/FileLog.cs`). Whether Claude Code ends child processes started by a hook or a server when the session ends (for example through a Windows job object) is **unverified**, and it decides whether a daemon started from a session survives it.

### Effort

| Option | What it takes | Size (inferred) |
|---|---|---|
| A. Keep per-session stdio | Nothing; optionally two backlog items: cross-process abandoned-write visibility (Safety gap 1) and a check that our elicitation works under Claude Code's 2026-07-28 negotiation | 0 to 2 backlog stories |
| B. Shared localhost HTTP daemon | A spike (lifecycle under Claude Code, job objects, which protocol revision Claude Code uses with it, many-client COM behaviour); spine amendments to AD-1, AD-3, AD-6, AD-7, AD-10, AD-12 (its known gap names "another server process"), AD-13, AD-14, AD-15, AD-18, AD-19, AD-20 and AD-21; `ModelContextProtocol.AspNetCore` and Kestrel (a new dependency); Origin check and token auth; MRTR rework of every dialog with signed `requestState`; per-session or handle-scoped `once` grants, issued paths and result IDs; a re-scoped one-picker rule; daemon start, single instance, idle exit, upgrade and uninstall; README and skill; tests | An epic, roughly 8 to 12 stories |
| C. Stdio shim plus named-pipe daemon | As B, minus HTTP, Origin and tokens (the pipe is user-only) and, if the daemon can keep each shim connection on the earlier handshake, minus the MRTR rework (each shim connection is then one stateful MCP session, so dialogs, `once` grants and issued paths stay per session; unverified: the shim relays whatever revision Claude Code offers, so the daemon would have to decline 2026-07-28 or use MRTR anyway), plus a shim mode in the exe that relays stdio to the pipe and starts the daemon if none runs. Plugin config stays stdio | An epic, roughly 6 to 9 stories |

Option C is the better shape if a shared server is ever wanted: the plugin keeps `"type": "stdio"`, Claude Code keeps starting and stopping something per session, the pipe's DACL replaces a token, and each shim is a natural session for dialogs and `once` grants. It still carries B's blast radius and upgrade problems, and depends on two unverified things: the daemon outliving the shim that started it, and the SDK keeping `elicitation/create` on a named-pipe stream when Claude Code offers 2026-07-28.

## Benefits a shared server would bring

- One COM client: one STA, one idle release, fewer references holding OneNote open (backlog bug 3).
- Closes the two cross-session gaps above.
- Less memory: about 18 to 55 MB per extra session today (measured 2026-11-10, four servers).
- One log stream instead of one rolling file per process (inferred benefit; today's per-process files work).

None of these is a correctness problem today that per-session stdio can't fix more cheaply, apart from Safety gap 1, which a backlog story can close.

## Recommendation

Keep one stdio server per session and don't plan a shared-server epic now: the lock, journal and grants it was meant to unify are already shared, while a shared server adds an authentication surface, its own lifecycle and upgrade path, one failure point for every session, and, over HTTP under MCP 2026-07-28, a rebuild of every grant dialog. Instead, file a backlog story to make an abandoned write visible to every server process, and a small check that our grant dialogs still work now that Claude Code negotiates MCP 2026-07-28 with stdio servers by default; revisit a shared server (as option C) only if many concurrent sessions become normal or a probe shows OneNote misbehaving with several COM clients.

## Decision for Phill

:::question
Should a shared server be planned as a future epic?
- **A. No: keep per-session stdio**, and file the two small follow-ups (cross-process abandoned-write visibility; check elicitation under MCP 2026-07-28).
- **B. Yes, as a localhost HTTP daemon** (epic: auth token, Origin check, MRTR dialogs, daemon lifecycle).
- **C. Yes, as a stdio shim plus a named-pipe daemon** (epic: plugin stays stdio, user-only pipe, per-shim sessions).
- **D. Not now, park it** with no follow-ups; revisit at the next initiative.

Recommended: A
:::

Whatever is chosen goes in the spine's memlog as a dated line (the ticket's Verify), after this doc's Fable final review.

## Not checked

- Whether ModelContextProtocol 2.3.1 negotiates 2026-07-28 with Claude Code, and whether our `ElicitAsync` dialogs work on such a connection (this also decides how much MRTR work option B or C adds).
- Whether a plugin `.mcp.json` HTTP entry accepts `headersHelper` and `${CLAUDE_PLUGIN_ROOT}` in it.
- Whether Claude Code ends processes started by a session's hooks or servers when the session ends.
- How OneNote schedules COM calls from several clients at once, and which `ONENOTE.EXE` several clients bind to (AD-3 Deferred).
- Named-pipe DACL behaviour between an elevated and a normal process of the same user.

## Sources

- Spine: `_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md`: AD-1, AD-2, AD-3, AD-6, AD-7, AD-8, AD-10, AD-12, AD-13, AD-18, AD-20, AD-21, Consistency Conventions, Structural Seed, Deferred.
- Code: `src/OneNoteMcp.Server/Program.cs` (`WithStdioServerTransport`), `src/OneNoteMcp.Server/ServerSetup.cs` (per-process singletons), `src/OneNoteMcp.Core/Writes/IssuedPathCaches.cs`, `src/OneNoteMcp.Server/Logging/FileLog.cs`, `src/OneNoteMcp.Core/Writes/WriteLock.cs`, `src/OneNoteMcp.Server/Grants/ElicitationGrantPrompt.cs`, `src/OneNoteMcp.Com/StaThread.cs`, `src/OneNoteMcp.Core/Access/OnceWindows.cs`, `plugin/.mcp.json`, `plugin/hooks/hooks.json`, `Directory.Packages.props`.
- `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md`: the 2026-11-10 shared-server note and memory measurements; `deferred-work-triage-2026-11-27.md` item D63; backlog bug 3 (`bug-onenote-shows-cleaning-up-after-a-restart-while-a-server-is-connected.md`).
- MCP specification 2026-07-28: https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http (Security), https://modelcontextprotocol.io/specification/2026-07-28/changelog (sessions removed, SEP-2567), https://modelcontextprotocol.io/specification/2026-07-28/basic (statelessness), https://modelcontextprotocol.io/specification/2026-07-28/server/tools (`input_required`), https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization.
- MCP C# SDK docs: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/stateless/stateless.md, .../docs/concepts/elicitation/elicitation.md, .../docs/concepts/mrtr/mrtr.md, .../docs/concepts/transports/transports.md.
- Claude Code docs: https://code.claude.com/docs/en/mcp (HTTP servers, env expansion, `headersHelper`, elicitation, client runtimes, plugin servers' lifecycle), https://code.claude.com/docs/en/plugins/components, https://code.claude.com/docs/en/hooks (SessionStart, background processes), https://code.claude.com/docs/en/env-vars (`MCP_PROTOCOL_NEGOTIATION`), https://code.claude.com/docs/en/changelog.
