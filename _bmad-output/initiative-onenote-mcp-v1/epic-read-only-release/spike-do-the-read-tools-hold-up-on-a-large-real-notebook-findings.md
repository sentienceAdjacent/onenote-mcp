---
title: 'Findings: do the read tools hold up on a large real notebook?'
type: 'spike-findings'
findings_for: '15'
status: 'final'
created: '2026-11-05'
machine: 'Claude Code 2.1.318, OneNoteMcp 0.4.0-rc.4 (dist/publish), Windows 11 Pro 10.0.26300, not elevated'
evidence: 'spikes/stress-harness/ (harness, diag modes, cap probe, image-download probe); run output kept outside the repo'
---

# Findings: do the read tools hold up on a large real notebook?

**Summary**
1. **Accuracy holds.** Across 1,322 comparisons on three real work notebooks, 1,314 matched the raw COM read, 7 were `changedDuringRun` and 1 was `unstableRead`, which is OneNote's own doing (quirk 1). There were 0 server mismatches.
2. **Size limits are hit often.** 11–34% of page reads, two of the three notebooks' pages-scope listings and the root pages listing have a text block over Claude Code's ~50,000-character persist limit. The model then sees only a 2 KB preview, or a notice with no content past the token cap.
3. **find_pages is slow.** It takes 2–5 s against 43–55 ms for the raw `FindPages`, and the cost grows with page count.
4. **Three OneNote quirks:**
   - unlocked table column widths alternate on every read of one page;
   - `one:XPSFile` CallbackIDs give `ObjectNotFound`;
   - `SyncHierarchy` doesn't fetch images that aren't on this PC.
5. **Seven recommendations:** large results (1.16 spine amendment, then 1.18), find_pages cost (1.19), a read grant for protected sections (1.16, then 1.20), missing images and XPSFile (1.21), column widths (epic 2), and a synthetic size fixture (1.17). All come before 1.13, the epic's clean-machine install.

**Status:** final (2026-11-06). Fable-reviewed, all 18 findings applied, and Phill reviewed it for content. The real-notebook runs used three work notebooks, called A, B and C here.

Claims are labelled inline. *Measured* means a run in this spike. *Inferred* means reasoned from measurements. *Code reading* means read from source or docs and not run.

**Deviations from the frozen plan, both approved by Phill on 2026-11-06:**
- The mismatch retry reads raw, raw, tool, not raw, tool.
- The agent started the real-notebook runs and read the mismatch and error records in `run.jsonl`, which hold IDs and numbers only, as well as `summary.json`.

## Harness validation (MCP Test, 2026-11-05 and 2026-11-06)

`StressHarness --notebook-name "MCP Test" --out <scratch>` with seed 1 against `dist/publish/OneNoteMcp.exe` (measured):
- exit 0;
- 71 comparisons, **0 mismatches** and 71 `match` across all nine tools;
- 0 `changedDuringRun`, 0 `recovered` and 0 `unstableRead`;
- run time 4–6 s.

The latest run's `build` block shows the server as `0.4.0-rc.4` and the oracle's `OneNoteMcp.Com` as `0.4.0-rc.4+b617777`. Its binary counts are `callbackIdsFound` 1, `binariesReturned` 1, `binariesUnavailable` 0 and `binariesNotFound` 0.

| Tool | Calls | Max response bytes | Max text-block bytes | Round trip ms p50 / max | Raw COM ms p50 / max |
|---|---|---|---|---|---|
| get_hierarchy (4 notebook scopes + root notebooks) | 5 | 8,744 | 4,114 | 9 / 110 | 10 / 11 |
| get_page_content (7 basic + 1 includeBinary + 1 nonexistent) | 9 | 13,708 | 6,461 | 17 / 49 | 15 / 34 |
| get_binary_page_content | 1 | 682 | 209 | 8 / 8 | 8 / 8 |
| find_pages (20 seeded queries) | 20 | 7,138 | 3,233 | 126 / 162 | 27 / 52 |
| find_meta (2 distinct Meta names) | 2 | 6,946 | 3,124 | 20 / 22 | 19 / 19 |
| get_hierarchy_parent (10 + 1 nonexistent) | 11 | 1,017 | 343 | 7 / 10 | 6 / 13 |
| get_hyperlink_to_object | 10 | 973 | 347 | 8 / 10 | 6 / 8 |
| get_web_hyperlink_to_object | 10 | 1,138 | 408 | 7 / 10 | 7 / 8 |
| get_special_location | 3 | 629 | 183 | 7 / 10 | 6 / 7 |

"Round trip" is the whole tool call as the client sees it, which can span many COM calls. "Raw COM" is the oracle's single COM call, which is what the 30 s `timeouts.call` bounds, so the summary's timeout counts (`rawOver50PctCallTimeout`, `rawOver100PctCallTimeout`) use raw ms. None came near: the slowest raw call was 52 ms (measured).

MCP Test has 3 sections (1 in the recycle bin), 7 pages, 1 image with 1 CallbackID, and 2 pages with Meta. Every page was readable. The warnings were the expected 10 `webLinkUnavailable` (MCP Test is local). The only errors were the 2 deliberate `ObjectNotFound`s from the errors phase.

- **Size overhead (measured).** A tool response is about 2.8 times the raw XML (the 3,081-char pages-scope hierarchy came back as 8,744 bytes). The envelope travels twice, once as `structuredContent` and once as the text block, and the XML is JSON-escaped inside each. The text block alone is 1.25–1.28 times the raw XML for hierarchy listings (A, B and C), but 1.66 times for the quirk-1 page (116,870 raw chars, 194,281 text bytes): page XML escapes more heavily. Only the text block counts toward the client's limits (see Client cap).
- **find_pages cost (measured).** It's the one tool much slower than its raw call (round-trip p50 126 ms against 27 ms for the raw `FindPages`). With unindexed pages included it reads every candidate page whose name doesn't already match (code reading, `FindPagesUseCase`). On MCP Test that's at most 7 reads.
- **What the search comparison proves.** The harness predicts find_pages from its own XML reading, but with the same rule the server implements: one:T text with tags stripped, entities decoded and lines joined by newlines, plus every term in the name or text, ignoring case. So a match shows that the server applies that rule consistently across the notebook. It doesn't show that the rule is the right one. find_meta is predicted from each page's own `one:Meta`, where the server uses the search result's, so that comparison does cross-check two sources.

**I/O matrix rows:**

| Scenario | Result |
|---|---|
| No `--notebook*` | exit 2, usage (measured) |
| Unknown nickname | exit 2, "no single notebook matched", without the name (measured) |
| Ambiguous nickname | not exercised: it would need two open notebooks with the same nickname or name, and the harness is read-only. Covered by code reading only, since the same `matches.Count != 1` check refuses both cases. |
| `--out ./x` inside the repo | exit 2 (measured) |
| A server with a tool outside the contract (the harness's own `--fake-server`, 1 `filler` tool) | exit 3 before any read (measured) |
| The nine names without readOnlyHint (`--fake-variant contract-no-readonly`) | exit 3 before any read (measured) |
| Tool error | exercised by the errors phase (measured). `get_page_content` and `get_hierarchy_parent` on a well-formed nonexistent ID (the notebook ID with its GUID's last digit changed) both returned `{ok:false, code: ObjectNotFound, hresult 0x80042014}`, the same as the raw calls, and both were counted in `errorsByCode`. `SectionLocked` itself wasn't exercised on MCP Test, because it has no locked section and making one is a write. On A, the 2 locked sections came back as `sectionLocked` warnings. |
| Concurrent edit | not exercised: making one is a write, and MCP Test was idle. Covered by code reading only. If the first raw read differs from the second, the record gets `changedDuringRun`. A search mismatch is reclassified only when every disagreeing page is one the pages phase never saw, or a re-read of a disagreeing page shows its piBasic hash changed. A page with no earlier hash, such as a recycle-bin page, counts as no evidence, so the mismatch stands. |

**Content check (AC2: met, measured 2026-11-06).** The latest MCP Test run's `run.jsonl` and `summary.json` were searched, ignoring case, for 31 strings:
- every section, page, notebook and recycle-bin name in MCP Test's captured hierarchy (`Seed`, `Fixtures`, `Fixtures 2`, `Fixture Image`, `Fixture Nested`, `Fixture Probe`, `Fixture Subpage`, `Fixture Table`, `Untitled`, `Deleted Pages`, `OneNote_RecycleBin`, `MCP Test`);
- both fixture Meta names;
- every fixture paragraph text from `FixtureCapture.cs`;
- the notebook folder and the user name.

There were 0 hits. The 2-character table texts `A1` and `B1` were checked only against non-ID string values. Their only hits were inside the oracle's commit hash, which is hex, not content. Every other string value in both files is a fixed label or a OneNote ID.

**Harness fixes after the first real-notebook runs:**

- **Binary counts were mislabelled.** `binariesAvailable` was every CallbackID found, not the ones whose binary came back. It's now `callbackIdsFound`, joined by `binariesReturned` (raw call ok with base64), `binariesUnavailable` (`0x8004200F`) and `binariesNotFound` (`ObjectNotFound`), all counted from the oracle's outcome. On A the old label read 90 "available", but none were returned: 86 were unavailable and 4 were `ObjectNotFound` (quirk 2).
- **A new match state, `unstableRead`.** A mismatch retry used to read raw, tool, raw, tool. A page whose XML alternates on every read lines up with that order exactly: both raw reads get one value and both tool reads the other, so the "two raw reads differ" check never fires (quirk 1). The retry now takes two raw reads back to back (raw2, raw3) before the tool read.
  - If those two differ, the record is `unstableRead`: OneNote itself is unstable. That's kept apart from `changedDuringRun` (an edit or sync) and from `mismatch` (a server fault).
  - The record also gets `unstableOffset`, plus the element path (`unstablePath`) and attribute name (`unstableAttr`) of the first difference, names only.
  - The state is counted in `matchStates` and `byPhase` and doesn't count as a failure.
  - Run as the pages phase runs it, the quirk-1 page now classifies as `unstableRead` at offset 29,795, `one:Page/one:Outline/one:OEChildren/one:OE/one:Table/one:Columns/one:Column`, attribute `width` (measured).

### OneNote quirks found

**1. On one page of A, unlocked table column widths alternate between two values on every piBasic read.** That page (`{CA9E1A20-…}{1}{E1956022…}`) failed the `get_page_content` comparison on every attempt. B and C matched on all their pages, so what triggers this isn't identified. `--diag-page` shows it isn't the server (all measured):

- Raw and tool are the same length (116,870 chars), and their character classes are identical: 0 lone surrogates, 0 C0 controls, 0 NUL, 228 CRLF, 0 U+FFFD, 42 non-ASCII units. `structuredContent.xml` equals the text block's xml, so the JSON layer changes nothing.
- They differ in 26 character positions, all digits, starting at offset 29,795. Every difference is in the `width` attribute of the page's two `one:Table/one:Columns/one:Column` elements, whose values are:
  - one read: `108.2345428466797` and `526.8356323242188`;
  - the other read: `106.6687622070312` and `528.4014282226562`.

  The total is the same in both. Both columns carry only `index` and `width`, and neither has `isLocked="true"`.
- Six back-to-back raw reads on one COM connection, with no tool call involved, return `A B A B A B`.
- Calling the tool before the raw read swaps which side gets which value. A second COM connection opened in the same process just takes the next value in the sequence. From these two results it's inferred that the state belongs to OneNote and toggles per page on each `GetPageContent`, rather than being tied to a client or connection.
- Ruled out (measured):
  - the server build: the published single-file exe and a plain framework-dependent `src/` build behave the same;
  - the exe name;
  - .NET globalization, with InvariantGlobalization on and off;
  - process DPI awareness, both per-monitor v2 and system-aware;
  - having a console window.

What it means (inferred): piBasic isn't byte-stable for this page, and AD-17's `pageHash` and `elementHashes` are computed from piBasic. With strict A/B alternation, a plain read-then-write is refused every time, not half the time. The basis comes from read n, and the write pipeline's step-3 re-read is read n+1, which always has the opposite parity, so the hashes never match and the result is `StaleWrite`. Only if an odd number of other reads of the page fell in between would the two reads land on the same value. That makes it a design question for the write epic: for example, exclude `one:Column/@width` of unlocked columns from the hash, or treat it as layout. It isn't a read-tool bug, since the tool returns exactly what OneNote gave it.

**2. A File Printout's `one:XPSFile` CallbackID returns ObjectNotFound from GetBinaryPageContent.** `--diag-callbacks` read every page's piBasic and every CallbackID's binary, raw only (measured: A in 8.7 s, and B):

| CallbackID's parent / grandparent | A | B |
|---|---|---|
| `one:OE/one:Image`: returned | 0 | 9 |
| `one:OE/one:Image`: `0x8004200F` (binary not on this PC) | 52 | 167 |
| `one:Page/one:Image`: `0x8004200F` | 34 | 10 |
| `one:Page/one:XPSFile`: `ObjectNotFound`, `0x80042014` | 4 (on 4 pages) | 2 (on 2 pages) |

So all 6 `ObjectNotFound`s across the three notebooks were page-level `one:XPSFile`, the printout behind a File Printout. No other element gave `ObjectNotFound`. The tool and the raw call agree, so the tool is right. Its description says only that an unavailable binary gives the `binaryUnavailable` warning, though, and this case is a hard `ObjectNotFound` error instead (recommendation 5).

**3. `SyncHierarchy` doesn't download an image that isn't on this PC (measured, 2026-11-06).** Phill approved `SyncHierarchy` on one page of A. The probe (`--probe-sync`) uses the spike's own compiled-in copy of the hand-declared interop, because `ComGateway` doesn't expose `SyncHierarchy`. It never navigates or writes.
- The page (`{CA9E1A20-…}{1}{E1956267…}`) has 2 image CallbackIDs. Before the sync, the chosen one gave `0x8004200F`.
- `SyncHierarchy(page)` returned S_OK in 0 ms. Thirty `GetBinaryPageContent` polls, every 2 s for 60.7 s, all gave `0x8004200F`.
- `SyncHierarchy(section)` also returned S_OK in 0 ms. Another 30 polls over 60.5 s all gave `0x8004200F`.
- Afterwards, both of the page's image CallbackIDs still gave `0x8004200F`.

Because the call returns at once, it's inferred to only queue a sync, or to do nothing for content. Either way, it doesn't bring the images down within a minute.

**OneMore (checked 2026-11-06, repo at `885f976`, code reading).** OneMore's "OneNote Defects" page lists 8 defects: merged cells, NavigateToUrl, child `lastModifiedTime`, spell-check, wrong window, local styles, page `lastModifiedTime`, and MathML soft-break. None covers any of the three quirks. It says nothing about column widths changing between reads, about XPS or printouts, or about GetBinaryPageContent or CallbackID. Its code is consistent with quirks 1 and 2 without naming them:

- `Models/Table.cs`'s `SetColumnWidth` always writes `isLocked="true"` along with a width, which fits widths being OneNote-managed when unlocked.
- Its one `GetBinaryPageContent` wrapper (`OneNote.cs` `GetPageContent(pageId, callbackId)`) catches every exception, logs it and returns null. It doesn't special-case XPS.
- Its Analyze command skips images with `xpsFileIndex` and reads printout sizes from `one:Printout`/`xpsFileIndex`, not by fetching the `XPSFile` binary.

Both lessons are recorded in `research-onemore-lessons.md` (rows 15 and 16).

## Client cap (Claude Code 2.1.318, default `MAX_MCP_OUTPUT_TOKENS`)

`probe-output-cap.ps1` sends synthetic filler through `claude -p --model haiku` with `MAX_MCP_OUTPUT_TOKENS` unset. The filler is numbered 100-character lines ending in `END-OF-FILLER`, and each run reads the tool_result out of the stream-json transcript. There were three series:
- `words`: plain words;
- `xml`: `<one:OE><one:T>`-style tags, chosen to tokenize at a different ratio;
- `structured`: the words filler wrapped the way the real server returns it, with the envelope as `structuredContent` and its JSON as the text block.

Tokens per character come from input-token deltas against a 13-character baseline run (110,112 input tokens). `words` costs 0.42 tokens per character (about 2.4 characters per token), and `xml` 0.62 (about 1.6). All rows below are measured.

| Series | Size sent (chars) | Text block (chars) | What the model received |
|---|---|---|---|
| words | 13 … 50,000 | same | whole |
| words | 50,500 … 58,750 | same | **preview**: a `<persisted-output>` block with the saved file's path and the first 2 KB |
| words | 60,000 | same | **notice only**: "Error: result (N characters across M lines) exceeds maximum allowed tokens. Output has been saved to <path>", plus how to read the file. No content. |
| xml | 40,000, 50,000 | same | whole |
| xml | 55,000 … 120,000 | same | notice only |
| structured | 25,000, 27,500, 30,000 | 25,343 … 30,393 | whole, as the envelope JSON (the text block) |
| structured | 50,000, 55,000 | ≈50,700, ≈55,700 | preview |
| structured | 60,000 | 60,693 | notice only, giving the JSON schema `{ok, xml, meta, warnings}` and suggesting jq |

`is_error` was false in every row. The earlier run (20k, 40k, 80k, 100k, 120k, 200k of words) agrees: whole up to 40k, notice only from 80k.

What the runs show:

- **There are two limits, not one (measured).**
  - **Over about 50,000 characters** (words 50,000 whole, 50,500 preview), Claude Code saves the result to a file under `~/.claude/projects/<project>/<session>/tool-results/`. The model gets the path and a 2 KB preview, so it sees the beginning and has to read the file for the rest.
  - **Over the token cap**, the model gets only the notice and no content at all.
- **The token cap depends on the content, so it isn't a character count (measured).** Words reach the notice-only limit between 58,750 and 60,000 characters, XML between 50,000 and 55,000. Measured against the model's own input tokens, it doesn't sit at 25,000 either. 50,000 characters of XML (about 31,000 measured tokens) went through whole, while 60,000 characters of words (about 25,200 measured tokens) got the notice. So it's inferred that Claude Code counts the 25,000-token cap its own way, which these runs only bracket.
- **structuredContent doesn't count; only the text block does (measured).** The structured runs crossed both limits at the same text-block sizes as plain text. The notice reported 60,693 characters, which is the text block alone, not twice that. And whole runs delivered the envelope JSON from the text block.
- **What the server should plan for (inferred from the above).** A read is delivered whole when its text block is under about 50,000 characters. Above that the model sees only the first 2 KB, and past the token cap it sees nothing (for XML, somewhere between 50k and 55k characters). How much raw XML fits depends on how heavily it escapes:
  - **hierarchy XML**, with a text block 1.25–1.28 times the raw: roughly 38,000 characters;
  - **page XML**, with a text block 1.66 times the raw on the one page measured: roughly 30,000 characters.

  In both spilled cases the full result, page content included, lands in a plain file on disk outside the server's control, which bears on AD-13's content-free promise. The `PerN` token estimates in the summary use bytes/N, so the 50,000-character limit is the sharper test. The summary counts it directly as `textOver50kBytes` (UTF-8 bytes, which equal characters for ASCII XML).

### Addendum: raising the limit per tool (story 1.16, 2026-11-06)

Added after the findings were finalised, as evidence for the spine's AD-20.

**What Claude Code documents (code.claude.com/docs/en/mcp, read 2026-11-06).** A server can set `_meta["anthropic/maxResultSizeChars"]` on a tool's `tools/list` entry. Claude Code then raises that tool's persist threshold to the value, "up to a hard ceiling of 500,000 characters". For such a tool, the value is used for text content "regardless of what `MAX_MCP_OUTPUT_TOKENS` is set to". Tools that return image data stay under `MAX_MCP_OUTPUT_TOKENS`. Error results longer than about 11,000 characters keep only their first and last 5,000 characters.

**The probe.** `FakeServer.cs` declares the key when `STRESS_MAX_RESULT_CHARS` is set, and `probe-output-cap.ps1 -MaxResultChars N` sets it for the child. The series was `structured` with `xml` filler, which is the heaviest-tokenizing content measured above, with the key set to 500,000 and `MAX_MCP_OUTPUT_TOKENS` unset (measured, Claude Code 2.1.318, `--model haiku`):

| Size sent (chars) | Text block (chars) | What the model received | Run input tokens |
|---|---|---|---|
| 60,000 | 60,693 | whole | 154,074 |
| 100,000 | 101,093 | whole | 179,274 |
| 200,000 | 202,093 | whole | 242,274 |
| 380,000 | 383,893 | whole | 355,674 |
| 600,000 | ≈600,700 | preview (`<persisted-output>`, "Output too large (591.9KB)", first 2 KB) | 117,678 |

A rerun of 60,000 on the final script gave whole with the key and, as a control without it, the notice only.

What the runs show:
- **The key lifts both limits, up to at least 380,000 characters (measured).** Without it, XML filler got the notice only from 55,000 characters. With it, 383,893 characters arrived whole, which is about 238,000 tokens above the 600,000 run's input (inferred from the token column). So the token cap no longer applied either.
- **Past the ceiling the result spills as before, with a preview (measured).** At 600,000 characters the model got the 2 KB preview, not the notice. Between 383,893 and 500,000 characters nothing was measured.
- **Delivering whole is not the same as usable (inferred).** A 380,000-character result costs the model about 238,000 tokens of context, more than many sessions can spare. That is why AD-20 sets an inline threshold well under the ceiling, about 100,000 characters, and indexes anything larger.
- **Other MCP clients** don't know the key. For them the inline threshold is a setting the user can lower (AD-20).

## Real-notebook runs (2026-11-06)

Three of Phill's work notebooks, A (`{ED868065-…}`), B (`{575EF622-…}`) and C (`{B2C5D9BF-…}`), were run one after another. All are stored on SharePoint.
- **Build** (from each `summary.json` `build` block, identical across the three runs):
  - server `dist/publish/OneNoteMcp.exe` 0.4.0-rc.4, SHA-256 `5B0AF18F562BA425DD1A8B2DB91494A42A3E16182AA8D7AA8010D7A8E4C866D5`, written 2026-11-06T14:25:54Z;
  - oracle `OneNoteMcp.Com` 0.4.0-rc.4+`b617777d2b1f15269a8cdc0d7673e08a1c062eeb`.
- **Options:** `--max-binaries 100000 --queries 100 --seed 1`, plus `--root-pages` on run A.
- **Approval:** see the deviations at the top.
- **Counts:** the runs came before the `unstableRead` state and the binary label fix, so the counts marked below were corrected by hand.

### Size and accuracy

| | A | B | C |
|---|---|---|---|
| Sections (locked, in recycle bin) | 14 (2, 1) | 39 (0, 1) | 37 (0, 1) |
| Pages read | 169 | 189 | 84 |
| Images, inserted files, CallbackIDs | 86, 0, 90 | 186, 3, 188 | 7, 0, 7 |
| Pages-scope hierarchy (raw chars) | 44,303 | 57,858 | 30,624 |
| Comparisons | 454 | 601 | 267 |
| Match | 451 | 597 | 266 |
| changedDuringRun | 2 | 4 | 1 |
| unstableRead (recorded as mismatch, corrected by hand) | 1 (quirk 1) | 0 | 0 |
| Mismatch | 0 | 0 | 0 |
| Run time | 8.8 min | 8.8 min | 3.5 min |

- **Accuracy holds (measured).**
  - Of 1,322 comparisons, 1,314 matched: the tool returned the same XML, bytes, ID sets or error code as the raw COM read. 7 were `changedDuringRun` and 1 was `unstableRead`.
  - The `unstableRead` is OneNote alternating one page's table column widths between reads (quirk 1).
  - All 300 search result sets matched their predictions.
  - The 7 `changedDuringRun` records are pages whose two raw reads differed. The cause wasn't captured. It's inferred to be sync or edits, since all three notebooks are SharePoint-stored and were open in OneNote during the runs.
- **Errors behave (measured).** The only errors were:
  - the deliberate nonexistent-ID probes, which got ObjectNotFound in both raw and tool, as expected;
  - 6 binary ObjectNotFound, all `one:XPSFile` (quirk 2): 4 in A and 2 in B.

  The 2 locked sections in A were reported as `sectionLocked` warnings, not as failures.
- **Timeouts aren't a concern at this size (measured).** No COM call reached 50% of the 30 s call timeout. The slowest took 220 ms.

### Limits hit

All figures are measured.

| Limit | A | B | C |
|---|---|---|---|
| get_page_content text block over 50k chars (2 KB preview, or notice only past the token cap) | 66 of 194 (34%) | 69 of 244 (28%) | 10 of 91 (11%) |
| get_page_content text block p50 / p95 / max (chars) | 28.6k / 130k / 862k | 27.4k / 125k / 362k | 16.1k / 92k / 305k |
| get_hierarchy text block over 50k | 2 of 6: the notebook at pages (56.7k) and the root at pages (198k) | 1 of 5 (notebook at pages, 72.5k) | 0 of 5 (notebook at pages, 38.6k) |
| find_pages text block over 50k | 4 of 100 | 13 of 100 | 0 of 100 |
| get_binary_page_content text block over 50k | 0 | 3 (max 235k) | 0 |
| find_pages round trip p50 (max) vs raw `FindPages` p50 | 4.95 s (6.8 s) vs 49 ms | 4.68 s (6.5 s) vs 55 ms | 1.93 s (2.9 s) vs 43 ms |
| Binaries actually returned (corrected by hand) | 0 of 90 (86 unavailable, 4 XPSFile ObjectNotFound) | 9 of 188 (177 unavailable, 2 XPSFile ObjectNotFound) | 0 of 7 (7 unavailable) |

These notebooks aren't a forecast for other users (Phill, 2026-11-06). What they show is that ordinary work notebooks already cross the client's limits often, and in which ways. The size limits need a design that works at any size, tested against a synthetic fixture that can be scaled past any real notebook.

### Not covered by the real data

- **`find_meta`:** no notebook has any `one:Meta`, so only MCP Test covers it.
- **Inserted files:** 3 in all.
- **Binaries:** only 9 were actually returned. In these three SharePoint notebooks, 270 of the 279 image CallbackIDs had no binary on this PC (measured). Whether that holds for SharePoint notebooks generally isn't known.
- **Unlocked password-protected sections:** the harness doesn't count `encrypted="true"`, so it can't say whether it read any. Spike 1 showed such sections are fully readable today (recommendation 3).

### Recommendations

1. **Large results must stay fully usable.** 1.16 (spine amendment), then 1.18; before 1.13.
   - **The problem (measured):** 11–34% of page reads, two of the three notebooks' pages-scope listings (A 56.7k, B 72.5k) and the root pages listing (198k) reach the model only as a 2 KB preview or a notice. C's pages-scope listing (38.6k) fits.
   - **Phill's rule (2026-11-06):** no functionality may be lost because of size. Writing results to disk and reading them from there is acceptable, and so is adding an XML library so the model reads them semantically.
   - **First a spine amendment** to choose the delivery design. Candidates:
     - a server-managed result file, returned as its path plus a small structural index (outlines, tables and images, with offsets), with a companion read for a range or an element;
     - paging by outline or element range;
     - a slimmer page view without ink, binary placeholders and layout attributes.
   - **Design inputs the amendment must settle:**
     - the result file's lifetime: deleted at server exit, after a TTL, or by a count or size cap;
     - what the companion read returns once a file has expired or been cleaned up, for example a distinct error code telling the caller to repeat the original read, never a silent empty result.
   - **Whatever is chosen must:**
     - keep each result under the persisted limit (about 50,000 text-block characters). Superseded by the addendum under Client cap: with `maxResultSizeChars` declared, the limit is the tool's own, and AD-20 sets the inline threshold;
     - clean up after itself;
     - be documented in the README.

     Claude Code's own spill files under `~/.claude/projects/` are acceptable to Phill, but they should also be documented.
2. **find_pages cost grows with page count.** 1.19.
   - **The problem:** it takes about 100 times as long as the raw `FindPages` (measured), because with unindexed pages included it reads every candidate page whose name doesn't match (code reading). At about 190 pages that's 5 s (measured). A notebook ten times larger would take about a minute, by linear extrapolation (inferred, not measured).
   - **Timeouts don't stop it (code reading).** Our 30 s `timeouts.call` bounds each COM call, not the tool, so it doesn't stop a long find_pages. Claude Code's per-tool MCP timeout (`MCP_TOOL_TIMEOUT`) has a documented default of about 28 hours (code.claude.com/docs/en/mcp, read 2026-11-06), and the plugin's `.mcp.json` sets no `timeout`. So neither limit would cut off a one-minute call. The cost is the user's wait, unless a user sets a short `MCP_TOOL_TIMEOUT`.
   - **Options:**
     - use indexed results first and read only the remainder;
     - cache page text by `lastModifiedTime`;
     - set a time budget that returns partial results flagged `possiblyIncomplete`, which AD-12 already promises.
3. **Password-protected sections need an explicit read grant.** 1.16 (spine amendment), then 1.20; before 1.13.
   - **Phill's rule (2026-11-06):** these sections stay unreadable without explicit permission, even while unlocked.
   - **Detection is reliable:** `encrypted="true"` is present whether the section is locked or not (spike 1).
   - **Scope:** the grant must cover every read path:
     - page content, binaries, search results and Meta results;
     - page names in the hierarchy;
     - `get_hyperlink_to_object` and `get_web_hyperlink_to_object`, because the onenote: link embeds the section file name and the page title (measured on MCP Test's captured link fixture);
     - `get_hierarchy_parent`, which confirms where a page sits.
4. **Images that aren't on this PC.** 1.21.
   - **The probe (measured, quirk 3):** `SyncHierarchy` on the page, and then on its section, returned S_OK at once. Neither brought the image down within 60 s per attempt.
   - **What to build (Phill chose the opt-in route, 2026-11-06):**
     - an opt-in fetch: on the user's explicit request, `NavigateTo` the page so OneNote downloads its images, then read the binary again. It stays user-requested only, because it takes focus (AD-11). Whether showing the page actually downloads the images is unverified (inferred), so 1.13 must measure it first.
     - The `binaryUnavailable` warning's `userAction` offers that fetch and also points to OneNote's Options > Sync > "Download all files and images".
   - **Calling `SyncHierarchy` from a read tool is not an option without a spine amendment.** It is an `AppState` operation under AD-7, bound by AD-11, so the amendment would have to reclassify or allow it. Given quirk 3, it's not worth it either.
5. **XPSFile CallbackIDs.** 1.21. All 6 `ObjectNotFound`s (A 4, B 2) were page-level `one:XPSFile` (quirk 2). Either map ObjectNotFound for `one:XPSFile` to `binaryUnavailable`, or say in get_binary_page_content's description that printout backgrounds can't be read.
6. **Alternating column widths.** Epic 2, logged in `deferred-work.md`. AD-17's ElementHash must ignore or normalise unlocked `one:Column/@width`. Otherwise, by the parity argument under quirk 1 (inferred), a plain read-then-write on such a page is refused as StaleWrite every time.
7. **A synthetic size fixture.** 1.17. Generate these in MCP Test, covering what the real notebooks didn't, at sizes the design must handle:
   - an oversized page;
   - a page with many large images and attachments;
   - a deep, wide hierarchy;
   - Meta tags;
   - a protected section.
