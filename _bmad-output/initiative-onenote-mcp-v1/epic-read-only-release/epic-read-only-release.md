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
- Unknown: how the plugin fetches the exe (a `.mcpb` bundle or a launcher), and code signing and SmartScreen. Owned here, at inception. Research (2026-11-03, draft): `research-plugin-exe-delivery.md` recommends an `archive` plugin source, a release zip pinned by SHA-256, and raises the local-development question for story 1.10.
- Unknown: whether the vendored XSD accepts partial-update XML (AD-16). It's proven here against fixtures, and epic 2 depends on the answer.
- Decision (2026-11-03, Phill): OneMore is our main prior art. We learn from its code and docs and write our own code, credit it in the README, and check it first when stuck. Findings are in `research-onemore-lessons.md`.
- Unknown (story 5): reconnect triggers. Use `0x800706BA` and `RPC_E_DISCONNECTED` plus `InvalidComObjectException`, but not `0x800706BE`, which can mean bad XML. Activation needs the same RPC handling as calls. Release `Windows`/`Window` wrappers explicitly. Watch for OneMore's "blank second instance" (not reproduced here). Research note rows 7, 10 and 11.
- Finding (2026-11-03, story 1.2 manual check): closing OneNote's window leaves `ONENOTE.EXE` running and resident with no window, even after our server releases its COM reference. COM reads work against it normally. (Spike 1 saw the same, findings line 123.) Story 5 keeps the process check without requiring a window: no operation is known to need a visible window. Unverified, for the stories that add them: whether `Publish` works against a COM-started `-Embedding` instance (OneMore says it needs an interactively launched OneNote; this only matters with `allowSilentActivation` on), and what `NavigateTo` does when no window is open. The live `OneNoteNotRunning` check needs the process actually gone, so it moves to story 5's integration suite.
- Unknown (story 6): whether the XSD accepts partial-update XML. OneMore validates pruned updates against it at runtime (evidence, not proof). Also record the installed type library's `xs2026` schema, which adds `Cell@colSpan`/`rowSpan` and an `AuthorDef` table. Research note rows 6 and 9.
- Decision (2026-11-03, Phill; AD-17 amended): a page's basis `lastModifiedTime` is the root value from the page XML of its `GetPageContent` read, never the hierarchy's. OneMore's "read time" defect didn't reproduce here (12-page probe, `spikes/probes-2026-11-03/`). Story 8 returns `meta.basis` per AD-17. Research note row 3.
- Unknown (story 9): recycle-bin filtering must check ancestor section groups and sections; symbols in queries; `FindMeta` reliability; a reopened cloud notebook refills over about 90 s, so results can be partial. Research note rows 4 and 8.
- Hands-off to later epics: the gateway, the envelope and `ErrorCode`, the settings type (read side), the XML model with `ElementHash`, `FakeGateway`, the integration harness, the release pipeline, the plugin and the skill. Later epics extend the skill and README, and cut releases through this pipeline.
