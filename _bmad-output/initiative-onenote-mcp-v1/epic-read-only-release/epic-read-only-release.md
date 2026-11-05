---
type: epic
title: "Read-only release"
parent: initiative-onenote-mcp-v1
covers: [R1, R2, R3, R7]
after: []
assignee: ""
risk: high
---

# Read-only release

## Description

This is the opening epic and the platform baseline:
- the repo scaffold and CI;
- the COM binding, proven first;
- the single-STA gateway;
- the result envelope and error map, settings, logging and test harnesses;
- every read and search tool;
- the plugin, skill, release pipeline and README, so a read-only server ships as a GitHub release that installs through the plugin.

Spike 1 (done) sits here as the evidence the gateway is built on.

## Outcome

Phill installs the plugin from a GitHub release and Claude reads and searches his notebooks reliably. That tests the packaging route early, and gives real use before any write exists.

## Requirements

This epic reuses the initiative's lines R1 (read), R2 (search), R3 (robust operation) and R7 (distribution) as written in `initiative-onenote-mcp-v1.md`. It owns all of each. Later epics only extend R7, by updating the skill and README and cutting releases.

## Done when

1. `/plugin marketplace add` and `/plugin install` on a machine with OneNote running install the skill and a pre-built exe from a GitHub release. `get_hierarchy` then lists the notebooks.
2. Every R1 and R2 tool returns the AD-9 envelope, the XML is unchanged, and the basis (`lastModifiedTime`, `pageHash`, `elementHashes`) is in `meta`.
3. The integration suite shows the AD-3 behaviour against real OneNote: OneNote not running, a modal dialog open, an integrity mismatch refused, a reconnect after OneNote restarts, and a hung call replaced.
4. CI runs the unit suite against `FakeGateway` fixtures and `claude plugin validate`. Both pass on the release commit.
5. An agent following the README's checklist installs it, and `uninstall --purge` removes settings and logs.

## Boundaries

All four projects (Server, Core, Xml, Com), both test suites, CI, the plugin and marketplace, the `onenote` skill, the release workflow, and `README.md`. No write tools: the classes other than `Read` are left to later epics, and so is the AD-18 mutex.

## References

- parent — _bmad-output/initiative-onenote-mcp-v1/initiative-onenote-mcp-v1.md, Requirements R1, R2, R3, R7
- architecture — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md, AD-1–AD-5, AD-9–AD-16, AD-17 (read side), AD-18 (`correlationId` only), AD-19
- spike findings — _bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/spike-what-can-the-onenote-desktop-com-api-do-on-this-machine-findings.md
- constraint — CLAUDE.md, section OneNote COM constraints
- research — _bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/research-onemore-lessons.md
- product — README.md, sections Install, For agents installing this

## Notes

- Decision (2026-11-03, Phill): this epic opens with the scaffold plus the AD-1 vtable smoke test as story 1, the first story that touches COM. The AD-3 gateway lifecycle follows.
- Decision (2026-11-03, Phill): distribution belongs to this epic, so every later epic ships through its pipeline.
- Decision (2026-11-03, Phill): the breakdown is entries 2–14 in `tickets.toml`, and the tracer bullet is entry 2. Lanes: 4 → 5 → 6, then 7, 8 and 9 in parallel, each adding only its own tool files. Entry 10 can run beside 3–9.
- Decision (2026-11-03, Phill): the repo is `pmcormier/onenote-mcp` ("OneNote MCP"). It's private from entry 2 and goes public in entry 13, before the clean-machine install, because release downloads from a private repo need a token.
- Decision (2026-11-03, Phill): test data comes only from a throwaway MCP Test notebook plus one sanitised cloud-image capture. Nothing from Phill's notebooks goes into the repo.
- Decision (2026-11-03, Phill): entry 13's clean-machine install is the epic's end-to-end check, so there's no separate end-to-end suite story.
- Resolved (2026-11-04): the plugin fetches the exe as an `archive` plugin source, a release zip pinned by SHA-256, with `claude --plugin-dir <zip>` as the local loop (`research-plugin-exe-delivery.md`, final). AD-19 allows `${CLAUDE_PLUGIN_ROOT}`. Story 1.11 settles how `marketplace.json` gets each release's `sha256`, and owns code signing and SmartScreen, still unknown.
- Unknown: whether the vendored XSD accepts partial-update XML (AD-16). It's proven here against fixtures, and epic 2 depends on the answer.
- Decision (2026-11-03, Phill): OneMore is our main prior art. We learn from its code and docs and write our own code, credit it in the README, and check it first when stuck. Findings are in `research-onemore-lessons.md`.
- Unknown (story 5): reconnect triggers. Use `0x800706BA` and `RPC_E_DISCONNECTED` plus `InvalidComObjectException`, but not `0x800706BE`, which can mean bad XML. Activation needs the same RPC handling as calls. Release `Windows`/`Window` wrappers explicitly. Watch for OneMore's "blank second instance" (not reproduced here). Research note rows 7, 10 and 11.
- Finding (2026-11-03, story 1.2 manual check): closing OneNote's window leaves `ONENOTE.EXE` running and resident with no window, even after our server releases its COM reference. COM reads work against it normally. (Spike 1 saw the same, findings line 123.) Story 5 keeps the process check without requiring a window: no operation is known to need a visible window. Unverified, for the stories that add them: whether `Publish` works against a COM-started `-Embedding` instance (OneMore says it needs an interactively launched OneNote; this only matters with `allowSilentActivation` on), and what `NavigateTo` does when no window is open. The live `OneNoteNotRunning` check needs the process actually gone, so it moves to story 5's integration suite.
- Unknown (story 6): whether the XSD accepts partial-update XML. OneMore validates pruned updates against it at runtime (evidence, not proof). Also record the installed type library's `xs2026` schema, which adds `Cell@colSpan`/`rowSpan` and an `AuthorDef` table. Research note rows 6 and 9.
- Decision (2026-11-03, Phill; AD-17 amended): a page's basis `lastModifiedTime` is the root value from the page XML of its `GetPageContent` read, never the hierarchy's. OneMore's "read time" defect didn't reproduce here (12-page probe, `spikes/probes-2026-11-03/`). Story 8 returns `meta.basis` per AD-17. Research note row 3.
- Decision (2026-11-05, Phill; AD-6, AD-7 and AD-17 amended): AD-17's hash also ignores selection, authorship and view state, and keeps layout. AD-6's parent chain ends at the empty string OneNote returns at a notebook. `FormatDate` is dropped from AD-7, R1 and story 7. See `change-story-1-6-spine-amendments/`.
- Unknown (story 9): recycle-bin filtering must check ancestor section groups and sections; symbols in queries; `FindMeta` reliability; a reopened cloud notebook refills over about 90 s, so results can be partial. Research note rows 4 and 8.
- Decision (2026-11-04, story 1.6): the vendored 2013 XSD accepts partial-update XML for the shapes tested. AD-16's precondition for making it a hard gate is met for `UpdatePageContent` shapes; it stays open for `UpdateHierarchy` and `DeletePageContent`, which epic 2 must prove before gating them. Evidence in `tests/OneNoteMcp.Tests/XmlModelTests.cs`: `PartialUpdate_NewOutlineWithNoIds_Validates`, `PartialUpdate_OneExistingOeWithChangedText_Validates` and `PartialUpdate_PageWithTitleOnly_Validates` pass, and `PartialUpdate_PlaceholderObjectId_FailsThePattern` shows a hand-built ID still fails the `ObjectID` pattern. The capture's five seeds (nested OEs with a tag and Meta, a table, a local PNG, a two-Outline probe page, a subpage) are partial `UpdatePageContent` documents with no objectIDs: OneNote accepted all five and all five validate (`manifest.json` `probe.seedResults`). Not covered: `DeletePageContent`, `UpdateHierarchy` payloads and `xs2026`. The `xs2026` schema (`Cell@colSpan`/`rowSpan`, `AuthorDef`) stays deferred to the spike before epic 2's write stories.
- Hands-off to later epics: the gateway, the envelope and `ErrorCode`, the settings type (read side), the XML model with `ElementHash`, `FakeGateway`, the integration harness, the release pipeline, the plugin and the skill. Later epics extend the skill and README, and cut releases through this pipeline.
