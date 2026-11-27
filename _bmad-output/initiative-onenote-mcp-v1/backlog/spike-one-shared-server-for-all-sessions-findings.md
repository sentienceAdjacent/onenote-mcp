---
title: 'Findings: one shared server for all sessions'
type: 'spike-findings'
findings_for: '30'
status: 'final'
created: '2026-11-27'
evidence: 'Desk research only: the spine (architecture-onenote-mcp.md, updated 2026-11-27), the code at ca2aa59, deferred-work.md (the 2026-11-10 shared-server note), and current external docs read through context7 on 2026-11-27 (MCP specification 2026-07-28, the MCP C# SDK docs and source, the Claude Code docs). No code, no prototype, no OneNote probe.'
---

# Findings: one shared server for all sessions

Backlog 30 asks whether one long-running `OneNoteMcp.exe` that every Claude session connects to (for example over localhost HTTP) is worth building instead of today's one stdio server per session, and what it would change. This is a desk comparison. Labels: **verified** means read this session in the file or doc named (for external docs: read, not tried); **recalled** means from memory of Windows or general knowledge, not checked here; **inferred** means reasoned from those sources, not observed; **unverified** means not checked and could be wrong.

## Short answer

1. **The ticket's premise is partly wrong: the write lock and the journal are already shared across sessions.** AD-18's lock is the named mutex `Local\OneNoteMcp.Write`, and it "Prevents: two writers, in one process or in two" (verified: spine AD-18; `src/OneNoteMcp.Core/Writes/WriteLock.cs`, `DefaultName`). The journal is one folder of files that every process writes and prunes (AD-8, AD-10), and settings and grants are one `settings.json` behind `Local\OneNoteMcp.Settings` (AD-10). AD-10 already says "Several server processes, one per client, may run at once." So a shared server would not add one lock or one journal; they already exist.
2. **What is really per process today** is smaller: the COM reference and its STA thread (AD-3), the record of an abandoned write (AD-3, AD-18), `once` read grants (AD-21, the one exception AD-10 names), issued attachment paths, the result store (AD-20), the search page-text cache (AD-12), the one-waiting-picker limit, and each process's idle-release timer. Only one of these causes a real cross-session safety gap today: a timed-out write that other processes can't see (Safety gap 1). A second, the stale search cache, is already covered by a setting.
3. **Grant dialogs already go to the right session**, because each server has exactly one client. In a shared server that stops being automatic, and `once` grants and issued paths would need re-scoping per session. Over HTTP, MCP revision 2026-07-28 has no sessions and no server-to-client `elicitation/create`; but an HTTP server in the C# SDK's `Stateful` mode refuses 2026-07-28 so that clients fall back to the older handshake, which keeps today's dialog code and a per-session `McpServer`. Rebuilding the dialogs on multi round-trip requests (MRTR) is therefore a choice, not a must, as long as clients keep that fallback. That is a bet with a limited life.
4. **Security gets harder.** A same-user process can already run `OneNoteMcp.exe` or call OneNote's COM API, and AD-6's Limits accept that. What is new with a localhost port is reach from other Windows users on the machine and from web pages in a browser (DNS rebinding); the spec says the server MUST validate `Origin` and SHOULD authenticate. Today's stdio pipe needs neither.
5. **Install and lifecycle get much harder.** A plugin can declare an HTTP server by URL, but nothing in a plugin starts a long-running process for it; something else must start, stop and upgrade it. After a plugin update an old daemon would keep serving the new skill, and Claude Code's 14-day cleanup of the old version's folder would meet an exe that is still running.
6. **Recommendation: don't build a shared server now.** Keep per-session stdio, file one fix (make an abandoned write visible to every server process) and one check (our grant dialogs under Claude Code's 2026-07-28 negotiation). If a shared server is ever wanted, prefer a stdio shim in front of a named-pipe daemon over localhost HTTP (Effort, below).

## What is shared today and what isn't

| State | Today | Source |
|---|---|---|
| Write lock | Shared by all of the user's sessions: named mutex `Local\OneNoteMcp.Write`, held AD-7 steps 2 to 8. The spine calls it "machine-wide", but a `Local\` name is scoped to one Windows (Terminal Services) session (recalled), so another signed-in Windows user has their own | AD-18; `WriteLock.cs` (verified) |
| Settings and stored grants (write, permanent delete, `hours`/`untilRevoked` read grants) | Shared: one `settings.json`, read-modify-write under `Local\OneNoteMcp.Settings`, re-read at every access check | AD-10 (verified) |
| Journal and backups | Shared: one folder; each entry written once to a temporary name then renamed; each process prunes and tolerates entries vanishing | AD-8, AD-10 (verified) |
| Rolling log files | One file per process in one shared folder; every process prunes the folder to 4 files before opening its own and again on every roll, so about 5 files in all | `src/OneNoteMcp.Server/Logging/FileLog.cs` (`DefaultRetainedFileCount = 5`, `Prune(..., RetainedFileCount - 1)`, `PruneOnOpen`) (verified) |
| COM reference and STA thread | Per process: one STA thread owns every COM call and serialises that process's tool calls | AD-2, AD-3; `src/OneNoteMcp.Com/StaThread.cs` (verified) |
| Abandoned write (a timed-out write still running in OneNote) | Per process: new non-read pipelines in the same process return `ComBusy`; the lock is kept "until the gateway reports none outstanding or the pipeline's deadline runs out, whichever comes first" | AD-3, AD-18 (verified) |
| `once` read grants | Per process, in memory | AD-21, AD-10 ("holds only in the process that asked"); `OnceWindows.cs` ("This process's once windows") (verified) |
| Issued attachment paths (`pathCache` values exempt from the file-path dialog) | Per process, in memory | AD-7 `PageWrite`; `IssuedPathCaches.cs` (verified) |
| Result store (`read_result`) | Per process, in memory | AD-20 (verified) |
| `quick_filing` one-waiting-picker limit | Per process | AD-7 `AppState`, AD-9 `ComBusy` row (verified) |
| Search page-text cache | Per process, in memory; another server's write can leave it stale (a stated known gap) | AD-12 (verified) |
| Idle release | Per process: each server drops its COM reference 5 s after its last call | AD-3 (verified) |
| Memory | 17.9, 20.4, 48.4 and 54.5 MB working set for four servers, idle CPU near zero | `deferred-work.md`, 2026-11-10 note (measured then, not re-measured) |

## Benefits a shared server would bring

- One COM client: one STA, one idle release, fewer references holding OneNote open (backlog bug 3).
- Closes Safety gap 1 and the stale-cache gap (Safety, below).
- Less memory: about 18 to 55 MB per extra session today (measured 2026-11-10, four servers).
- One log file at a time instead of one per process. Today, with several busy sessions, the shared five-file limit can prune another session's recent log before anyone reads it (inferred from `FileLog.cs`; not observed). Raising the limit, or pruning by age, would fix that without a shared server.

None of these is a correctness problem today except Safety gap 1, and a backlog story can close that without a shared server.

## Comparison

### Safety: write lock and journal (AD-6, AD-8, AD-18)

- **Per-session stdio:** one lock and one journal for all sessions already (table above). Two real gaps remain, both inferred from the spine and the code:
  1. *An abandoned write is invisible to other processes.* Two ways it opens:
     - *After the deadline.* If session A's write times out, A keeps the mutex until its call returns or the pipeline's deadline runs out (AD-18). The deadline is 120 s, or 600 s for the classes that publish, not counting time on a grant dialog (verified: `src/OneNoteMcp.Core/Writes/WritePipeline.cs`, `DefaultDeadline` and `PublishingDeadline`). Another process waits for the lock up to `timeouts.callSeconds` (30 s by default), capped by its own deadline (verified: `WritePipeline.cs` remarks, `LockAndDeadline.cs`). After A's deadline, session B can take the lock and write while A's call may still be changing OneNote. Inside one process the `ComBusy` check after step 2 catches this; across processes nothing does.
     - *When A's process ends.* The lock is held on a background thread of A's process (verified: `WriteLock.cs`), and "An `AbandonedMutexException` counts as acquiring the lock" (verified: spine AD-18). If the user closes session A mid-write, B gets the lock at once, while OneNote may still be running A's call (inferred; OneNote finishing an out-of-process call whose caller died is unprobed).
  2. *Another server's writes can leave the search cache stale* (AD-12's known gap). `search.cacheMaxMB: 0` avoids it, and the README says so.

  Two smaller per-process items, `IssuedPathCaches` and the one-picker limit, matter only to a shared server; they are under the next two headings.
- **Shared server:** both gaps close, because there is one gateway and one cache. Nothing else in this area gets safer: the lock and journal are already shared.
- **Narrow fix for gap 1 (inferred, not designed):** record an outstanding abandoned write where every process's `ComBusy` check after step 2 can see it, for example a marker file. The marker must outlive the process that wrote it, since the process ending is one of the two ways in; so its only escape is an age limit, not clearing on exit. That is an open trade-off: the spine releases the lock at the deadline on purpose, so a call that never returns can't block writes forever, and the age limit has to keep that property. It looks backlog-sized, but that is an estimate, not a design. Gap 2 needs no new work.

### Safety: grant dialogs reach the right session (AD-6, AD-7, AD-21)

- **Per-session stdio:** correct by construction, as long as the connection delivers `elicitation/create` (see "The 2026-07-28 revision", below). `ElicitationGrantPrompt` calls `ElicitAsync` on the `McpServer` of the call, and a stdio server has one client. A `once` grant holds only in that process, so only in that session. So do the attachment paths a read issued: `IssuedPathCaches` exempts a `pathCache` value the server itself returned from AD-7's file-path dialog, "in memory only, for this server's lifetime" (verified: `src/OneNoteMcp.Server/Grants/ElicitationGrantPrompt.cs`; `src/OneNoteMcp.Core/Writes/IssuedPathCaches.cs`, a singleton in `ServerSetup.cs`).
- **It fails closed.** `ElicitationGrantPrompt.AskAsync` returns `Unsupported` when `ClientCapabilities.Elicitation` is null or `ElicitAsync` throws `InvalidOperationException`, and `Cancelled` on an `McpException` (verified: `ElicitationGrantPrompt.cs`). Then no dialog shows and the write or protected read is refused (`WriteNotGranted`, `ReadNotGranted`; AD-6, AD-21). The SDK warns that on 2026-07-28 a non-null capability "does not mean the transport can deliver a direct server-to-client request" (verified: csharp-sdk MRTR docs); `ElicitAsync` then throws `InvalidOperationException`, which is still caught. So a broken dialog would show as grants that can't be given, not as writes without a grant.
- **Shared server over HTTP, initialize-handshake sessions:** the C# SDK gives each HTTP session its own `McpServer`, so a dialog raised in a tool call goes back to the session that made the call (inferred from the SDK docs on sessions and `ConfigureSessionOptions`; unverified in code). `once` grants would have to be keyed by session, or one session's grant would open the section to every session for its window, which weakens AD-21. The same holds for `IssuedPathCaches`: shared, a path issued to one session's read would skip the file-path dialog for another session's write.
- **Shared server over HTTP, 2026-07-28 requests:** the SDK gives two ways (verified: csharp-sdk stateless and MRTR docs, and `StreamableHttpHandler.cs`):
  - *Keep sessions:* with `SessionMode = HttpServerSessionMode.Stateful`, the server "refuses such requests with an UnsupportedProtocolVersion (-32022) error so dual-path clients fall back to the initialize handshake". Today's `ElicitAsync` code and a per-session `McpServer` keep working. This rests on clients keeping that fallback, a bet with a limited life (inferred). Whether 2.3.1, the SDK version we pin (`Directory.Packages.props`), has `SessionMode` at all is **unverified**.
  - *Go stateless:* `Stateless` (the default) and `StatefulForInitializeClients` serve 2026-07-28 without a session, and there `ElicitAsync` throws "Elicitation is not supported in stateless mode". The replacement is throwing `InputRequiredException` (MRTR). Consequences (inferred):
    - Every grant dialog (write grant, read grant, permanent-delete confirmation, file-path acceptance, notebook location) becomes a two-round call, and the pipeline resumes from `requestState`. AD-7 step 1's "raise the dialog, then continue" becomes "return, then re-enter step 1".
    - `requestState` comes back from the client, so it would need integrity protection (signed and bound to the call's target), or a forged retry could claim a grant answer for a different target (inferred; the spec says only that the client echoes it).
    - With no session, a `once` grant has nothing to be scoped to except a server-minted handle passed as a tool argument, which the spec suggests for cross-call state (verified: spec changelog, SEP-2567). That is a contract change (AD-14).

### The 2026-07-28 revision (applies to today's server too)

- **What the revision removes:** `Mcp-Session-Id`, protocol sessions and the `initialize` handshake, and the server-to-client `elicitation/create` request; a tool asks for input by returning an `input_required` result, and the client retries the call with `inputResponses` and the server's `requestState` (verified: MCP spec 2026-07-28, changelog and `tools/call`).
- **What Claude Code does:** on the v2 MCP client runtime only, it asks stdio, HTTP and claude.ai connector servers whether they support 2026-07-28 and uses it with those that do; `MCP_PROTOCOL_NEGOTIATION=legacy` keeps every server on the earlier handshake. With that variable unset it can skip asking a stdio server for 7 days, for example after the server exits when asked (verified: code.claude.com/docs/en/env-vars and /mcp, "MCP client runtimes"). The changelog says stdio connections negotiate 2026-07-28 by default on every install (verified). So one passing run doesn't show which revision a connection used.
- **What the SDK says:** "the SDK still allows the legacy methods on `2026-07-28` stdio sessions because stdio is implicitly single-process / stateful and the client handler is wired up regardless of negotiated revision" (verified: csharp-sdk MRTR docs, Compatibility). The same page says its `Stateful` row for 2026-07-28 "applies to stdio and other non-HTTP stateful sessions".
- **What is unverified:** whether 2.3.1 behaves as those current docs say, and whether Claude Code answers an `elicitation/create` sent on a 2026-07-28 stdio connection. If either fails, our dialogs fail closed (above), and every write to an ungranted notebook and every protected read is refused. No recent sitting is cited here that showed a grant dialog under the newest Claude Code, so this check should come first.

### Safety: the single STA thread (AD-1, AD-2, AD-3)

- **Per-session stdio:** one STA per process; a hung call poisons only that session's gateway (AD-3), and the other sessions keep working.
- **Shared server:** one STA for every session. Consequences (inferred from AD-3):
  - One session's 300 s `Publish`, or any hung call, queues every other session's reads behind it; a timeout poisons the thread and fails every queued call with `ComBusy`, in every session. After two abandoned threads the gateway returns `ComUnavailable` to everyone.
  - One crash ends every session's OneNote tools at once.
  - OneNote is an out-of-process COM server, so it may already serialise calls from several clients; then per-session processes buy less parallelism than they appear to (inferred, unprobed).
- **One picker at a time:** AD-7 `AppState` makes a second `quick_filing` call `ComBusy` while "one of this process's" calls waits for the user (verified: spine AD-7 `AppState`). Across stdio servers each session has its own allowance; in a shared server one session's open picker would block every session's `quick_filing` for up to 300 s unless the rule were re-scoped (inferred).
- **Benefit:** one COM client instead of N. Backlog bug 3 ("cleaning up" after a restart) showed that a held reference keeps `ONENOTE.EXE` alive; N servers mean N chances to hold one. The 5 s idle release (AD-3, backlog 3) already narrows this, and a sitting has not yet checked it (AD-3, Deferred).

### Security: localhost access and client authentication

- **Per-session stdio:** the client starts the server and owns its pipes; no other process can talk to that server. AD-6's Limits already say the grants guard "against accidents and casual misuse, not against a hostile local process" (verified: spine AD-6, Limits). A same-user process can already start `OneNoteMcp.exe` itself or call OneNote's COM API directly, so it gains nothing from a shared server (inferred).
- **Localhost HTTP:** what is new is who else can reach it.
  - *Other Windows users on the machine.* A TCP port on 127.0.0.1 is reachable from every Windows session on the machine, not only the user's (recalled). Our mutexes are per Windows session (table above) and the journal's DPAPI protection is per user (verified: AD-8), so today another user can't reach this user's server at all.
  - *Web pages.* A page in a browser can try to reach a local port; that is why the spec requires: validate `Origin` on every connection and answer 403 when it is present and invalid ("to prevent DNS rebinding attacks"); bind to 127.0.0.1, not 0.0.0.0; and "SHOULD implement proper authentication for all connections" (verified: MCP spec 2026-07-28, Streamable HTTP, Security). Tokens go in `Authorization: Bearer`, never in the URL (verified: spec, Authorization).
  - *What grants still stop.* A grant dialog still stands before a write to a notebook without a write grant (AD-6), and a caller that can't answer elicitation is refused. But reads of unprotected sections need no grant, and a stored write grant lasts until revoked and raises no dialog (verified: AD-6, AD-10), so an unauthenticated caller could read most notes and write to notebooks already granted (inferred). So a token is mandatory, not optional.
  - *Where the token lives:* for a plugin's MCP `http`, `sse` and `ws` servers, `${...}` resolves in `url`, `headers` and `headersHelper` (verified from docs, not tried: code.claude.com/docs/en/plugins/manifest-reference, "Where each variable resolves"), and `headersHelper` runs at each connect with `CLAUDE_PLUGIN_ROOT` set when a plugin provides the server (verified from docs: code.claude.com/docs/en/mcp). So the plugin could run `${CLAUDE_PLUGIN_ROOT}/OneNoteMcp.exe token` as a headers helper that reads a per-user secret (for example DPAPI-protected under `%LOCALAPPDATA%`).
  - *Integrity level:* AD-3 refuses a server whose integrity differs from OneNote's. A daemon started from an elevated session would refuse every call for every session; an elevated client could still reach a normal daemon over HTTP (inferred).
- **Named pipe (only with a shim, below):** a pipe can carry a DACL limited to the current user, so no token is needed and other users are kept out (recalled; Windows named-pipe security, not checked against our setup).

### Install and plugin config

- **Today:** `plugin/.mcp.json` declares `desktop` as `"type": "stdio"`, `"command": "${CLAUDE_PLUGIN_ROOT}/OneNoteMcp.exe"` (verified). Claude Code starts it at session start and stops it at session end (verified: Claude Code docs, plugin MCP servers, "Automatic lifecycle").
- **HTTP:** a plugin's `.mcp.json` can declare `"type": "http"` with a `url` and `headers` (verified: Claude Code plugin and MCP docs). But Claude Code then only connects; it starts nothing. Something else must start the daemon: a `SessionStart` hook can start a background program (verified: Claude Code hooks docs), but every session would race to start it, the hook needs a single-instance guard, and a session whose hook failed connects to nothing. A login task or tray app is a second install step outside the plugin.
- **Upgrades:** AD-19 installs each version's exe in a location of its own, an upgrade "never overwrites an exe that is running", and with `${CLAUDE_PLUGIN_ROOT}` Claude Code removes an old version's directory 14 days after an update (verified: spine AD-19, Install and upgrade). So an update doesn't fail on a locked exe. The problems are different (inferred, unprobed):
  - the old daemon keeps serving after the update, so a newer skill talks to an older tool contract until the daemon restarts;
  - 14 days later the cleanup meets a directory whose exe may still be running.

  Either way, plugin and daemon need a version handshake (AD-14).
- **Port:** a fixed port can collide; a dynamic one needs a discovery file the `url` can't read without a helper (inferred).

### Lifecycle: who starts and stops it

- **Today:** Claude Code. One process per session, ended with the session. `uninstall --purge` warns about other running servers (verified: `src/OneNoteMcp.Server/Program.cs`, the comment at line 51).
- **Shared:** the daemon needs its own start (first session or login), single-instance guard, idle exit when no session is connected, crash restart, and a clean stop for upgrades and `uninstall`. Logs would no longer go to a session's stderr (AD-13 assumes stdio); only the rolling file log would remain. Whether Claude Code ends child processes started by a hook or a server when the session ends (for example through a Windows job object) is **unverified**, and it decides whether a daemon started from a session survives it.

### Effort

| Option | What it takes | Size (inferred) |
|---|---|---|
| A. Keep per-session stdio | One fix (abandoned-write visibility across processes, Safety gap 1) and one check (grant dialogs under Claude Code's 2026-07-28 negotiation) | 1 backlog story and 1 small check |
| B. Shared localhost HTTP daemon | A spike (lifecycle under Claude Code, job objects, which protocol revision Claude Code uses with it, whether 2.3.1 has `SessionMode`, many-client COM behaviour); spine amendments, among others to the frontmatter `scope` ("Local stdio MCP server"), the Structural Seed diagram, AD-1, AD-2 ("Each process has one live `ComGateway`"), AD-3, AD-6, AD-7, AD-9 (the `ComBusy` row's "this process's" picker), AD-10, AD-12 (its known gap names "another server process"), AD-13, AD-14, AD-15, AD-18, AD-19, AD-20 and AD-21; `ModelContextProtocol.AspNetCore` and Kestrel (a new dependency); Origin check and token auth; per-session `once` grants, issued paths and result IDs; a re-scoped one-picker rule; daemon start, single instance, idle exit, version handshake and uninstall; README and skill; tests. With `Stateful` sessions the dialogs stay as they are; going stateless adds the MRTR rework of every dialog with signed `requestState` and handle-scoped `once` grants | An epic: roughly 6 to 8 stories with `Stateful` sessions, 9 to 12 with MRTR |
| C. Stdio shim plus named-pipe daemon | As B with `Stateful` sessions, minus HTTP, Origin and tokens (the pipe is user-only), plus a shim mode in the exe that relays stdio to the pipe and starts the daemon if none runs. Plugin config stays stdio. Each shim connection is one stateful stream session, which the SDK docs put with stdio for 2026-07-28 ("stdio and other non-HTTP stateful sessions"), so the legacy dialog flow should survive even when Claude Code negotiates 2026-07-28 (inferred from the docs; unverified for 2.3.1 and for a pipe) | An epic, roughly 6 to 9 stories |

Option C is the better shape if a shared server is ever wanted: the plugin keeps `"type": "stdio"`, Claude Code keeps starting and stopping something per session, the pipe's DACL replaces a token, and each shim is a natural session for dialogs and `once` grants. It still carries B's blast radius and upgrade problems, and depends on two unverified things: the daemon outliving the shim that started it, and the SDK keeping `elicitation/create` on a named-pipe stream session when Claude Code negotiates 2026-07-28.

## Recommendation

Keep one stdio server per session and don't plan a shared-server epic now: the lock, journal and grants it was meant to unify are already shared, while a shared server adds a new reach for other Windows users and web pages that must be authenticated, its own lifecycle and upgrade path, and one failure point for every session; over HTTP its dialogs also depend either on clients keeping the older handshake or on an MRTR rebuild. Instead, file one backlog story to make an abandoned write visible to every server process, and run one check, first, that our grant dialogs still work now that Claude Code negotiates MCP 2026-07-28 with stdio servers by default; revisit a shared server (as option C) only if many concurrent sessions become normal or a probe shows OneNote misbehaving with several COM clients.

## Decision for Phill

:::question
Should a shared server be planned as a future epic?
- **A. No: keep per-session stdio**, and file the fix (abandoned-write visibility across processes) and the check (grant dialogs under MCP 2026-07-28).
- **B. Yes, as a localhost HTTP daemon** (epic: auth token, Origin check, `Stateful` sessions or MRTR dialogs, daemon lifecycle).
- **C. Yes, as a stdio shim plus a named-pipe daemon** (epic: plugin stays stdio, user-only pipe, per-shim sessions).
- **D. Not now, park it** with no follow-ups; revisit at the next initiative.

Recommended: A
:::

Decision (Phill, 2026-11-27, Inbox): option A. Keep one stdio server per session; no shared-server epic. The two follow-ups are filed as backlog 31 (abandoned-write visibility across processes) and backlog 32 (grant dialogs under MCP 2026-07-28).

Whatever is chosen goes in the spine's memlog as a dated line (the ticket's Verify), after this doc's Fable final review.

## Follow-ups under option A

- **Fix:** make an abandoned write visible to every server process, with an age limit as its only escape (Safety gap 1).
- **Check, first:** a grant dialog under the newest Claude Code with `MCP_PROTOCOL_NEGOTIATION=auto`, recording which revision the connection used (The 2026-07-28 revision).
- **Spine wording:** AD-18 calls the write lock "machine-wide"; correct it, or record that a `Local\` name is scoped to one Windows (Terminal Services) session (recalled).
- **Optional:** prune the rolling logs by age, or raise the shared five-file limit, so one busy session doesn't prune another's recent log (Benefits).

## Not checked

- Whether ModelContextProtocol 2.3.1 allows `ElicitAsync` on a 2026-07-28 stdio connection as the current SDK docs say, and whether Claude Code answers `elicitation/create` on such a connection.
- Whether 2.3.1 has `HttpServerSessionMode` (options B and C).
- Whether Claude Code ends processes started by a session's hooks or servers when the session ends.
- How OneNote schedules COM calls from several clients at once, which `ONENOTE.EXE` several clients bind to (AD-3 Deferred), and whether OneNote finishes a call whose caller has exited.
- Named-pipe DACL behaviour between an elevated and a normal process of the same user.

## Sources

- Spine: `_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md`: frontmatter `scope`, AD-1, AD-2, AD-3, AD-6 (Limits), AD-7, AD-8, AD-9, AD-10, AD-12, AD-13, AD-18, AD-19, AD-20, AD-21, Consistency Conventions, Structural Seed, Deferred.
- Code: `src/OneNoteMcp.Server/Program.cs` (`WithStdioServerTransport`; the uninstall comment at line 51), `src/OneNoteMcp.Server/ServerSetup.cs` (per-process singletons), `src/OneNoteMcp.Core/Writes/WriteLock.cs`, `src/OneNoteMcp.Core/Writes/WritePipeline.cs`, `src/OneNoteMcp.Core/Writes/LockAndDeadline.cs`, `src/OneNoteMcp.Core/Writes/IssuedPathCaches.cs`, `src/OneNoteMcp.Server/Grants/ElicitationGrantPrompt.cs`, `src/OneNoteMcp.Server/Logging/FileLog.cs`, `src/OneNoteMcp.Com/StaThread.cs`, `src/OneNoteMcp.Core/Access/OnceWindows.cs`, `plugin/.mcp.json`, `plugin/hooks/hooks.json`, `Directory.Packages.props`.
- `_bmad-output/initiative-onenote-mcp-v1/deferred-work.md`: the 2026-11-10 shared-server note and memory measurements; `deferred-work-triage-2026-11-27.md` item D63; backlog bug 3 (`bug-onenote-shows-cleaning-up-after-a-restart-while-a-server-is-connected.md`).
- MCP specification 2026-07-28: https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http (Security), https://modelcontextprotocol.io/specification/2026-07-28/changelog (sessions removed, SEP-2567), https://modelcontextprotocol.io/specification/2026-07-28/basic (statelessness), https://modelcontextprotocol.io/specification/2026-07-28/server/tools (`input_required`), https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization.
- MCP C# SDK: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/mrtr/mrtr.md (Compatibility), .../docs/concepts/stateless/stateless.md (the 2026-07-28 revision, hybrid mode), .../docs/concepts/elicitation/elicitation.md, .../docs/concepts/capabilities/capabilities.md, .../docs/concepts/transports/transports.md, .../src/ModelContextProtocol.AspNetCore/StreamableHttpHandler.cs.
- Claude Code docs: https://code.claude.com/docs/en/mcp (HTTP servers, env expansion, `headersHelper`, elicitation, MCP client runtimes, plugin servers' lifecycle), https://code.claude.com/docs/en/plugins/manifest-reference (where each variable resolves), https://code.claude.com/docs/en/plugins/components, https://code.claude.com/docs/en/hooks (SessionStart, background processes), https://code.claude.com/docs/en/env-vars (`MCP_PROTOCOL_NEGOTIATION`), https://code.claude.com/docs/en/changelog.
