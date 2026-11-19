---
title: 'Protection by path misses OneDrive cloud addresses'
type: 'bugfix'
ticket: '13'
created: '2026-11-18'
status: 'built'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/story-protected-by-path-export-roots-and-the-backups-folder-plan.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** AD-21's path rule (entry 15) compares local paths only (`PathUnder.Full` returns null for `://`), so a section or notebook that OneNote reports by a OneDrive URL is never under a protected root and is read with no grant. 3.8's sitting S2 (2026-11-18) saw it live: after a OneNote restart, copies opened under the OneDrive-redirected default export root (`…\OneDrive\Documents\OneNote Exports\<copy>`), protected while their paths were local, came back as `https://d.docs.live.net/<cid>/Documents/OneNote Exports/<copy>/` (notebook and section groups with a trailing `/`, sections `…/<copy>/New Section 1.one`, new notebook IDs, the cid's hex case varying between elements), and `get_hierarchy` of the notebook at pages scope listed every page. On main since #89.

**Approach:** Teach `Protection.UnderRoot` the URL form of every protected root that lies inside a OneDrive folder, by Phill's decision (Decisions, D1), failing closed. Everything that decides "protected" already goes through `Protection`, so the one change covers reads, gated writes, journal snapshots, `publish` and the grant listing. A hotfix off main, merged before 3.8 (Decisions, D2).

## Boundaries & Constraints

**Always:**
- One place: `Protection` (`Core/Access/Protection.cs`) and the helpers it uses; the Xml layer keeps taking `UnderRoot` as its test.
- A URL is compared with its query and fragment dropped, percent-decoded (an invalid escape is left as it is), case-insensitively (the cid's case varies), with trailing `/` trimmed, at a path-segment boundary, strictly inside (a URL equal to a root's URL form isn't under it, as for local paths). `http` and `https` alike. A `%2F` that decodes into a new segment boundary may over-protect: the safe direction.
- Fails closed: a URL counts as protected whenever it matches a root's URL form or holds a root's tail, by the rules in D1 and Open Questions 1–3.
- "Inside a OneDrive folder": each of a root's two forms (`PathUnder.Prefixes`: plain full path and canonical) is tested against each known OneDrive folder at a segment boundary, case-insensitively; `…\OneDrive - Contoso` is not inside `…\OneDrive`.
- The OneDrive folders come from a seam, `OneDriveFolders` (each account's local folder, with its URL prefix when known). The server reads it from the user's environment; unit tests build it by hand. No real cid or account name in any committed file.
- The local-path rule, `ExportTarget` and grant keys are unchanged. A grant stays keyed on the raw path (AD-21): a section reported once with `%20` and once with a space is protected both times and granted for one spelling only (fails closed).

**Never:**
- No network calls; no reading OneNote's own files; no registry values written.
- No change to tool contracts, error codes or warnings.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error |
|---|---|---|---|
| Copy notebook by URL, pages scope | notebook `https://d.docs.live.net/<CID>/Documents/OneNote Exports/X/`, sections `…/<cid>/…/X/S.one`, a group `…/X/G/` | every section in `meta.withheld`, its pages out of the XML, one `readNotGranted` warning per section (as entry 15) | none |
| Section by URL, pages scope | `…/OneNote Exports/X/S.one`, no grant | refused, as for any protected section | `ReadNotGranted` |
| Percent-encoded URL | `…/OneNote%20Exports/X/S.one` | protected | as above |
| URL with a query or fragment | `…/X/S.one?web=1` | protected | as above |
| Look-alike folder | `…/Documents/OneNote Exports2/X/S.one` | unprotected | none |
| URL elsewhere in the same OneDrive | `…/Documents/Work notes/P.one` | unprotected | none |
| A gated write on a URL section | soft delete of its page | refused as for a protected one | `NotSupported` |
| Journal snapshot / `publish` / grant listing | a URL section under the root | stripped / needs its read grant / listed | as entry 15 |
| Root outside every OneDrive folder | a local root | local rule only, unchanged | — |
| Business OneDrive | host `<tenant>-my.sharepoint.com`, path `/personal/<user>/Documents/OneNote Exports/X/S.one` | protected by the tail | as above |
| Personal account unreadable | no `cid`; `%OneDrive%` set | protected by the tail | as above |
| No OneDrive folder known | neither registry nor `%OneDrive*%` | per Open Question 2 | — |
| Root equal to a OneDrive folder, or one segment below it | `…\OneDrive`, `…\OneDrive\Exports` | per Open Question 3 | — |

## Decisions (Phill, 2026-11-18)

- **D2 (his Q2 on the bug):** a hotfix off main, merged before 3.8.
- **D1 (his Q1 on the bug), (c): an exact mapping when the account can be read, the fail-closed tail as the fallback.**
  - *Accounts:* the OneDrive folders are combined from every `HKCU\Software\Microsoft\OneDrive\Accounts\<Personal|BusinessN>` key that has a `UserFolder` and from `%OneDrive%`, `%OneDriveConsumer%` and `%OneDriveCommercial%`, de-duplicated (a business folder known only from the environment still counts). The personal account's URL prefix is `https://d.docs.live.net/<cid>/` from its `cid`. A business account gets no exact prefix (its value names are unverified here; only the `Personal` key's `UserFolder`, `UserEmail` and `cid` were seen, names only); the tail covers it.
  - *Exact:* for a root inside an account's folder, its URL form is that account's prefix plus the root's path below the folder, with `/`.
  - *Tail:* for a root inside any known OneDrive folder, its tail is its path below that folder (`Documents/OneNote Exports`); a URL holding that tail as whole segments, followed by `/`, counts as under the root, whatever the host or cid. It may over-protect a same-named folder in another OneDrive (the safe direction).
  - When each applies: Open Question 1.
- **Answers to Open Questions 1–4 (Phill, 2026-11-18, each as recommended):**
  - *Q1 (ii):* the tail always applies, alongside the exact address; the exact mapping protects nothing the tail doesn't, and is kept to map a URL notebook back to its local folder (Q4).
  - *Q2 (ii):* with no OneDrive folder known (or none passed in), a root's own last two segments (below the drive) are its tail, for any `http`/`https` URL.
  - *Q3 (ii):* an export root that is a OneDrive folder, or one segment below one, is refused like entry 15's guard does it, saying why: the root keeps protecting, reads carry `settingsInvalid` naming it, and writes fail with `SettingsInvalid` until the user changes it. (First built as "the whole file is invalid"; the review showed that drops the root at startup and fails open, so it follows the guard's pattern, as the question's own wording said.)
  - *Q4 (i):* the export-root guard covers URL notebooks: one under a root by this rule is reported unless its local folder, mapped back through the account's address (or, without one, the root's tail), holds the `.onenote-mcp-opened` marker; one whose mapped local folder holds a root is reported too.

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/Access/Protection.cs` -- prefixes are built in the constructor (l.32–33), `UnderRoot` (l.55) delegates to `PathUnder`; the URL test goes beside it, built once per `Protection` from the seam. `RootsOf` (export roots + journal `backups`); `From(SettingsSnapshot)` used by `UseCases/RestoreFromJournalUseCase.cs:241` and `src/OneNoteMcp.Server/Cli/GrantCommand.cs:458`; `ReadAccess.cs` builds it at l.90, 101, 104, 132 (entry 15's last-valid roots).
- `src/OneNoteMcp.Core/Writes/PathUnder.cs` -- `Full` returns null for `://` (l.74); `Prefixes` (l.51–65); local behaviour unchanged.
- `src/OneNoteMcp.Xml/EncryptedSections.cs` -- `IsProtectedByPath` tests the section's and the notebook's `path` through `underRoot`; no change.
- `src/OneNoteMcp.Core/Access/GrantKeys.cs:42` -- `SameLocation`'s decoding is the style to follow; it "never decides a read" (l.39–40), so the URL test is new code, not a call to it.
- `src/OneNoteMcp.Core/Access/ExportRootGuard.cs` -- skips URL notebooks today (l.41–42, 48–51; pinned by `tests/OneNoteMcp.Tests/ExportRootGuardTests.cs:59`); per Open Question 4.
- New: `src/OneNoteMcp.Core/Access/OneDriveFolders.cs` -- the seam (accounts: local folder, optional URL prefix); `OneDriveFolders.None` for code that knows none. New: `src/OneNoteMcp.Server/OneDriveAccounts.cs` -- reads the registry (names `UserFolder`, `cid` under `HKCU\Software\Microsoft\OneDrive\Accounts\*`, via `Microsoft.Win32.Registry`, as `src/OneNoteMcp.Com/IProcessProbe.cs` already uses it) and the three environment variables through an `IOneDriveSource` interface the tests fake; read once per `Protection` build and cached for 30 s (as the export-root guard caches), so an account signed in later is known within 30 s.
- Q3: `ExportRootGuard.ShallowProblem` (no COM read): `ReadAccess.ProtectionAsync` turns it into a `settingsInvalid` warning, and the guard's `ProblemAsync` returns it, so `WritePipeline` step 1 refuses writes with `SettingsInvalid` as for entry 15's guard.
- Wiring: `ReadAccess` takes the seam as an optional constructor argument, `Protection.From(settings, folders)` likewise; an omitted seam reads this process's environment (`OneDriveAccounts.FromEnvironment()` passed in by Server; Core's default is `OneDriveFolders.None` only where Server can't reach, so `ServerSetup.cs`, `src/OneNoteMcp.Server/Program.cs` l.38–44 (the grant CLI, built outside DI) and `GrantCommand`'s constructor pass the server's reader). Existing tests keep compiling and see no OneDrive folders.
- Tests: `tests/OneNoteMcp.Tests/OneDriveUrlProtectionTests.cs` (in this branch since b5e2d5b, moved from 3.8's 1e7f368): two leak tests run red then skipped, one guard test running.
- Docs to amend: spine AD-21 "What's protected" (l.≈696), AD-11's "One 'under a root' test …" (l.507) and "an open local notebook" (l.508); doc comments `PathUnder.cs` l.4–10 and `Protection.cs` l.54; entry 15's plan (its "Cloud section | URL path | marker only" row and l.88's "comes back for a change") gets a note pointing here.

## Tasks & Acceptance

**Execution (in this order):**
- [ ] `src/OneNoteMcp.Core/Access/OneDriveFolders.cs` (new) -- the seam's types only, so the tests compile.
- [ ] `tests/OneNoteMcp.Tests/OneDriveUrlProtectionTests.cs` -- unskip the two leak tests, give them a hand-made seam (the test's own OneDrive folder, the placeholder cid); add a test per matrix row; run red.
- [ ] `tests/OneNoteMcp.Tests/OneDriveAccountsTests.cs` (new) -- the reader on a fake `IOneDriveSource`: registry `UserFolder` and `cid`, environment only, both combined, missing values; run red.
- [ ] `src/OneNoteMcp.Core/Access/Protection.cs`, `src/OneNoteMcp.Core/Writes/PathUnder.cs` -- the URL test (exact and tail).
- [ ] `src/OneNoteMcp.Server/OneDriveAccounts.cs` (new) -- the reader and its cache.
- [ ] `src/OneNoteMcp.Core/Access/ReadAccess.cs`, `UseCases/RestoreFromJournalUseCase.cs`, `src/OneNoteMcp.Server/ServerSetup.cs`, `src/OneNoteMcp.Server/Program.cs`, `src/OneNoteMcp.Server/Cli/GrantCommand.cs` -- the wiring.
- [ ] Per Open Question 4: `src/OneNoteMcp.Core/Access/ExportRootGuard.cs` and its tests.
- [ ] `_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md` (the lines in the Code Map) and `.memlog.md` beside it -- D1, D2 and the answers to Open Questions 1–4; the spine amendment goes through its own Fable final review before it is final. Entry 15's plan -- a note.
- [ ] `README.md` -- one line: protection by path also covers OneDrive addresses of those folders.

**Acceptance Criteria:**
- Given a copy notebook reported by URL under the default root's OneDrive location, when it is read at pages scope, then every section is withheld, its pages are out of the XML, and each has a `readNotGranted` warning.
- Given a section reported by URL under that location, when it is read at pages scope with no grant, then it is `ReadNotGranted`.
- Given the same section spelled with `%20`, with a query, or with an upper-case cid, when it is read, then it is refused the same way.
- Given a URL under `…/OneNote Exports2/` or elsewhere in the same OneDrive, when it is read, then it stays unprotected.
- Given a soft delete of a page in a URL section under the root, when it is called, then it is refused as for a protected section; and given a journal snapshot of the copy, a `publish` of one of its pages, and the grant CLI's listing, then each treats the URL section as protected.
- Given a business-style URL (host `<tenant>-my.sharepoint.com`, path `/personal/<user>/…`) under the root's tail, when it is read, then it is protected.
- Given no readable personal `cid` but `%OneDrive%` set, when a URL under the root is read, then it is protected by the tail.
- Given the existing unit tests, when they run, then they pass unchanged.

## Open Questions

All four answered (Decisions, 2026-11-18); kept as asked.

1. **When does the tail apply?** D1 says "exact when the account can be read, the tail as the fallback". (i) *Either/or:* the tail only when no exact prefix is known. Precise, but if the registry's `cid` and the URL's ever differ (another account, a stale key), a URL under the root fails open. (ii) *Always both:* the tail always applies, so the exact mapping protects nothing the tail doesn't; it stays as a record of the account and for the sitting's checks. Fails closed in every case. Recommended: (ii).
2. **No OneDrive folder known** (neither registry nor `%OneDrive*%`, while OneNote reports URLs). (i) No URL rule (as drafted): the S2 leak stays possible on such a machine. (ii) Fail closed on the root's own last two segments (`Documents/OneNote Exports`) for any `https` URL. Over-protects a same-named folder in any cloud notebook. (iii) Any `https` URL counts as protected when a root's local path holds a segment starting `OneDrive`. Recommended: (ii).
3. **A root equal to a OneDrive folder, or one segment below it.** An empty tail would make every `https` URL protected; a one-segment tail (`Exports`) matches that folder name anywhere. (i) Allow it: an empty tail protects every URL of that account's host (exact prefix) or, with no prefix, every `https` URL; a one-segment tail as written. (ii) Refuse such an export root as `settings.json` invalid, saying why, like entry 15's guard. Recommended: (ii).
4. **The export-root guard and URL notebooks.** After this fix a root can protect the user's own cloud notebooks (a root at `…\OneDrive\Documents`, or the tail matching another OneDrive), and the guard, which skips URL notebooks, gives no `exportRootHoldsNotebooks` warning. And 3.8's copies, re-homed as URL notebooks after a restart, can't carry the marker check (no local folder). (i) Extend the guard to URL notebooks by the same rule; a URL notebook whose local folder (mapped back through the account) holds the marker is exempt, any other is reported. (ii) Leave the guard local-only and accept the silent over-protection. Recommended: (i).

## Implementation Notes

- 2026-11-18, built test-first: the two S2 leak tests unskipped with the seam, plus 26 rule, seam, settings and guard tests (`OneDriveUrlProtectionTests`) and 7 reader tests (`OneDriveAccountsTests`), run red (33 failing, only the new ones), then green.
- `Core/Writes/UrlPath.cs` (new): parses `http`/`https` paths into host and decoded segments; `IndexOfTail` (whole segments, case-insensitive, strictly inside); `Below` (maps back, refusing a decoded `..` that leaves the folder).
- `Core/Access/OneDriveFolders.cs` (new): the seam; de-duplicates by folder, keeps the first address; `LocalPathOf` (exact mapping back); `ShallowRoot` (Q3).
- `Protection`: each root's tails built once per value (from every root form against every OneDrive folder form; with none known, the last two segments); `UnderRoot` = local rule OR tail; `LocalPathOfUrl`; `WithWarnings` keeps the seam.
- `ExportRootGuard.Problem`: URL notebooks under a root are a problem unless the folder they map back to (account address first, else the root's tail) is under the root and holds the marker; a URL notebook mapped back through its account that holds a root is a problem.
- Q3 (after review F1): `ExportRootGuard.ShallowProblem`; reads warn `settingsInvalid`, the guard refuses writes, the root keeps protecting. `SettingsStore` is unchanged.
- `Server/OneDriveAccounts.cs` (new): reads `HKCU\Software\Microsoft\OneDrive\Accounts\*` (`UserFolder`, `cid`, read only) and the three variables; a registry failure leaves the environment; 30 s cache. Wired in `ServerSetup` (`ReadAccess`, the guard, `restore_from_journal`) and `Program.cs` (`GrantCommand` takes the seam and passes it to `Protection.From`).
- After review: `UrlPath.Readings` (query/fragment cut or kept, decoded or not) so a `#` or `%` in a folder's name can't fail open (F2); `Protection` makes each root's canonical forms once (F4).
- Docs: spine AD-11 (l.507–508) and AD-21 "What's protected" (new "Web addresses" bullet), memlog, entry 15's plan row, README, `PathUnder` and `Protection` doc comments. The spine amendment still needs its Fable final review.

## Plan Change Log

- 2026-11-18, Phill answered Open Questions 1–4 (all as recommended); recorded under Decisions; status ready-for-dev, then in-progress.

- 2026-11-18, Phill's Q1 answer (c) recorded as D1, D2 named; then the Fable final review at low effort (24 findings, each checked against the sources; all held): the frozen block now states the URL normalisation (query, fragment, invalid escapes, `http`, strictly inside, `%2F`), how "inside a OneDrive folder" is tested, that sources are combined, the raw-key grant note, and which warnings the copy row means; the matrix gains the query, snapshot/publish/listing and edge rows; the Code Map names the reader file, the cache, the optional seam and its default, `Program.cs`, the guard, every doc line to amend, and corrected citations and provenance; tasks are ordered so the tests compile red and name their files; ACs cover the matrix and the ticket; the sitting has a fallback and an over-reach check; four questions for Phill (when the tail applies, no OneDrive known, a shallow root, the guard).

## Review Triage Log

- 2026-11-18, Fable final review of the spine amendment (low effort, read-only, `bmad-review`; 15 findings, each checked against the spine, plan and code; all held). Applied: the restart exception in "Path stability" and in a grant's key (F1), grants stay keyed on the reported path (F2), AD-10's `SettingsInvalid` line (F3), how broadly a shallow root protects (F4), fewer than two segments (F5), which guard branch works by address (F6), the shallow check runs first (F7), where the folders and the address come from (F8), the normalisation details (F9), l.508 reworded (F12), the bullet split into sub-bullets (F13), each answer's letter (F14), and the memlog labels (F15). For Phill: F10 (the journal's backups folder isn't covered by the shallow-root check) and F11 (an open-measurements row: the address form rests on one observation).

- 2026-11-18, one adversarial/edge-case reviewer on d675222 (findings checked against the code):
  - F1 (high, fixed): a shallow root made `settings.json` invalid, so at startup the fallback dropped that root and a custom journal's backups folder: fails open. Now the guard's pattern (Decisions, Q3). Test `AShallowExportRoot_KeepsProtecting_…`.
  - F2 (medium, fixed): cutting at `#` before matching lost a folder named `#Work`, or a root holding `#`. Every reading is tested now. Tests `AFolderNameHoldingAHash…`, `ARootWhoseOwnNameHoldsAHash…`.
  - F3 (low, deferred): the soft-delete, snapshot, publish and listing rows are tested at the seam (`ReadAccess.Protection()`, `Protection.From`), not end to end, and the DI wiring has no test; every call site was read and passes the seam. The sitting covers it live.
  - F4 (low, fixed): canonical forms were made twice per root per `Protection`.
  - Residual, by design (matrix): with a OneDrive folder known, a root not recognised as inside one (a `subst` drive, a stale folder path) keeps the local rule only.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass, the two leak tests running.
- `dotnet test tests/OneNoteMcp.Integration` (no variables) -- builds, live tests skip.

**Sitting (main session with Phill):**
- S1: with the dev build, read a notebook OneNote reports by a `d.docs.live.net` URL under the default export root: one of 3.8's copies after a restart if still open; otherwise make one (`open_package` with no destination, then restart OneNote, which re-homes it). Its pages are withheld, and a section read asks for the grant. Then read a cloud notebook elsewhere in the same OneDrive (MCP Test Cloud): still readable. Phill closes the copies.
- When this is approved: set the plan's `status` and the ticket's `refined`.
