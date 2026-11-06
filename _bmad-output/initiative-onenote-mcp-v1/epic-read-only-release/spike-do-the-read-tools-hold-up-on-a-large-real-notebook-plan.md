---
title: 'Spike: do the read tools hold up on a large real notebook?'
type: 'chore'
ticket: '15'
created: '2026-11-05'
status: 'in-review'
route: 'full'
route_source: 'auto'
baseline_revision: '85ddad7d6066fa3e8241cf0c7f14d1320c2b3e14'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/CLAUDE.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The nine read tools have only met the small MCP Test notebook. We don't know whether they stay accurate, fast and small enough on a large real work notebook, or what Claude Code does with a result past its MCP output cap (`MAX_MCP_OUTPUT_TOKENS`, about 25k tokens).

**Approach:** Build a harness, `spikes/stress-harness/`. It drives the real `OneNoteMcp.exe` over stdio as an MCP client and compares every result with the same COM read made through `ComGateway`. It records only IDs, sizes, timings, counts, codes and pass/fail, never content. A separate probe measures Claude Code's output cap with a fake server that returns filler text. Phill runs the harness on the notebook he picks. The findings doc then reports aggregates, with a recommendation for each limit hit.

## Boundaries & Constraints

**Always:**
- Read-only. The harness calls only `ComGateway`'s public read methods, which are the only public COM surface in epic 1, plus the MCP tools. Before any call it aborts unless `tools/list` is exactly the nine contract tools, each with `readOnlyHint: true`.
- No content anywhere the harness writes, on stdout, stderr or files. That means no text, titles, names, Meta names or contents, query words, paths or exception messages. It writes only:
  - IDs;
  - numbers;
  - tool and error codes;
  - HRESULTs;
  - exception type names.
- The notebook is chosen by `--notebook <ID>` or `--notebook-name <nickname>`. A name is resolved inside the harness and never echoed. There's no default, and no option lists notebooks.
- `--out` must resolve outside the repo. The harness refuses otherwise.
- On a mismatch, read once more, raw then tool. If the two raw reads differ, classify it `changedDuringRun` (Phill may be editing), not `mismatch`.
- The agent runs the harness only against MCP Test. Phill starts the real-notebook run.

**Never:**
- No `NavigateTo` or any UI call. No write. Never close, kill or start OneNote.
- Never read harness output from a real-notebook run into the model unless it's the content-free summary. The JSONL stays outside the repo.
- No changes under `src/` or `tests/`, and no new product behaviour. Recommendations become tickets later.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Happy path | `--notebook-name "MCP Test" --out <temp>` | `run.jsonl` and `summary.json` hold numbers and IDs only; mismatches 0 | — |
| No notebook | no `--notebook*` | exit 2, usage message | refuse |
| Name not found or ambiguous | unknown nickname | exit 2, "no single notebook matched", without the name | refuse |
| Out inside repo | `--out ./x` | exit 2 | refuse |
| Unexpected tool list | exe exposes a tool outside the contract, or one without readOnlyHint | exit 3 before any read | abort |
| Tool error | e.g. `SectionLocked` | recorded as `{ok:false, code}`; run continues | count by code |
| Concurrent edit | raw read 1 ≠ raw read 2 | `changedDuringRun` | not a mismatch |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Com/ComGateway.cs` -- the oracle. It has public `GetHierarchyAsync`, `GetHierarchyParentAsync`, `GetPageContentAsync(id, PageInfo)`, `GetBinaryPageContentAsync`, `FindPagesAsync(start, query, includeUnindexed)`, `FindMetaAsync` and the link and special-location methods. Construct it with `new ComGateway(new ProcessProbe(), settingsStore)`, as the integration tests do (`tests/OneNoteMcp.Integration/HierarchyToolsIntegrationTests.cs`). Use a temp settings dir.
- `src/OneNoteMcp.Core/UseCases/GetPageContentUseCase.cs:26-34` -- the tool reads `PageInfo.Basic` when `includeBinary` is false, else `PageInfo.BinaryData`. Compare against the same.
- `src/OneNoteMcp.Core/Settings/SettingsSnapshot.cs:22` -- `Timeouts.Default`: Call 30 s. Report each tool's timings against it.
- `FindPagesUseCase` and `SearchResults` -- the server's filter rules (recycle-bin by flag or ancestor; every term in name or `one:T` text, ignoring case; Meta name ordinal). The harness predicts independently:
  - find_pages: from the Basic page XML, with HTML stripped and entities decoded;
  - find_meta: from each page's own `one:Meta` elements, not the search result.
- `tests/OneNoteMcp.Tests/Contract/tool-contract.json` -- the nine tool names for the startup check.
- `spikes/Directory.Build.props` and `spikes/Directory.Packages.props` -- spike projects opt out of root build settings and central package versions. Pin `ModelContextProtocol` 2.3.1 (its client: `McpClient`, `StdioClientTransport`). Keep the harness out of `OneNoteMcp.slnx`.
- `scripts/pack-plugin.ps1` -- writes `dist/publish/OneNoteMcp.exe`, the harness's default `--exe`.

## Tasks & Acceptance

**Execution:**
- [x] `spikes/stress-harness/StressHarness.csproj`, `Program.cs` (and a few files) -- the harness:
  - **Startup:** argument checks and the tool-list check.
  - **Phase hierarchy:** the notebook at self, children, sections and pages; the root at notebooks and, with `--root-pages`, at pages. Compare bytes and the Section and Page ID sets.
  - **Phase pages:** every page that isn't in the recycle bin, `includeBinary` false against Basic. Pages with `one:Image` or `one:InsertedFile` also get `includeBinary` true against BinaryData. Record the image, attachment and `one:Data` byte counts.
  - **Phase binaries:** each CallbackID (`--max-binaries`, default 200) against raw, recording size and binaryUnavailable.
  - **Phase search:** `--queries` (default 20) seeded-random 1–2-word queries taken from page text, with the words kept in memory only; each tool result set against the prediction. Up to 10 distinct Meta names go through find_meta.
  - **Phase misc:** parent, both links and special location, on a sample of 20.
  - **Per call:** response bytes (the JSON-RPC `result` serialised), ms, ok and code, warning codes, match.
  - **Output:** `run.jsonl` and `summary.json` (counts; p50/p95/max bytes and ms per tool; top 10 sizes by tool; counts over 25k tokens estimated at bytes/4 and bytes/3; calls over 50% and 100% of the 30 s call timeout; errors by code; mismatches by phase). Progress goes to stderr as counts. -- the measuring tool.
- [x] `spikes/stress-harness/probe-output-cap.ps1` and the harness's `--fake-server <chars>` mode -- the cap probe. A one-tool stdio MCP server returns `<chars>` of filler. The script runs `claude -p --mcp-config` for sizes 20k, 40k, 80k, 100k, 120k and 200k characters and records, from stream-json, whether the result reached the model whole, truncated or as an error, and any message Claude Code adds. -- answers the cap unknown without any notebook.
- [x] `spikes/stress-harness/README.md` -- how Phill runs it, what it never records, and where the output goes.
- [x] Dry run on MCP Test plus the cap probe. Their numbers go in the findings doc's "Harness validation" and "Client cap" sections.
- [ ] hitl: Phill runs the harness on his chosen notebook and shares `summary.json`. The agent writes `spike-do-the-read-tools-hold-up-on-a-large-real-notebook-findings.md`: accuracy, limits, and a recommendation per limit hit, targeting 1.13, 1.14 or a new story. Phill reviews it for content before it's committed.

**Acceptance Criteria:**
- Given MCP Test, when the harness runs, then every matrix row behaves as listed, and `summary.json` reports 0 mismatches across all nine tools.
- Given any harness output file, when it is searched for MCP Test's section, page and Meta names, then none appears.
- Given the cap probe, when it finishes, then the findings doc states the size at which Claude Code first truncates or rejects a result, and what the model sees.

## Design Notes

Assumptions, made while Phill was away:
- The oracle is `ComGateway`, which makes the same COM call as the server with no tool, envelope, JSON or process in between. A pwsh or IDispatch raw call isn't possible, because late binding fails (CLAUDE.md). So byte equality proves the tool path changes nothing beyond the adapter.
- Search queries are drawn from page text so that they hit, and stay in memory. A query that matches nothing is still a valid test (an empty set).
- The output cap is probed with filler, not notebook content, so the agent can run it unattended.

## Verification

**Commands:**
- `dotnet build spikes/stress-harness -c Release` -- expected: success.
- `dotnet run --project spikes/stress-harness -c Release -- --notebook-name "MCP Test" --out <scratch>` -- expected: exit 0; mismatches 0.
- Each refusal row: exit 2 or 3 as listed.
- `pwsh -File spikes/stress-harness/probe-output-cap.ps1 -Out <scratch>` -- expected: one row per size.

**Manual checks (if no CLI):**
- Grep the MCP Test run's output for MCP Test's section names ("Seed", "Fixtures") and its fixture Meta name. Expected: no hits.

## Review Triage Log

Pass 1, quick lens: 11 findings, counting the small items as one.
- Verdicts: 2 high, 6 medium, 3 low.
- Routes: 10 patch, 1 rejected.

| # | Finding | Verdict | Route | Evidence / action |
| --- | --- | --- | --- | --- |
| 1 | Search `unknown > 0` relabels every result as changedDuringRun | high | patch | `Run.cs:338,434`: the assignment has no condition. A `match` is never relabelled now, and a mismatch only when its disagreeing IDs are all unknown or proven changed. |
| 2 | `AnyChangedAsync` treats a missing prior hash as a change | high | patch | `Run.cs:471`: recycle-bin pages have no `_facts`, so a filter bug was hidden. A missing hash now means no evidence of change. |
| 3 | Matrix rows "Tool error", "Concurrent edit" and "ambiguous" never ran | medium | patch | The findings doc says so. A negative-ID probe was added; the other two can't be produced read-only and are recorded as checked by reading the code. |
| 4 | Timeout metrics use the tool round-trip, not the per-COM-call time | medium | patch | `Timeouts.Call` is applied per COM call (`ComGateway.cs:324`). Moved to `rawMs`. |
| 5 | Client-cap conclusions are unsupported (structuredContent, tokens versus characters, arithmetic) | medium | patch | The fake server returned text only. Added a structuredContent variant and a second filler, and the claims were rewritten. |
| 6 | A retried record mixes two attempts | medium | patch | In `CompareAsync`, `rec` came from the first attempt. It now records the second, flagged `retried`. |
| 7 | The oracle and the server may be different builds | medium | patch | Nothing checked this. `summary.json` now records the server's version, the exe hash and time, and the Com assembly version. |
| 8 | The find_pages prediction shares the server's rule implementation | low | patch | Same regex, join and decode as `PageText.cs`. The claim in the doc and comment is corrected. |
| 9 | README exit code 4 doesn't match the code's 5 | low | patch | Fixed in the README. |
| 10 | `--out` check doesn't follow junctions or 8.3 names | low | reject | Phill picks `--out` himself, a junction into the repo is unlikely, and resolving link targets adds code. The gap is noted in the README. |
| 11 | Filler length below 13; temp settings dir never deleted | low | patch | Direct corrections. |
