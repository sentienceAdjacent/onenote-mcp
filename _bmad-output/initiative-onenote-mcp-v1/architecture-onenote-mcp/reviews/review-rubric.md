---
reviews: architecture-onenote-mcp.md (status: draft, AD-1..AD-16)
lens: rubric (initiative-altitude build-substrate checklist)
date: 2026-11-03
mode: read-only
---

# Rubric review: architecture-onenote-mcp

**Verdict:** A solid, terse spine. The COM constraints from spike 1 are carried through faithfully. It is not ready to finalize, though. Two rules don't prevent the divergence they claim to prevent (AD-6 grant gate, AD-8 snapshot fidelity). The tool-result envelope, the settings schema, and the write-timeout semantics are left for each story to invent. The operational envelope (upgrade, uninstall, config/journal migration, disk growth) is silent.

Checked against: the spine, `.memlog.md`, the spike-1 findings doc, and CLAUDE.md. Stack was verified: nuget.org flatcontainer lists `ModelContextProtocol` 2.3.1 as the latest version (checked 2026-11-03). .NET 10 is the current LTS.

## Findings

### F1 — HIGH — AD-6: the gate the model can't grant itself is grantable by the model
- **Problem:** AD-6 claims to prevent "a skill-only or model-grantable gate". One of its two grant routes is "editing the settings file", at a fixed, documented path (`%APPDATA%\OneNoteMcp\settings.json`, AD-10). The main client, Claude Code, has file-write tools and can edit that file itself. The server re-reads the file at every access check (AD-10), so a model-written grant takes effect on the next call. The rule closes the MCP-tool route and leaves the file route open.
- **Fix:** Choose one and write it into the Rule:
  - (a) State the threat model honestly. Grants guard against *accidental* writes, not against an agent that edits files. Then have the plugin ship a Claude Code permission `deny` rule for `Edit`/`Write` on that path, and say that other clients get no such protection.
  - (b) Make grants something only the user can produce: elicitation only, or a separate interactive `OneNoteMcp.exe grant` CLI. If you also want tamper evidence, keep grants in a DPAPI-protected store rather than in plain `settings.json`.

  Either way, the "Prevents" line has to match what the rule actually delivers.

### F2 — HIGH — AD-7/AD-8 vs AD-9: on cloud notebooks the snapshot is silently lossy, so `restore` can delete images
- **Problem:** The spike found that on cloud notebooks, `GetPageContent` with `piBinaryData` *drops the Image element*; only `GetBinaryPageContent` fails with `0x8004200F`. AD-8 assumes the snapshot holds "full XML, binary included". AD-9 says `BinaryUnavailable` degrades and never fails. AD-7 says any failed step aborts. Put together, the snapshot succeeds but has no images. Restoring it through `UpdatePageContent` would then remove images that were still on the page. Stories will also split on whether a degraded snapshot aborts the write.
- **Fix:** Add to AD-8:
  - The snapshot records which binaries it could not capture, in the metadata header.
  - `restore` never removes elements that are missing from a degraded snapshot. It either merges, or refuses and reports the affected pages.
  - AD-7 step 3 proceeds with a degraded snapshot, which is not a failure, and the degradation is reported in the write's result.

### F3 — HIGH — AD-7/AD-8: the pipeline is defined for page-content writes only
- **Problem:** "Snapshot the target page's full XML", "every referenced objectID exists" and "the target section is not locked" all assume one page is the target. Most v1 writes don't fit that: `CreateNewPage` (no prior state), `UpdateHierarchy` section create and rename, soft `DeleteHierarchy` of a page or section, and later move. Each story will invent its own idea of what to snapshot and validate for those. That is exactly the divergence AD-7 exists to prevent.
- **Fix:** Add a small table to AD-7 with one row per write kind: page content, page create, hierarchy update, soft delete, permanent delete. Each row gives the state read, the snapshot taken (page XML / hierarchy fragment XML / none + OneNote backup), the validations, and the optimistic check, which for hierarchy writes may be none. Also consider swapping steps 3 and 4 (validate, then snapshot), so that rejected writes don't leave journal files behind. This is cheap to do now.

### F4 — MEDIUM — AD-4 vs AD-9/AD-12: "returned unchanged" contradicts placeholders and post-filtering; no result envelope
- **Problem:**
  - AD-4 says tools return 2013 XML *unchanged*.
  - AD-9 replaces missing binaries with a placeholder.
  - AD-12 drops recycle-bin pages from the XML and adds `possiblyIncomplete: true`. A bare XML string has nowhere to carry that flag.

  The spine never says what a tool result looks like: raw text, MCP `structuredContent`, `{xml, warnings[], possiblyIncomplete}`, or something else. It also doesn't say what a placeholder looks like. Every read tool will pick its own answer.
- **Fix:** Add one convention row and amend AD-4. Every tool returns one envelope, e.g. `{ xml, warnings: [{code, objectId}], possiblyIncomplete? }`. The server may *remove* filtered nodes and may *mark* degraded nodes (state exactly how: an attribute or a `warnings` entry; never invented 2013-schema elements). Apart from that the XML is unchanged. The error shape (AD-9) is the failure branch of the same envelope.

### F5 — MEDIUM — Operational envelope is silent: upgrade, uninstall, config/journal migration, disk growth
- **Problem:** Distribution (AD-15) is decided. Nothing covers what happens after install:
  - **Upgrade:** How does the user, or the plugin, get a new exe? How are the plugin version and the exe version coupled (AD-14 versions the tools, not the bundle)? What happens when a running server process holds the exe locked during an update?
  - **Mixed versions:** AD-10 says several processes share `settings.json`. After an upgrade, those processes can be *different versions*. `settings.json` and the journal metadata header have no schema-version field, so an old process can rewrite a new-format file and lose fields, and `restore` can misread an old snapshot.
  - **Uninstall:** Removing the plugin leaves `%APPDATA%\OneNoteMcp` (with grants) and `%LOCALAPPDATA%\OneNoteMcp` (the journal holds full note content, logs) behind. Nothing says whether they are kept, removed, or documented.
  - **Disk:** The journal snapshots every write with binaries and keeps them for 30 days, with no size cap. The log roll size and retention aren't set.
- **Fix:** Add one AD, "Install lifecycle":
  - `settings.json` and journal headers carry `schemaVersion`.
  - Readers ignore unknown fields and preserve them on rewrite. A process refuses to write a newer `schemaVersion` than it understands.
  - The exe version is pinned to the plugin version.
  - Upgrade and uninstall steps go in the README, including where data stays and how to purge it.
  - The journal gets a size cap next to its day retention.

  If any of this waits, move it to Deferred explicitly, naming the packaging story.

### F6 — MEDIUM — AD-10: "temp file then rename" is atomic replace, not a cross-process lock
- **Problem:** Write-then-rename stops readers from seeing a torn file. It does not stop two processes that both read, modify and write `settings.json` from losing each other's update. Two sessions granting different notebooks at about the same time is a realistic case: one grant silently disappears.
- **Fix:** Write the mechanism into the Rule. Do every read-modify-write under a named mutex (e.g. `Local\OneNoteMcp.Settings`), and re-read inside the lock. Keep temp-then-rename for atomicity.

### F7 — MEDIUM — AD-3: timeout semantics are undefined for the STA thread and for writes
- **Problem:** A COM call that times out cannot be cancelled. The STA thread stays blocked, so every queued call behind it also times out. AD-3 doesn't say what the gateway does next (abandon the thread and start a new STA thread plus reference? mark the gateway poisoned?). For writes, a `Timeout` means the outcome is *unknown*: `CreateNewPage` may have succeeded. AD-9's `retryable` flag on such a write invites duplicates. Relatedly, "retry RPC_E_CALL_REJECTED with backoff" doesn't say whether that is done with an `IMessageFilter` registered on the STA thread (the standard approach) or with manual loops.
- **Fix:** Add to AD-3:
  - On timeout, the gateway abandons that STA thread and reference and starts fresh for the next call.
  - A timed-out write returns `Timeout` with `retryable: false` and a `userAction` telling the user to verify, or the code becomes `OutcomeUnknown`.
  - Call rejection is handled by an `IMessageFilter` registered on the STA thread.

### F8 — MEDIUM — AD-2 + Deferred "Events": the STA design could need rework for a later round
- **Problem:** The scope direction says later rounds must not need rework. Events (`OnHierarchyChange`) need the STA thread to pump Windows messages. The `IMessageFilter` in F7 needs that too. A ComGateway built as a plain blocking-queue loop would have to be rewritten. So this Deferred item really can let the v1 gateway diverge from what round 2 needs.
- **Fix:** AD-2 should require the STA thread to run a message-pumping loop (e.g. `MsgWaitForMultipleObjectsEx` with a work queue) from the first build story. Events stay deferred.

### F9 — LOW — AD-3: "refuse to run" vs the `IntegrityMismatch` error code; OneNote may not be running at startup
- **Problem:** If OneNote isn't running at startup, its integrity level can't be compared. And a stdio server that exits shows up as an opaque "server failed" in the client, while the closed enum already has `IntegrityMismatch` for tools to return.
- **Fix:** Split the rule:
  - At startup, if the server process is elevated, log the problem and serve every tool as `IntegrityMismatch` (or exit; pick one).
  - At each activation, compare against the running `ONENOTE.EXE` and return `IntegrityMismatch` before calling COM.

### F10 — LOW — Settings schema shape is not fixed although four stories write to it
- **Problem:** The access, journal, timeouts and activation stories all add keys to `settings.json`. The key names and structure are left to whoever writes first. Grant keying (notebook ID plus display path and name) sits in Deferred, but it determines the schema.
- **Fix:** In Conventions, say that Core owns one `Settings` type, with camelCase keys and grants keyed by notebook ID (`{ id, displayName, path, write, permanentDelete }`). Move the "key by ID, store path and name" decision out of Deferred and into AD-6. It is already a decision.

### F11 — LOW — Enforceability wording
- AD-5 says "About one MCP tool per COM method". "About" can't be enforced. Say "one tool per COM method, except where listed", and list the known exceptions: `restore`, any grant/elicitation surface, and the `Windows` collection.
- The Conventions naming rule ("snake_case of the COM method") doesn't cover server-native tools such as `restore`. Add: server-native tools use `verb_noun`.
- AD-16 doesn't say which target frameworks let CI stay cheap. Say: `Core`/`Xml` target `net10.0`, and `Com`/`Server` target `net10.0-windows`. CI runs on `windows-latest` unless the unit tests are kept free of Windows dependencies.

## Bloat / altitude
- The spine is lean. No rationale essays. The parenthetical reasons in "Prevents" lines are fine.
- `companions: [README.md]` names a file that doesn't exist yet in the folder. Either create it or mark it pending, so the frontmatter isn't dangling.
- The first mermaid diagram (layers) and the arrow rule overlap with the Structural Seed tree. This is acceptable, but if you need to trim, drop one of them.

## Checklist summary
| Check | Result |
| --- | --- |
| Real divergence points fixed | Mostly. Gaps: result envelope (F4), non-page write pipeline (F3), settings schema (F10), write-timeout semantics (F7) |
| Rules enforceable and actually prevent the stated divergence | AD-6 does not (F1). AD-8 is unsafe on cloud notebooks (F2). AD-10's lock is mis-specified (F6) |
| Deferred items can't cause divergence | Events (F8) and grant keying (F10) can |
| Tech verified-current | Yes. ModelContextProtocol 2.3.1 is the latest on nuget.org. .NET 10 is the LTS |
| Operational envelope covered | No. Upgrade, uninstall, config/journal migration and disk caps are silent (F5) |
