---
title: 'Protection by path misses OneDrive cloud addresses'
type: 'bugfix'
ticket: '13'
created: '2026-11-18'
status: 'draft'
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

**Problem:** AD-21's path rule (entry 15) compares local paths only, so a section or notebook that OneNote reports by a OneDrive URL is never under a protected root and is read with no grant. 3.8's sitting S2 (2026-11-18) saw it live: after a restart, copies opened under the OneDrive-redirected default export root came back as `https://d.docs.live.net/<cid>/Documents/OneNote Exports/…` (new IDs, the cid's case varying, trailing slashes on notebooks and groups), and `get_hierarchy` at pages scope listed every page. On main since #89.

**Approach:** Teach `Protection.UnderRoot` the URL form of every protected root that lies inside a OneDrive folder, in the form Phill picks (Open Question 1), failing closed. Everything that decides "protected" already goes through `Protection`, so the one change covers reads, gated writes, journal snapshots, `publish` and the grant listing. A hotfix off main, merged before 3.8 (Phill, 2026-11-18, Q2).

## Boundaries & Constraints

**Always:**
- One place: `Protection` (`Core/Access/Protection.cs`) and the helpers it uses; the Xml layer keeps taking `UnderRoot` as its test.
- A URL is compared after percent-decoding, case-insensitively (the cid's hex case varies), with trailing `/` trimmed, at a path-segment boundary (`…/OneNote Exports2/…` doesn't match).
- Fails closed: when the rule can't tell (no OneDrive folder known, a URL it can't parse), a URL is not counted as unprotected if its decoded path would put it under a root by the chosen form.
- The OneDrive folders come from a seam (`OneDriveFolders`: local folder → URL prefix, or local folder only, per Open Question 1) that the server fills from the user's environment and tests fill by hand; no real cid or account name in any committed file.
- The local-path rule, `ExportTarget`, grant keys and the export-root guard are unchanged.

**Never:**
- No network calls; no reading OneNote's own files.
- No change to tool contracts, error codes or warnings.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error |
|---|---|---|---|
| Copy notebook by URL, pages scope | notebook `https://d.docs.live.net/<CID>/Documents/OneNote Exports/X/`, sections `…/<cid>/…/X/S.one`, a group `…/X/G/` | every section withheld, one warning each | none |
| Section by URL, pages scope | `…/OneNote Exports/X/S.one`, no grant | refused, as for any protected section | `ReadNotGranted` |
| Percent-encoded URL | `…/OneNote%20Exports/X/S.one` | protected | as above |
| Look-alike folder | `…/Documents/OneNote Exports2/X/S.one` | unprotected | none |
| URL elsewhere in the same OneDrive | `…/Documents/Work notes/P.one` | unprotected | none |
| A gated write on a URL section | soft delete of its page | refused as for a protected one | `NotSupported` |
| Root outside every OneDrive folder | a local root | local rule only, unchanged | — |
| Business OneDrive | the account's SharePoint personal-site address (host `<tenant>-my.sharepoint.com`, path `/personal/<user>/Documents/OneNote Exports/X/S.one`) | per Open Question 1 | — |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/Access/Protection.cs` -- `UnderRoot` (prefixes built once per call) is the single place; add the URL test beside the local one. `RootsOf` (export roots + journal `backups`). `From(SettingsSnapshot)` is used by `RestoreFromJournalUseCase.cs:241` and `Server/Cli/GrantCommand.cs:458`; `ReadAccess.cs` builds it at l.90, 101, 104, 132 (Q2's last-valid roots).
- `src/OneNoteMcp.Core/Writes/PathUnder.cs` -- `Full` returns null for `://` paths (the fail-open); local prefixes unchanged.
- `src/OneNoteMcp.Xml/EncryptedSections.cs` -- `IsProtectedByPath` tests the section's and the notebook's `path` through `underRoot`; no change needed once `UnderRoot` understands URLs.
- `src/OneNoteMcp.Core/Access/GrantKeys.cs:42` -- `SameLocation` already decodes percent-encoding for grant keys; reuse its decoding style.
- New seam: `src/OneNoteMcp.Core/Access/OneDriveFolders.cs` -- the user's OneDrive folders (and, for option a, their URL prefixes). Server side, `src/OneNoteMcp.Server/` fills it from `%OneDrive%`, `%OneDriveConsumer%`, `%OneDriveCommercial%` and `HKCU\Software\Microsoft\OneDrive\Accounts\<Personal|BusinessN>` (`UserFolder`; `cid` for Personal — value names verified on this machine 2026-11-18, values not read; Business value names unverified, that account isn't signed in). `ReadAccess`, `Protection.From` and `ServerSetup.cs` take it; unit tests pass a hand-made one.
- Tests: `tests/OneNoteMcp.Tests/OneDriveUrlProtectionTests.cs` (moved from 3.8's branch, commit 1e7f368: two tests run red then skipped, one guard test running) -- unskip and set the seam to the test's own OneDrive folder and the placeholder cid.

## Tasks & Acceptance

**Execution (after Phill answers Open Question 1):**
- [ ] `tests/OneNoteMcp.Tests/OneDriveUrlProtectionTests.cs` -- unskip the two leak tests, set the seam, add the matrix rows (percent-encoded, look-alike, a gated write, business form per Q1); run red.
- [ ] `tests/OneNoteMcp.Tests/OneDriveFoldersTests.cs` (new) -- the environment reader on a fake environment and registry view: env vars, `UserFolder`, `cid`, missing values.
- [ ] `src/OneNoteMcp.Core/Access/OneDriveFolders.cs` (new), `Protection.cs`, `ReadAccess.cs`, `UseCases/RestoreFromJournalUseCase.cs`, `src/OneNoteMcp.Server/Cli/GrantCommand.cs`, `src/OneNoteMcp.Server/ServerSetup.cs` -- the seam and the URL test.
- [ ] Spine AD-21 "What's protected" and the memlog -- the URL form, as Phill decides.
- [ ] `README.md` -- one line: protection by path also covers OneDrive addresses of those folders.

**Acceptance Criteria:**
- Given a copy notebook reported by URL under the default root's OneDrive location, when it is read at pages scope, then every section is withheld.
- Given a section reported by URL under that location, when it is read at pages scope with no grant, then it is `ReadNotGranted`.
- Given a URL elsewhere in the same OneDrive, when it is read, then it stays unprotected.
- Given the existing unit tests, when they run, then they pass unchanged.

## Open Questions

1. **How URLs are matched (an AD-21 change; Phill hasn't picked).**
   - (a) **Exact mapping.** For each protected root inside a OneDrive folder, compute its URL: personal `https://d.docs.live.net/<cid>/` + the root's path below `UserFolder` with `/` (from `HKCU\Software\Microsoft\OneDrive\Accounts\Personal`'s `UserFolder` and `cid`); business from the account's SharePoint address (host `<tenant>-my.sharepoint.com`, path `/personal/<user>/Documents/…`; recalled, unverified here). Precise, but if the registry is missing or an account changes, it fails open again unless paired with a fallback.
   - (b) **Fail closed by tail.** For a root inside a OneDrive folder (the env vars or any `UserFolder`), take its path below that folder (`Documents/OneNote Exports`); any `https` URL whose decoded path contains that tail at a segment boundary counts as under the root, whatever the host or cid. No registry values needed beyond the local folders; covers personal and business; over-protects only a same-named folder in another OneDrive (the safe direction).
   - (c) **Both:** (a) when the mapping is known, (b) as the fallback.
   - The coordinating session recommends (b) to Phill.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass, the two leak tests running.
- `dotnet test tests/OneNoteMcp.Integration` (no variables) -- builds, live tests skip.

**Sitting (main session with Phill):**
- S1: with the dev build, read a notebook that OneNote reports by a `d.docs.live.net` URL under the default export root (one of 3.8's copies after a restart, still open): its pages are withheld, and a section read asks for the grant. Phill closes the copies afterwards.
