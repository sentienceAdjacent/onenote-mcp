---
epic: epic-read-only-release
date: 2026-11-09
verdict: accepted-with-open-items
criteria: declared
headless: false
---

# Retrospective: epic 1, read-only release

## Epic summary

- **Tickets:** 21 of 21 done. 1.20 and 1.14 were marked done on 2026-11-09, by Phill. None is still at `built`, and none is unfinished.
- **Going-in concerns:** none (Phill).
- **Release under review:** `v0.8.0-rc.1`, tagged from `1efd814` (prerelease).
  - Release workflow run 42658950503 passed. It reran CI on the tagged commit.
  - The marketplace catalog pins the zip with SHA-256 `1dc90780…9279`, which matches the release sidecar.
- **Size:** 112 commits (34 merges) over `25a26fa..1efd814`. src/ and tests/ changed 194 files, net +30,273 lines.
- **Ranges:** one per plan, ordered by baseline. Per-range evidence is in the session scratchpad (`ev/*.json`, not committed).
- **Evidence inventory:**
  - Available: all 21 plans have a Verification section. Only 1.1 has a story file; every other ticket's intent is its `tickets.toml` entry.
  - Missing:
    - Vault session logs stop on 2026-11-06, so nothing covers 1.18–1.21, 1.13, 1.14 or the close-out. Raw transcripts exist (49 files) and weren't read.
    - Only plan 1.3 has a dated Code Review block.
    - Plans 1.12, 1.15 and 1.17 have no Plan Change Log.

## Findings

### Cross-story code review (bmad-review: adversarial, edge-case and verification-gap lenses, run inline)

I verified each finding against the code at `1efd814`.

| # | Finding | Source | Seam | Disposition |
|---|---|---|---|---|
| R1 | **`read_result` can keep serving a protected section's page names and IDs after its `hours` or `untilRevoked` grant ends.** It happens when the search started at that section, or at a page in it, and the result was big enough to be stored. `CheckBroad` records the grant with `WholeResult: false`, so `read_result` re-checks only the parts of the result that contain that section. The section is then the stored body's root (ref `/`, so its ID isn't registered) or isn't in the body at all. `Overlaps` gets `Find(sectionId) == null` and skips the check: it fails open. A broad result stored with no index (`ElementSpans.Text`) fails the same way. | `ReadAccess.cs:206-211`, `:227-248`, `:521-530`; `ElementSpans.cs:203-207`; `ResultDelivery.cs:85-92` | 1.18 × 1.20 × 1.9 | **fix now** |
| R2 | **The plugin hook lets the model run `OneNoteMcp uninstall --purge --yes`.** That deletes `settings.json` (every grant) and the logs, and later the journal. AD-6 says only the user changes settings. | `SettingsGuard.cs` (`GrantInvocation` matches only `grant`); no purge case in `SettingsGuardTests` | 1.20 × 1.13 | **fix now** |
| R3 | **A read refused because its grant ended says `retryable: true`.** A grant that ended by expiry stays ended, so retrying `read_result` can never succeed. A model that follows the flag loops. | `ToolErrors.cs:45-53` (`ReadNotGranted` → true); `ReadAccess.cs:516` | 1.3 × 1.20 | **fix now** |
| R4 | **Purge doesn't take the settings mutex, and doesn't detect a running server.** A grant saved by a still-running server after purge re-creates `settings.json`, and that server's in-memory `once` windows keep allowing reads. | `UninstallCommand.cs:155-158`; `SettingsWriter.cs:97` | 1.13 × 1.20 × 1.4 | defer (epic 2; the README already says to quit the clients first) |
| R5 | **The search cache keeps a page's text after its section becomes protected.** It's never served, and it lives until eviction or a restart. This assumes the page keeps its ID when the section gets a password, which wasn't checked. | `FindPagesUseCase.cs:80-100` (no `cache.Remove` for protected pages) | 1.19 × 1.20 | **fix now** (trivial) |

**Checked and clean:**
- Every target-scoped read runs the AD-21 check before reading content.
- Broad hierarchy and search withholding fail closed.
- Protected pages are never cached.
- `read_result` never prompts.
- Settings readers fail closed.
- The grant dialog never holds the settings mutex or a COM call.
- `navigate_to` doesn't retry.
- The hook packaging is pinned by tests.

### Aggregate views

**Architecture delta**

| # | Finding | Source | Disposition |
|---|---|---|---|
| A1 | Project references follow the spine: Core→Xml, Com→Core and Xml, Server→Core and Com. COM interop is `internal` to Com. | csproj files; `Com/Interop/*` | clean |
| A2 | AD-2 says no type but `ComGateway` references the interop types, but `StaThread` holds and releases `IApplication`. | `StaThread.cs:28,58,124-126` | reconcile the spine (the wording, not the code) |
| A3 | Server imports `OneNoteMcp.Xml` directly, through Core's reference, which the spine diagram doesn't draw. | `GrantCommand.cs:5,232-236` | reconcile the spine, or route the call through Core |
| A4 | Two namespace cycles inside Core: Access↔UseCases and Access↔Settings. Both come from story 1.20 and are inside one assembly. | `ReadAccess.cs:3-4`; `SettingsSnapshot.cs:114,156`; `SettingsStore.cs:204,243` | accept |

**Duplication**

| # | Finding | Source | Disposition |
|---|---|---|---|
| D1 | The xs:boolean `IsTrue` helper is written three times. | `LockedSections.cs:20`, `SearchResultFilter.cs:160`, `EncryptedSections.cs:55` | defer (epic 2's first refactor) |
| D2 | The "XML can't be read → `InvalidXml`" mapping is written seven times. `ListGrantsUseCase` bypasses `OneNoteXml.Parse`, and its message format differs. | `GetHierarchyUseCase.cs:84`, `ReadAccess.cs:462`, `GrantCommand.cs:234`, `SearchResults.cs:71`, `GetPageContentUseCase.cs:77`, `FindPagesUseCase.cs:151`, `ListGrantsUseCase.cs:83-92` | defer |
| D3 | Two parent-chain walks in one file. Section location is computed two ways: the CLI from XML, the dialog through the gateway. `ProtectedSectionGrant` is built in two places, with different sources for the allowed hours. | `ReadAccess.cs:255-281`, `:423-448`, `:376`; `EncryptedSections.cs:37-44`; `GrantCommand.cs:152-173`; `ElicitationGrantPrompt.cs:26` | defer |
| D4 | settings.json is parsed two ways: duplicate keys are handled differently, and the `expiresAt` parse is duplicated. | `SettingsStore.cs:71,198-212,277`; `SettingsWriter.cs:158,176` | defer |
| D5 | HRESULT `0x80042014` is declared twice in Core (and again in Com). | `NavigateToUseCase.cs:15`; `GetBinaryPageContentUseCase.cs:32` | defer |
| D6 | Nine separate `IOneNoteGateway` test doubles. | Listed in the aggregate-views report | accept (each is fit for its test) |

**Pattern divergence**

| # | Finding | Source | Disposition |
|---|---|---|---|
| P1 | Use-case constructors take three shapes. | `FindPagesUseCase.cs:25-45`; `ListGrantsUseCase.cs:19`; `ReadResultUseCase.cs:18` | accept |
| P2 | Private fields are named `_x` in Com and `x` elsewhere, with no `.editorconfig` rule behind either. | `ComGateway.cs` and `StaThread.cs` vs `ReadAccess.cs:94-99` | defer: add an `.editorconfig` rule |
| P3 | The XPSFile probe in `get_binary_page_content` swallows every `OneNoteException`, including `Timeout` and `Cancelled`, and rethrows the original `ObjectNotFound`. | `GetBinaryPageContentUseCase.cs:93` | defer (low) |
| P4 | Tool tests use three different harnesses. | `NavigateToToolTests.cs:117`; `SearchToolTests.cs:360`; ProtectedSectionKit | accept |

**Size**

| # | Finding | Source | Disposition |
|---|---|---|---|
| S1 | `ComGateway.cs` (656 lines, 5 commits) is large but single-purpose under AD-2 and AD-3. | `ComGateway.cs` | accept |
| S2 | `ReadAccess.cs` (531 lines) mixes stored-grant lookup, `once` memory, dialog orchestration, saving and two parent walks. | `ReadAccess.cs:80-472` | defer: split when epic 2 adds write grants beside it |
| S3 | `SettingsStore.Validate` is one ~138-line method, and each story added a block. | `SettingsStore.cs:93-231` | defer: split it per settings area |

**Spec against the as-built**

| # | Finding | Source | Disposition |
|---|---|---|---|
| C1 | Done-when 2 says the XML is "unchanged". AD-4, amended in 1.16, allows search removals, AD-21 withholding and AD-20 parts. | Epic file, Done when #2 | reconcile the epic wording |
| C2 | Done-when 2's basis is in `meta` for page reads only. AD-17 also defines a basis for sections, section groups and notebooks, and no hierarchy read returns one. | `GetPageContentUseCase.cs:42-43`; spine AD-17:380 | reconcile: say epic 1's basis is page-only, or give it to epic 2 |
| C3 | `navigate_to` was built as a general tool, which goes beyond the boundary exception's wording (fetching images). | Epic Boundaries; 1.21 plan:25-35 | reconcile the boundary wording |
| C4 | Done-when 3: the four hands-on AD-3 scenarios passed on 2026-11-04. They haven't been re-run since `ComGateway` changed (15fdb19, 77501a6). The hung-call scenario runs in every opt-in run. | `LifecycleScenarioTests.cs`; 1.5 plan:94 | open: re-run on the release, or accept the 11-04 run |

### Process

| # | Finding | Source | Disposition |
|---|---|---|---|
| Q1 | History was rewritten when the repo went public, so plans 1.1–1.11 cite baseline SHAs that `main` no longer reaches (they survive on `remotes/private/main`). Each was matched to a rewritten commit with an identical tree. | Inventory report | lesson; record the mapping |
| Q2 | Some triage summaries disagree with their own tables: 1.15 (2/6/3 vs 2/5/4), 1.16 (medium 5 vs 6), 1.20 pass 1 (medium 4 vs 5). 1.20 pass 3 logs 8 findings with no verdicts. | Those plans' triage logs | lesson |
| Q3 | Vault session logs (`/save`) stop on 2026-11-06, so the "why" for 1.18 onward lives only in raw transcripts. | `D:\ObsidianVaults\ClaudeVault\logs` | lesson |
| Q4 | Wins:<br>• The test notebook (MCP Test) and the stress harness gave byte-for-byte evidence every time.<br>• The Fable reviews caught real spine contradictions three times: 1.16, 1.20 and 1.14.<br>• The clean-machine run found README gaps no test could. | Plans 1.16, 1.20, 1.14 and 1.13 | keep |

## Behavior verification

Against the released `v0.8.0-rc.1` exe, downloaded from the release; its SHA-256 `4c61335b…be37` matches the sidecar:
- **Stress harness on MCP Test:** 620 comparisons, 0 mismatches, 2 `unstableRead` (the known table widths). 31 large results were stored and reassembled through `read_result`. The largest text block was 100,000 characters, and none was over the limit. `serverVersion` 0.8.0-rc.1; the oracle was built from `1efd814`.
- **Unit tests:** 1232 passed. **Integration:** 87 passed and 7 skipped, the 7 being the hands-on scenarios and fixture captures. Both ran on 2026-11-09, on the code that was tagged.
- **Install end to end:** the 1.13 clean run on Phill's work machine, 2026-11-09. It used `v0.7.0-rc.2`; nothing in the install path changed for `0.8.0-rc.1` apart from the version.
- **Not exercised:** the four hands-on AD-3 scenarios (C4), and R1 end to end. R1 is shown by reading the code, not by a live run.

## Previous-retro follow-through

None. This is the first epic.

## Action items

Proposed. Nothing has been applied. Phill decides which to run.

1. **R1 (fix before 1.0).** Make the stored-result grant check fail closed: a section that isn't in the stored tree counts as overlapping, and a search started at a granted section or one of its pages gets `WholeResult: true`. Add a test that stores a search started at a granted section, revokes the grant, then calls `read_result`. Owner: a new bug ticket (bmad-ticket), built with bmad-build.
2. **R2.** Have `SettingsGuard` deny `OneNoteMcp uninstall --purge` from the model, with a test. Owner: the same bug ticket, or one beside it.
3. **R3.** Mark a `ReadNotGranted` caused by an ended grant `retryable: false`, with a userAction that says to repeat the original call. Owner: the same ticket.
4. **R5.** Remove protected pages from the page-text cache. Owner: the same ticket.
5. **C4.** Re-run the four hands-on AD-3 scenarios on `0.8.0-rc.1` (Phill at the machine), or accept the 2026-11-04 run.
6. **C1–C3, A2 and A3.** Reconcile the epic's Done-when wording and the spine's AD-2 diagram and text, through bmad-correct-course plus a Fable review. Owner: Phill decides when.
7. **R4, D1–D5, P2, P3, S2 and S3.** Add them to `deferred-work.md` as an "epic 2 first refactor" group. Owner: me, on Phill's go.
8. **Q1–Q3 (process).** Record the old→new SHA mapping for plans 1.1–1.11, and don't rewrite history after a plan records its baseline. Keep each triage summary in line with its table. Run `/save` at the end of each session.

## Acceptance verdict

**accepted-with-open-items** (criteria declared in the epic file's Done when):
- **DW1, install from a release:** met on `v0.7.0-rc.2` (the 1.13 run). `v0.8.0-rc.1` is published and pinned with a matching SHA.
- **DW2, the envelope and XML for every R1 and R2 tool:** met, with the spine-approved exceptions (C1). The basis is page-only (C2).
- **DW3, AD-3 against real OneNote:** met on 2026-11-04. It hasn't been re-run on the release commit (C4).
- **DW4, CI on the release commit:** met (release run 42658950503).
- **DW5, the README agent checklist and purge:** met (the 1.13 run).

**The open items:**
- R1–R3 and R5 are real defects in the shipped release.
- R1 weakens AD-21. A grant you end doesn't stop a stored search result for up to `ttlMinutes`, 30 by default. It exposes page names and IDs, not page content, and only when the search started inside the granted section.

**Confirmed by Phill on 2026-11-09** (in the retro decisions review): accepted-with-open-items.

## Open questions

Phill answered all three on 2026-11-09, in `review/epic-1-retro-decisions.md`:
1. **R1, R2, R3 and R5:** one bug ticket now, before epic 2, then release `v0.8.0-rc.2`.
2. **C4:** the 2026-11-04 run of the four hands-on AD-3 scenarios is accepted as the evidence. They aren't re-run.
3. **R4, D1–D5, P2, P3, S2 and S3:** added to `deferred-work.md` as an "epic 2 first refactor" group. C1–C3, A2 and A3 (the spec and epic wording) go through bmad-correct-course later.
