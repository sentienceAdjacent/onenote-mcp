---
type: review
lens: reconcile (inputs -> spine)
target: architecture-onenote-mcp.md (status: draft, AD-1..AD-16)
inputs:
  - .memlog.md (lines 1-81; later SUPERSEDES entries applied)
  - spike-what-can-the-onenote-desktop-com-api-do-on-this-machine-findings.md (final)
  - CLAUDE.md "OneNote COM constraints"
date: 2026-11-03
---

# Reconcile review: inputs vs spine

**Verdict:** most decisions landed faithfully. There are 5 substantive gaps, all in failure and recovery paths (optimistic-check basis, reconnect vs not-running, hung-call recovery, HRESULT map, cloud-binary degrade), plus about a dozen smaller misses.

Severity: **H** = a story could be built wrong or unsafe from the spine as written. **M** = a constraint is missing and a story would have to rediscover it. **L** = wording, tagging or completeness.

## H: high

### R1. The optimistic content check has no basis to compare against (AD-7 step 5)
- **Source:** findings l.30: "Add a content read-compare-write, for example a hash of the target OE or the page XML". Memlog l.16: "Concurrency guard must add content read-compare-write". Memlog l.56: objectIDs checked "on the page as read just before (read-compare-write)". CLAUDE.md: "Pass UTC dates and add a content read-compare-write."
- **Problem:** AD-7 reads current state in step 2 and then, in step 5, runs "a content compare of the targeted elements". It never says what that state is compared *with*. Comparing state the server read a moment earlier against itself proves nothing. The guard only works if the caller supplies its basis: the hash or XML of the targeted elements as the caller last read them, or the `lastModifiedTime` it saw. As written, it is easy to build a vacuous check, and on undisplayed pages the date check is vacuous too (findings rows 18 and 21).
- **Fix:** in AD-7 step 5, require write tools to accept the caller's basis: an `expectedHash` per targeted `objectID` (or for the whole page), returned by reads, plus the `lastModifiedTime` the caller saw. Mismatch → `StaleWrite`. Pin the hash definition (canonicalised element XML, algorithm) in Consistency Conventions. Say what happens when the caller omits it: refuse, or allow and flag as unguarded.

### R2. Reconnect contradicts "don't activate silently" (AD-3)
- **Source:** memlog l.25 ("drop the stale COM object, activate a new one, retry once"), l.28 ("Activation after OneNote exits or crashes starts a windowless ONENOTE.EXE -Embedding instance"), l.42 and l.71 (OneNoteNotRunning unless `allowSilentActivation`). Findings constraint 16 (l.48). CLAUDE.md: "Don't do that silently".
- **Problem:** AD-3 bullet 2 says that on `RPC_S_SERVER_UNAVAILABLE` / `RPC_E_DISCONNECTED` the gateway should "drop the reference, reactivate and retry once". The commonest cause of that HRESULT is that the user closed or killed OneNote, which is exactly the case where reactivating starts a hidden `-Embedding` instance. The two bullets conflict, and the reconnect bullet wins by default.
- **Fix:** AD-3: "Reactivation on reconnect goes through the same not-running gate: if ONENOTE.EXE is gone, return `OneNoteNotRunning` (unless `allowSilentActivation`)". Also say whether retrying a *write* after a disconnect is safe. A `CreateNewPage` / `UpdatePageContent` may have been applied before the disconnect, so either retry reads only, or re-read and compare before retrying a write.

### R3. Recovering from a hung call isn't defined, and it clashes with "one STA thread" (AD-2 / AD-3)
- **Source:** findings, Failure modes l.121: "two real COM calls hung … The 30 s timeout abandoned the worker both times and the runner continued". Memlog l.34 (hung-call timeouts logged, "worker abandoned"). Findings constraint 7 (l.39). CLAUDE.md: "every call needs a timeout".
- **Problem:** a timeout on an STA actor doesn't free the thread: the COM call is still blocked on it, and every queued call stays stuck behind it. The spike recovered by *abandoning the worker*. AD-2 fixes "exactly one ComGateway per process owns one STA thread and one COM reference", and AD-3 only says "every call has a timeout". Nothing says what the gateway does after a timeout, so the first hang could wedge the process for good.
- **Fix:** AD-3: "On a call timeout, the gateway returns `Timeout`, marks the STA thread poisoned, abandons it (background thread, never joined), and starts a fresh STA thread and COM reference for the next call. Queued calls fail fast with `Timeout` or move to the new thread." Reword AD-2 to "exactly one *live* STA thread at a time". Note that a write that timed out has an unknown outcome (`retryable: false`, userAction "check the page").

### R4. The HRESULT→code map isn't pinned, and several observed codes have no enum value (AD-9)
- **Source:** CLAUDE.md "Errors to map": `0x80042030` modal (retryable), `0x8004201b` locked, `0x8004200f` binary, `0x80042010` date mismatch. Findings HRESULT table l.134-148: also `0x80042014` hrObjectDoesNotExist, `0x80042019` hrInvalidQuery, `0x8004201a` hrFileAlreadyExists, `0x80080005` CO_E_SERVER_EXEC_FAILURE, `0x80010001` RPC_E_CALL_REJECTED. Findings l.150: "Take the names from that page: a list from memory had them shifted by one code from 0x80042010 onward."
- **Problem:** AD-9 lists the enum but only maps `0x8004200F`. The four codes CLAUDE.md requires to be mapped aren't tied to enum values, and `ModalDialogOpen`'s required `retryable: true` and userAction "close the OneNote dialog" (findings constraint 10) aren't stated. `hrInvalidQuery` (empty search) and `hrFileAlreadyExists` (Publish, which the permanent-delete backup depends on) have no fitting code and would fall into `Internal`. The warning about the shifted names isn't carried over, and that is exactly the error a story would make.
- **Fix:** add an HRESULT table to AD-9 (or as a companion) that binds `0x80042030→ModalDialogOpen (retryable)`, `0x8004201B→SectionLocked`, `0x8004200F→BinaryUnavailable`, `0x80042010→StaleWrite`, `0x80042014→UnknownObjectId`, `0x80080005→IntegrityMismatch` (or ComUnavailable), `0x800706BA`/`RPC_E_DISCONNECTED`→reconnect then `ComUnavailable`, and `RPC_E_CALL_REJECTED`/`RETRYLATER` once retries are used up→`ComUnavailable (retryable)`. Add `InvalidQuery` and `FileExists` to the enum, or map them to `InvalidArgument`. State: "names and codes come from learn.microsoft.com 'Error codes (OneNote)' (copy in `spikes/com-capabilities/OneNoteProbe.psm1`), never from memory."

### R5. The cloud-binary degrade keys only on an HRESULT, conflicts with AD-4, and undermines the journal
- **Source:** memlog l.19 and l.44: "GetBinaryPageContent 0x8004200f **and piBinaryData drops the Image**". Findings row 6: the call works but images are missing (0 images where piBasic counted 1-3). Findings l.37: report "binary unavailable" instead of failing.
- **Problem:** (a) AD-9 triggers the degrade on `BinaryUnavailable (0x8004200F)` only. On the `GetPageContent piBinaryData` path there is no HRESULT: the Image elements just vanish, so the read looks complete when it isn't. (b) AD-9 says to degrade "to a placeholder", while AD-4 says XML is returned "unchanged". They need reconciling. (c) AD-8 snapshots "full XML, binary included" before every write. On cloud notebooks the binary isn't local, so the snapshot silently has no images, and a `restore` (or recovery after a permanent delete) loses them without telling anyone.
- **Fix:** AD-4/AD-9: "Binary reads compare the image or ink count against a piBasic read, and report missing binaries in a result-level `binaryUnavailable: [objectID…]` list. The XML itself stays unchanged; no placeholder is injected into it." AD-8: "A snapshot records which binaries were unavailable; `restore` reports them." Optionally add: "A permanent delete is refused, or warned about, when the snapshot is missing binaries."

## M: medium

### R6. Recycle-bin restore: page IDs change and names are ambiguous
- **Source:** memlog l.20 and l.46. Findings constraint 6 (l.38): "a restore that resolves by name can be ambiguous … so it needs the user to choose". CLAUDE.md: "a soft-deleted page gets a new ID."
- **Problem:** AD-8 covers restoring from a *journal snapshot* only. It doesn't cover restoring a soft-deleted page from the recycle bin, and it doesn't say that journal metadata keyed by object ID (AD-8 bullet 2) won't match the page's new recycle-bin ID.
- **Fix:** AD-8: "Recycle-bin restore resolves the candidate by the user's explicit choice (elicitation or skill prompt) when names collide; never automatically by name. Journal lookup for a soft-deleted page uses the snapshot's stored ID, notebook and page title, not the page's current ID."

### R7. Locked sections should show as locked, not empty
- **Source:** memlog l.30: "locked sections list 0 pages … Report as 'unlock in OneNote', not as empty". Findings constraint 11 (l.43) and the Failure modes row (l.128): `GetHierarchy(section, hsPages)` works and returns nothing.
- **Problem:** AD-7 covers the write side (check `locked`). Nothing covers the read side: a hierarchy read or search over a locked section quietly returns zero pages, and "every result carries possiblyIncomplete" (AD-12) doesn't say why.
- **Fix:** AD-12 and a hierarchy-read note: "Results that include `locked="true"` sections list them in `lockedSections` and never present them as empty. The skill tells the user to unlock in OneNote."

### R8. `FindPages` `fDisplay`, and focus behaviour that wasn't measured
- **Source:** findings row 25a: "Not measured: `Publish`, `DeleteHierarchy`, `FindPages` with `fDisplay=true`". Constraint 9: never take focus unasked.
- **Problem:** AD-11 lists the known focus-takers but doesn't require `fDisplay=false` on `FindPages`/`FindMeta`. It also doesn't flag that `Publish`, which the AD-8 backup route depends on, and `DeleteHierarchy` weren't measured for focus.
- **Fix:** AD-12: "`FindPages`/`FindMeta` always pass `fDisplay=false`." Deferred: "Focus behaviour of `Publish` and `DeleteHierarchy`: measure in their stories, and add them to AD-11 if they take focus."

### R9. The 30 s call timeout vs the long `Publish` backup before a permanent delete
- **Source:** findings constraint 7: "a slow call (docx Publish, 2.9 s) blocks every other tool call" (a single page). Memlog l.64: a permanent delete of a section or notebook first backs up through Publish.
- **Problem:** publishing a whole notebook in OneNote format can easily run past the 30 s default (AD-3). The backup would then time out, the delete would be refused (AD-8), and the STA thread would be blocked for the whole time. No per-operation timeout exists.
- **Fix:** AD-3: "Timeouts are per operation class; `Publish` (backup) has its own, longer default." Note that a long backup blocks the queue.

### R10. Integrity check when OneNote isn't running at startup
- **Source:** memlog l.31-32 and l.40: "the server must detect elevation at startup". CLAUDE.md: "detect elevation at startup".
- **Problem:** AD-3 says "At startup, refuse to run if the server's integrity level differs from OneNote's". If OneNote isn't running at startup, there's nothing to compare against. A later OneNote launch at a different level (or `allowSilentActivation`) brings back the 30-57 s hang.
- **Fix:** AD-3: "At startup, refuse to run when elevated. Re-check OneNote's integrity level at each activation, and return `IntegrityMismatch` before calling CoCreate."

### R11. Grants are viewable, and the skill prompts for them
- **Source:** memlog l.54: the skill's job includes "prompting the user to allow notebooks not yet allowed". Memlog l.60: grants live "in a config file under %APPDATA% that the skill can show".
- **Problem:** AD-6 says how grants are *created*. It has no read-only way to *list* grants (a tool or resource), so the skill would have to read `settings.json` directly, and nothing ties `WriteNotGranted` to the skill's prompt-to-grant flow.
- **Fix:** AD-6: "A read-only `list_grants` tool or resource exposes the current grants. `WriteNotGranted` carries a userAction the skill turns into an elicitation request or a settings-file instruction."

### R12. A minimum date disables the date check (`DateTime.MinValue`)
- **Source:** findings U1 (l.232): "`MinValue` disables the date check".
- **Problem:** AD-4 says how dates are passed, but not that passing the zero or `MinValue` date disables the check. A use case could quietly drop concurrency protection.
- **Fix:** AD-7 step 5: "A guarded write never passes `MinValue`. Unguarded writes are explicit and logged." (U1 is ad hoc evidence, so label it observed, not logged.)

## L: low

### R13. Phill's adopted decisions are missing their [ADOPTED] tags
- **Source:** memlog l.71-75 (Activation, Timeouts, Settings, Logging, Error shape are all "[Phill 2026-11-03]") and l.66 (Concurrency).
- **Problem:** AD-3, AD-9, AD-10 and AD-13 have no `[ADOPTED]` tag, although the owner decided them. AD-12 is spike-derived, so it is untagged correctly unless Phill adopted it.
- **Fix:** tag AD-3, AD-9, AD-10 and AD-13 `[ADOPTED]`, or record why they are still open.

### R14. Hierarchy-write facts aren't carried
- **Source:** memlog l.37 and findings constraint 8: a partial Notebook XML in `UpdateHierarchy` adds sections and keeps siblings, and a rename can use the Section as the XML root. Rows 10-12: the section ID survives a rename.
- **Fix:** add a line to AD-4 or AD-7: "Hierarchy writes send partial XML. Siblings are preserved (logged), and IDs are stable across a rename." It belongs in a story if not here.

### R15. Images don't round-trip byte for byte
- **Source:** findings row 8: "the PNG is re-encoded, so the bytes don't round-trip".
- **Fix:** AD-8: "`restore` and the content compare must not compare binary bytes. Compare `objectID`s and text, or hash the canonicalised XML without `one:Data`." This also affects the hash definition in R1.

### R16. Settings re-read: wording drift
- **Source:** memlog l.73: "re-read on change". Memlog l.66: "grants are re-read at each write check".
- **Problem:** AD-10 says "re-read at each access check", which is stricter and consistent, but doesn't say whether timeouts and `allowSilentActivation` are also re-read live.
- **Fix:** one clause in AD-10 saying which settings apply live and which apply at restart.

### R17. A write retried after `RPC_E_CALL_REJECTED` or a reconnect isn't idempotent
- **Source:** memlog l.21 and l.34 (retry on CALL_REJECTED/RETRYLATER), l.25 (retry once on reconnect).
- **Fix:** AD-3: "Automatic retries apply to reads and to rejected-before-execution calls (CALL_REJECTED/RETRYLATER, which OneNote never ran). After a disconnect, re-read and compare before retrying a write." This overlaps R2.

### R18. Verified as landed (no action)
Early binding / `[ComImport]` + PIA fallback (AD-1). Single STA plus serialisation (AD-2). Timeouts 30 s / 10 s / 5 min (AD-3). Never kill OneNote (AD-3). Raw 2013 XML, `xs2013`, opaque IDs, binary excluded by default, UTC dates (AD-4). Thin tools and promoting tasks to use cases (AD-5). Read-all with per-notebook write grant plus a separate permanent-delete grant, user-only grants, owning notebook resolved through the gateway (AD-6). Pipeline order (AD-7). Gzipped journal with unique names, 30 days, restore without identity, Publish backup before permanent delete (AD-8). Error shape and enum (AD-9). Multi-process settings with a lock (AD-10). NavigateTo only on explicit ask (AD-11). Include-unindexed search, drop recycle-bin pages, metadata first, scope below root, `possiblyIncomplete` (AD-12). Logging (AD-13). Versioning (AD-14). Distribution (AD-15). Test seam, with integration writes guarded to the test notebook (AD-16). Deferred items match the memlog questions (move, Publish OneNote format, elicitation, plugin layout, signing, OneNote-elevated case, InsertedFile).
