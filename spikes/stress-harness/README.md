# Stress harness (spike 1.15)

Checks whether the read tools stay accurate, fast and small enough on a large real notebook. It drives the real `OneNoteMcp.exe` over stdio as an MCP client, and compares every tool result with the same COM read made directly through `ComGateway` (the oracle). The plan is `_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/spike-do-the-read-tools-hold-up-on-a-large-real-notebook-plan.md`.

## Run it

OneNote desktop must already be running, with the notebook open. The harness never starts, closes or navigates OneNote; only the separate `--probe-navigate` probe below calls `NavigateTo`.

Run `scripts/pack-plugin.ps1` first, every time. The server under test is `dist/publish/OneNoteMcp.exe`, but the oracle is the `src/` build the harness compiles against, and the two can differ. `summary.json`'s `build` block records both: the server's `serverInfo.version`, the exe's SHA-256 and last-write time, and the oracle's `OneNoteMcp.Com` version (with its commit).

```powershell
pwsh -File scripts/pack-plugin.ps1            # writes dist/publish/OneNoteMcp.exe, the default --exe
dotnet build spikes/stress-harness -c Release
$h = "spikes/stress-harness/bin/Release/net10.0-windows/StressHarness.exe"
& $h --notebook-name "<nickname>" --out "$env:TEMP\stress-run"
# or: & $h --notebook "<notebook ID>" --out "$env:TEMP\stress-run"
```

Options: `--exe <path>`, `--root-pages` (also read every open notebook at pages scope), `--max-binaries <n>` (200), `--queries <n>` (20), `--seed <n>` (1), `--inline-max <n>` (100000: the server's `results.inlineMaxChars`, which no text block may exceed). There's no default notebook and no option that lists notebooks. A `--notebook-name` is matched against each notebook's nickname or name, ignoring case, and is never echoed.

Exit codes: 0 done with no mismatches; 1 done with mismatches, or with a text block over `--inline-max`; 2 usage or refusal (no notebook, no single match, `--out` inside the repo, exe missing); 3 the server's `tools/list` isn't exactly the contract tools in `tests/OneNoteMcp.Tests/Contract/tool-contract.json` (eleven since story 1.21 added `navigate_to`), each `readOnlyHint: true` except `navigate_to` (checked before any read); 4 OneNote not running, or the notebook list can't be read at startup; 5 a phase aborted, including when the notebook's own pages-scope hierarchy can't be read raw (the summary is still written, with `abortedBy`).

The harness never calls `navigate_to`: its tool client refuses it.

## What it does

1. **hierarchy**: the notebook at self, children, sections and pages, and the root at notebooks (and pages with `--root-pages`). Compares the XML byte for byte and the Section and Page ID sets.
2. **pages**: every page not in the recycle bin, `includeBinary: false` against piBasic; pages with `one:Image` or `one:InsertedFile` also `includeBinary: true` against piBinaryData.
3. **binaries**: up to `--max-binaries` CallbackIDs (seeded sample) against `GetBinaryPageContent`, including `binaryUnavailable`. A page-level `one:XPSFile`'s CallbackID (a File Printout) gives raw `ObjectNotFound`; since story 1.21 the tool must answer it with `ok` and `binaryUnavailable`.
4. **search**: `--queries` seeded-random 1–2-word queries drawn from page text through `find_pages`, and up to 10 distinct Meta names through `find_meta`. The harness predicts each result set itself, from the page XML it read (text with HTML stripped and entities decoded; each page's own `one:Meta`), in its own code, but with the same rule the server documents (same tag stripping, decoding and newline join), so it checks that the server applies the rule consistently, not that the rule is right.
5. **misc**: `get_hierarchy_parent`, both hyperlink tools on a sample of 20 objects, and the three special locations.
6. **errors**: `get_page_content` and `get_hierarchy_parent` with a well-formed ID that doesn't exist (the notebook ID with its GUID's last digit changed). Raw and tool must fail with the same code; `ObjectNotFound` is expected.

**Large results (story 1.18, AD-20).** A tool result over the server's inline threshold comes back as `meta.result` and an index. The harness then reads the whole body back with `read_result` offset reads of `/` and compares that with the raw COM read, so a stored result is checked byte for byte like an inline one. Every text block, the tool call's and each `read_result` part's, is measured in characters. The text block of a success with a body is a JSON header line, a newline, then the body (AD-9); the harness reads `structuredContent` and falls back to that format.

Each comparison reads raw first, then the tool. If they differ it reads raw twice back to back, then the tool once more, and the record carries that last attempt. If the two back-to-back raw reads differ, the call is `unstableRead`: OneNote returns unstable XML for it, as with unlocked table column widths, which alternate on every read. The record names the element path and attribute of the first difference. Otherwise the record carries the second tool attempt (`retried: true`, with the first attempt's size and time as `firstBytes` and `firstMs`). If the first raw read differs from the second, the call is `changedDuringRun` (you may be editing), not a mismatch; if they agree and the second tool read matches, it's `recovered`. A search mismatch also becomes `changedDuringRun` when every page the sets disagree on is one the pages phase never saw, or a re-read shows one of them changed. Only `mismatch` counts as a failure.

## Diagnostic modes

Both print to stdout and write no files. Like the run, they print only lengths, offsets, counts, U+hex codepoints, schema element and attribute names, numeric attribute values, codes and IDs.

- `--diag-page <pageId>` reads the page's piBasic raw and through `get_page_content` (`includeBinary: false`). It prints character-class counts (surrogates, controls, CR/LF, U+FFFD) for each side, then for both `structuredContent.xml` and the text block: the first differing offset, the length delta, the substituted codepoints, each difference's element path (and attribute, with both values when they're numeric), whether a second COM connection and a re-read agree, and six back-to-back raw reads as an A/B sequence. `STRESS_TOOL_FIRST=1` calls the tool before the raw read.
- `--notebook <ID> --diag-callbacks` (or `--notebook-name`) reads every page's piBasic raw and every CallbackID's binary raw, then prints counts by parent and grandparent element name and outcome. Failures other than "unavailable" are listed with their page ID.
- `--probe-sync --notebook <ID> [--page <ID>]` is the image-download probe. It finds a page with an image whose binary gives `0x8004200F` (or uses `--page`), calls `SyncHierarchy` on the page, and polls `GetBinaryPageContent` every 2 s for 60 s. If the image still hasn't arrived, it does the same once for the page's section. It needs no server. It uses a compiled-in copy of `src/OneNoteMcp.Com/Interop`, because `ComGateway` has no `SyncHierarchy`. `SyncHierarchy` is an `AppState` call (AD-7, AD-11), so run it only where Phill has approved it.
- `--probe-navigate --notebook <ID> [--page <ID>]` is story 1.21's image-download probe. It picks a page that isn't unread, isn't in the recycle bin and isn't in a locked or password-protected section, and that has at least one image CallbackID giving `0x8004200F` (or uses `--page`, under the same rules). It calls `NavigateTo` on the page once, then polls every image CallbackID of the page every 2 s, up to 30 times, stopping when none gives `0x8004200F`. It prints only IDs, HRESULTs, counts and times. It needs no server and uses the same compiled-in interop copy as `--probe-sync`. `NavigateTo` is user-visible: it takes keyboard focus and changes the page OneNote shows (AD-11). Run it only with Phill's approval, while he's at the machine.

## What it never records

Nothing it writes, on stdout, stderr or in files, carries notebook content: no text, titles, section or notebook names, Meta names or contents, query words, paths, links or exception messages. It writes only IDs, numbers (sizes, timings, counts), tool and error codes, warning codes, HRESULTs and exception type names. Query words and Meta names stay in memory, and records refer to them by index. The server's own stderr is counted, not kept.

## Where the output goes

`--out` must resolve outside the repo; the harness refuses otherwise. The check compares resolved full paths only. It doesn't follow junctions or symlinks and doesn't expand 8.3 short names, so pick a plain folder outside the repo. It writes:

- `run.jsonl`: one line per comparison: phase, tool, kind, target ID, match state, ok/code/HRESULT, response bytes (the JSON-RPC `result` serialised), text-block bytes and characters (`textChars`), the `read_result` parts used to reassemble a stored result (`readResultParts`) and the largest text block among the call and its parts (`maxTextChars`), tool round-trip and raw ms, warning codes, and per-call counts.
- `summary.json`: `largeResults` (`inlineMaxChars`, `maxTextChars`, `textBlocksOverInlineMax`, `storedResults`, `readResultParts`); notebook counts (among them sections, `sectionsLocked`, `sectionsEncrypted` — password-protected, whether locked or unlocked now — `sectionsInRecycleBin`, pages, `pagesInRecycleBin`, `hierarchyPagesChars`, `images`, `insertedFiles`, `pagesWithMeta`, `distinctMetaNames` and the `binaries*` counts); calls and mismatches; match states overall and by phase; errors and warnings by code; per tool and per kind, p50/p95/max response bytes, text-block bytes, round-trip ms (`roundtripMs`, the whole tool call, which can span many COM calls) and raw ms (`rawMs`, one COM call); the top 10 sizes; counts over 25k tokens estimated at bytes/4 and bytes/3, for the full response and for the text block; and raw COM calls over 50% and 100% of the 30 s per-COM-call timeout (`rawOver50PctCallTimeout`, `rawOver100PctCallTimeout`).

For a real notebook, share only `summary.json`. Keep `run.jsonl` outside the repo.

## The cap probe

`probe-output-cap.ps1` measures what Claude Code does with a tool result past its MCP output cap, using no notebook. The harness's `--fake-server <chars>` mode is a one-tool stdio server whose `filler` tool returns exactly that many characters of numbered filler lines ending in `END-OF-FILLER`. The script runs `claude -p --mcp-config` for each size (20k, 40k, 80k, 100k, 120k and 200k characters by default), unsets `MAX_MCP_OUTPUT_TOKENS` so the default cap applies, and records from the stream-json transcript whether the result reached the model whole, cut short, replaced by a saved-to-file notice (`savedToFile`), shown as a saved file plus a 2KB preview (`persistedPreview`), or as an error, along with any text Claude Code added. The notice names a local file under `~/.claude/projects/`, never notebook content, since the filler is synthetic.

```powershell
pwsh -File spikes/stress-harness/probe-output-cap.ps1 -Out "$env:TEMP\cap-probe"
# a second filler with a different chars-per-token ratio, and the real envelope shape:
pwsh -Command "& spikes/stress-harness/probe-output-cap.ps1 -Out $env:TEMP\cap-xml -Filler xml -Sizes 40000,60000,80000"
pwsh -Command "& spikes/stress-harness/probe-output-cap.ps1 -Out $env:TEMP\cap-env -Variant structured -Sizes 25000,50000"
```

`-Filler words|xml` (`--fake-filler`) picks plain words or `<one:OE><one:T>`-style tags, which tokenize at different characters per token, so comparing their thresholds tells a token cap from a character cap. `-Variant structured` (`--fake-variant structured`) returns the filler the way the real server does, with the envelope `{ok, xml, meta, warnings}` as `structuredContent` and its JSON as the text block, which shows whether `structuredContent` counts toward the cap. `--fake-server` refuses fewer than 13 characters (the end marker's length).

`--fake-server <chars> --fake-variant contract-no-readonly` advertises the contract names without `readOnlyHint`. It exists only to test the startup check: `--exe <StressHarness.exe> --exe-arg --fake-server --exe-arg 100 --exe-arg --fake-variant --exe-arg contract-no-readonly` must exit 3.
