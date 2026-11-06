---
title: 'The guide skill: read recipes'
type: 'feature'
ticket: '12'
created: '2026-11-05'
status: done
route: 'full'
route_source: 'auto'
baseline_revision: '85ddad7d6066fa3e8241cf0c7f14d1320c2b3e14'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The plugin ships nine read tools but no skill, so Claude has to guess how to use them. The tools are thin (AD-5): which scope to use, how to read OneNote's XML, why search results may be incomplete, and what to do with each error's `userAction` are all things a model has to be told.

**Approach:** Add `plugin/skills/guide/SKILL.md` (`/onenote:guide`). It's a model-invoked skill that triggers on OneNote requests and covers:
- a tool overview;
- the 2013 XML patterns needed for reading;
- task recipes;
- search caveats (`possiblyIncomplete`, `meta.filtered`);
- how to act on errors and warnings;
- the AD-11 user-visible rule.

A unit test pins the skill to the tool contract, so the two can't drift apart.

## Boundaries & Constraints

**Always:**
- Name tools as Claude Code shows them: `mcp__plugin_onenote_desktop__<tool>`, with the bare name given once.
- Read-only: the skill says the release has no write tools, and never suggests editing notebooks by other means (files, UI automation).
- Follow AD-11:
  - never take focus or move the user's view;
  - user-visible tools (none exist yet) are only for explicit requests;
  - `find_pages` never displays results in OneNote.
- Follow AD-12 when reporting search results: always say they may be incomplete, and never treat a missing page as proof it doesn't exist.
- Follow AD-9 on errors:
  - when `retryable` is true, relay the `userAction` to the user and retry only after they act, or immediately for `ComBusy` or `Timeout` on a read, at most once;
  - when it's false, don't retry with the same input.
- Default to the cheapest call: `get_hierarchy` at the narrowest scope that answers the question, and `get_page_content` with `includeBinary` false.

**Never:**
- No new server code, tool, or contract change (AD-14 snapshot unchanged).
- No content from Phill's notebooks in examples. Examples use made-up names or MCP Test.
- Don't restate full tool descriptions. The skill adds what the descriptions can't: choice, sequencing and interpretation.
- No reference subfolders unless SKILL.md exceeds ~400 lines.

</frozen-after-approval>

## Code Map

- `plugin/` -- plugin root. It holds `.claude-plugin/plugin.json` (name `onenote`) and `.mcp.json` (server `desktop`). `scripts/pack-plugin.ps1` copies the whole of `plugin/` into `dist/stage/onenote`, so a new `skills/` folder ships with no script change.
- `src/OneNoteMcp.Server/Tools/*Tool.cs` -- the nine tool descriptions, which are the source of truth for parameters and meta fields:
  - `scope`: self, children, notebooks, sections, pages;
  - `includeBinary`, `callbackId`;
  - `startNodeId` or `fromRoot` (exactly one);
  - `meta.filtered` reasons: recycleBin, noMatch, notFound;
  - `meta.basis`, `meta.parentId`, `meta.link`, `meta.path`, `meta.binary`, `meta.binaryUnavailable`.
- `src/OneNoteMcp.Core/Results/ToolErrors.cs` -- the per-code `userAction` text and `IsRetryable`. The skill's error guidance must agree with it.
- `src/OneNoteMcp.Core/Results/ToolWarnings.cs` -- warning codes `sectionLocked`, `webLinkUnavailable`, `binaryUnavailable`.
- `src/OneNoteMcp.Xml/OneNoteXml.cs:9` -- namespace `http://schemas.microsoft.com/office/onenote/2013/onenote`.
- `tests/OneNoteMcp.Tests/Contract/tool-contract.json` -- the nine tool names (`tools[].name`). Reuse it for the drift test.
- `tests/OneNoteMcp.Tests/PluginPackagingTests.cs` and `RepoPaths.cs` -- where the plugin-file tests live, with the `RepoPaths.Combine` helper. Add the skill tests here.
- Spine AD-5, AD-9, AD-11, AD-12, AD-15 -- the rules the skill teaches. Don't edit the spine.

## Tasks & Acceptance

**Execution:**
- [x] `plugin/skills/guide/SKILL.md` -- write the skill:
  - frontmatter `name: guide` and a trigger-rich `description` (OneNote notebooks, sections, pages, search, links);
  - sections: when to use; tool map; reading the XML; recipes; search caveats; errors and warnings; user-visible rule and limits.
  - The recipes cover:
    - list notebooks;
    - list the sections of a notebook by name;
    - find a page by title;
    - read a page as text;
    - read an image;
    - full-text search;
    - get a link;
    - walk up from a page ID.
  - The XML notes cover: Notebook/SectionGroup/Section/Page; `ID`, `name`, `isInRecycleBin`, `locked`, `pageLevel`; Outline/OE/T with CDATA HTML; Table/Row/Cell; Image with CallbackID; InsertedFile; Tag and TagDef; Meta; and skipping recycle-bin section groups (`isRecycleBin`). -- the deliverable.
- [x] `tests/OneNoteMcp.Tests/PluginPackagingTests.cs` -- add tests:
  - `GuideSkill_HasFrontmatterNamedGuide`: the YAML block's first lines, with `name: guide` and a non-empty `description`;
  - `GuideSkill_MentionsEveryContractTool`: each `tools[].name` from `tool-contract.json` appears as `mcp__plugin_onenote_desktop__<name>`;
  - `GuideSkill_NamesOnlyContractTools`: every `mcp__plugin_onenote_desktop__\w+` in the skill is in the contract. -- the drift guard.

**Acceptance Criteria:**
- Given the staged plugin, when `claude plugin validate --strict dist/stage/onenote` runs, then it passes and the skill is found.
- Given a new tool added to the contract snapshot, when the unit tests run, then `GuideSkill_MentionsEveryContractTool` fails until the skill covers it.
- Given the plugin-dev `skill-reviewer` agent, when it reviews `plugin/skills/guide/SKILL.md`, then it reports no blocking issues, and its suggestions are triaged.
- Given Claude Code loaded with the packed zip (`--plugin-dir`) and OneNote running, when asked "list the sections of the MCP Test notebook", then the session uses `get_hierarchy` as the skill describes (notebooks scope to find the ID, then sections scope from that ID, or children) and answers with the section names.

## Implementation Notes

- 2026-11-05, Phill away: checkpoint 1 was approved by the agent, with no open questions. The Design Notes assumptions stand.
- The skill-reviewer (plugin-dev) found 1 blocking issue, the `InvalidXml` advice on reads, and made 11 suggestions. All were applied before the quick review.
- The build used a scratch `--artifacts-path`, because a running Debug `OneNoteMcp.exe` locks `src/OneNoteMcp.Server/bin/Debug`. That process was not stopped.
- Final verification:
  - build: 0 warnings;
  - tests: 834/834;
  - pack, then `claude plugin validate --strict dist/stage/onenote`: passed.
- End to end: a headless `claude -p --plugin-dir dist/onenote-0.4.0-rc.4-win-x64.zip` session, asked to list MCP Test's sections, auto-loaded `onenote:guide`. It called `get_hierarchy` with no arguments, then `get_hierarchy` with MCP Test's ID and `scope: sections`, and answered "Seed, Fixtures", leaving out the recycle bin. No other notebook was read below notebook level.
- Not done here: bumping `<Version>` and releasing. The skill ships with the next release string.
- These claims rest on the schema and OneMore, not on our fixtures:
  - lists (`one:List`);
  - the image `alt` attribute;
  - "a page ID changes when moved to another section".

## Design Notes

Assumptions, made while Phill was away:
- The skill is model-invoked, with no `disable-model-invocation`, so OneNote requests load it automatically, and `/onenote:guide` still works by hand.
- The `retryable` policy, at most one automatic retry for `ComBusy` or `Timeout` reads, is the skill's own choice. AD-9 says when retrying can succeed, not how often.
- Size advice is provisional: spike 1.15 will measure result sizes. Until then the skill says to prefer narrow scopes and to start from a section, not the root, for pages scope.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, including the three new tests.
- `pwsh -File scripts/pack-plugin.ps1; claude plugin validate --strict dist/stage/onenote` -- expected: pass.

**Manual checks (if no CLI):**
- A headless `claude -p --plugin-dir dist/onenote-<ver>-win-x64.zip` session asked to list MCP Test's sections. Only the MCP Test notebook is read.

## Review Triage Log

Pass 1 (quick lens): 6 findings. Verdicts: 1 medium, 4 low, 1 false. Routes: 5 patch, 0 defer, 1 rejected.

| # | Finding | Verdict | Route | Evidence / action |
| --- | --- | --- | --- | --- |
| 1 | `GuideSkill_MentionsEveryContractTool` substring match: `get_hierarchy` is satisfied by `get_hierarchy_parent` | medium | patch | `Assert.Contains` is ordinal substring; the drift-guard AC fails for prefix names. Patched to whole-name match. |
| 2 | "When to use" section doesn't say when the guide applies | low | patch | The section held only the read-only policy. One sentence added. |
| 3 | Failure envelope omits `meta.correlationId`, which the skill later says to quote | low | patch | AD-9 shape includes `meta:{correlationId}`. Added. |
| 4 | `InvalidQuery` row cites an empty query as a cause | low | patch | `SearchResults.Start` raises it only for the start-node rule; else COM `0x80042019`. Clause dropped. |
| 5 | `InvalidXml` row says it means only a server-side parse failure | low | patch | `HResultMap.cs:97` also maps `0x80042000/01/20`. Reworded; guidance unchanged. |
| 6 | ACs 1, 3, 4 have no evidence in the diff | false | reject | Evidence is run output, not diff content. The implementer ran `claude plugin validate --strict dist/stage/onenote`, the skill-reviewer (1 blocking and 11 suggestions, all applied) and a headless `--plugin-dir` session. Verification is rerun after the patches. |
