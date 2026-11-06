---
title: 'Findings: do the read tools hold up on a large real notebook?'
type: 'spike-findings'
findings_for: '15'
status: 'draft'
created: '2026-11-05'
machine: 'Claude Code 2.1.318, OneNoteMcp 0.4.0-rc.4 (dist/publish), Windows 11 Pro 10.0.26300, not elevated'
evidence: 'spikes/stress-harness/ (harness, cap probe); run output kept outside the repo'
---

# Findings: do the read tools hold up on a large real notebook?

**Status:** draft. The harness and the cap probe are built and validated. The real-notebook run is Phill's (hitl). Accuracy, limits and the per-limit recommendations get filled in from his `summary.json`, and he reviews this doc for content before it's committed.

## Harness validation (MCP Test, 2026-11-05)

`StressHarness --notebook-name "MCP Test" --out <scratch>` with seed 1 against `dist/publish/OneNoteMcp.exe`: exit 0, 71 comparisons, **0 mismatches** and 71 `match` across all nine tools, with 0 `changedDuringRun` and 0 `recovered`. It took 4.4 s. The `build` block shows the server as `0.4.0-rc.4` (exe SHA-256 `EBED1C37…`, written 2026-11-06T03:56Z) and the oracle's `OneNoteMcp.Com` as `0.4.0-rc.4+85ddad7`.

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

"Round trip" is the whole tool call as the client sees it, which can span many COM calls. "Raw COM" is the oracle's single COM call, which is what the 30 s `timeouts.call` bounds, so the summary's timeout counts (`rawOver50PctCallTimeout`, `rawOver100PctCallTimeout`) use raw ms. None came near: the slowest raw call was 52 ms.

MCP Test has 3 sections (1 in the recycle bin), 7 pages, 1 image with 1 CallbackID, and 2 pages with Meta. Every page was readable. The warnings were the expected 10 `webLinkUnavailable` (MCP Test is local). The only errors were the 2 deliberate `ObjectNotFound`s from the errors phase.

- **Size overhead.** A tool response is about 2.8 times the raw XML (the 3,081-char pages-scope hierarchy came back as 8,744 bytes). The envelope travels twice, once as `structuredContent` and once as the text block, and the XML is JSON-escaped inside each. The text block alone is about 1.3 times the raw XML. See Client cap for which part counts.
- **find_pages cost.** It's the one tool much slower than its raw call (round-trip p50 126 ms against 27 ms for the raw `FindPages`), because with unindexed pages included it reads every candidate page whose name doesn't already match. On MCP Test that's at most 7 reads. On a large notebook it grows with the page count, and the real run will measure it.
- **What the search comparison proves.** The harness predicts find_pages from its own XML reading, but with the same rule the server implements: one:T text with tags stripped, entities decoded and lines joined by newlines, plus every term in the name or text, ignoring case. So a match shows that the server applies that rule consistently across the notebook. It doesn't show that the rule is the right one. find_meta is predicted from each page's own `one:Meta`, where the server uses the search result's, so that comparison does cross-check two sources.

**I/O matrix rows:**

| Scenario | Result |
|---|---|
| No `--notebook*` | exit 2, usage |
| Unknown nickname | exit 2, "no single notebook matched", without the name |
| Ambiguous nickname | not exercised: it would need two open notebooks with the same nickname or name, and the harness is read-only. It's covered by code reading only, since the same `matches.Count != 1` check refuses both cases. |
| `--out ./x` inside the repo | exit 2 |
| A server with a tool outside the contract (the harness's own `--fake-server`, 1 `filler` tool) | exit 3 before any read |
| The nine names without readOnlyHint (`--fake-variant contract-no-readonly`) | exit 3 before any read |
| Tool error | exercised by the errors phase. `get_page_content` and `get_hierarchy_parent` on a well-formed nonexistent ID (the notebook ID with its GUID's last digit changed) both returned `{ok:false, code: ObjectNotFound, hresult 0x80042014}`, the same as the raw calls, and both were counted in `errorsByCode`. `SectionLocked` itself wasn't exercised, because MCP Test has no locked section and making one is a write. |
| Concurrent edit | not exercised: making one is a write, and MCP Test was idle. It's covered by code reading only. If the two raw reads differ the record gets `changedDuringRun`. A search mismatch is reclassified only when every disagreeing page is one the pages phase never saw, or a re-read of a disagreeing page shows its piBasic hash changed. A page with no earlier hash, such as a recycle-bin page, counts as no evidence, so the mismatch stands. |

**Content check.** A grep of the final run's `run.jsonl` and `summary.json` for "Seed", "Fixtures", "MCP Test", both fixture Meta names (`onenote-mcp-fixture`, `onenote-mcp-search`), the notebook folder and the user name found nothing, matching case or ignoring it. Every string value in both files is a fixed label (phase, tool, kind, match state, warning code) or a OneNote ID.

## Client cap (Claude Code 2.1.318, default `MAX_MCP_OUTPUT_TOKENS`)

`probe-output-cap.ps1` sends synthetic filler through `claude -p --model haiku` with `MAX_MCP_OUTPUT_TOKENS` unset. The filler is numbered 100-character lines ending in `END-OF-FILLER`, and each run reads the tool_result out of the stream-json transcript. There were three series. `words` is plain words. `xml` is `<one:OE><one:T>`-style tags, chosen to tokenize at a different ratio. `structured` is the words filler wrapped the way the real server returns it: the envelope as `structuredContent`, and its JSON as the text block. Tokens per character come from input-token deltas against a 13-character baseline run (110,112 input tokens): `words` costs 0.42 tokens per character (about 2.4 characters per token) and `xml` 0.62 (about 1.6).

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

- **There are two limits, not one.**
  - **Over about 50,000 characters** (words 50,000 whole, 50,500 preview), Claude Code saves the result to a file under `~/.claude/projects/<project>/<session>/tool-results/`. The model gets the path and a 2 KB preview, so it sees the beginning and has to read the file for the rest.
  - **Over the token cap**, the model gets only the notice and no content at all.
- **The token cap depends on the content, so it isn't a character count.** Words reach the notice-only limit between 58,750 and 60,000 characters, XML between 50,000 and 55,000. Measured against the model's own input tokens, it doesn't sit at 25,000 either. 50,000 characters of XML (about 31,000 measured tokens) went through whole, while 60,000 characters of words (about 25,300 measured tokens) got the notice. So Claude Code counts the 25,000-token cap its own way, which these runs only bracket. The earlier draft's "60k is 24.5k tokens, consistent with 25k" was wrong: at 0.42 tokens per character, 60,000 characters is about 25,300 tokens, and the XML runs show the cap isn't the model's token count anyway.
- **structuredContent doesn't count; only the text block does.** The structured runs crossed both limits at the same text-block sizes as plain text. The notice reported 60,693 characters, which is the text block alone, not twice that. And whole runs delivered the envelope JSON from the text block.
- **What the server should plan for.** A read is safe when its text block is under about 50,000 characters. Above that the model sees only the first 2 KB, and past the token cap it sees nothing (for XML, somewhere between 50k and 55k characters). The text block is about 1.3 times the raw XML, so the practical ceiling for a fully delivered read is roughly 38,000 characters of raw page or hierarchy XML. In both spilled cases the full result, page content included, lands in a plain file on disk outside the server's control, which bears on AD-13's content-free promise. The real run's `textBytes` distribution and the `textOver25kTokensAtBytesPer3` and `textOver25kTokensAtBytesPer4` counts estimate how often that happens. The `PerN` estimates use bytes/N, so the 50,000-character limit is the sharper test, and the summary now counts it directly as `textOver50kBytes` (UTF-8 bytes, which equal characters for ASCII XML).

## Real-notebook run (pending: Phill)

To be filled in from Phill's `summary.json`: notebook size, accuracy (mismatches, `changedDuringRun`, `recovered`, by phase), limits hit (sizes over the cap by tool, calls over 50% and 100% of the 30 s call timeout, errors by code), and a recommendation per limit hit, each targeting 1.13, 1.14 or a new story.
