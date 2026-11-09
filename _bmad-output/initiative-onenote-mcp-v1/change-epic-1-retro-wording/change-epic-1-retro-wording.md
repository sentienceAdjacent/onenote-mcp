---
type: sprint-change-proposal
status: approved
approved: '2026-11-09 (Phill; Fable final review run, F1–F8 and F10–F12 applied)'
created: '2026-11-09'
trigger: epic 1 retrospective, findings C1, C2, C3, A2, A3
scope: minor
mode: batch
decisions: 'C2 (b), A3 (b) (Phill, 2026-11-09); Fable findings F1–F8 and F10–F12 applied, F9 not (Phill, 2026-11-09)'
---

# Sprint Change Proposal: epic 1 retrospective wording

## 1. Issue summary

The epic 1 retrospective found five places where the epic, the initiative or the spine says something the shipped code doesn't do. None of them is a defect: the code is what was decided along the way, and the documents weren't updated. Phill sent them to correct-course (retro decision 3, 2026-11-09).

| Id | What the document says | What the code does | Evidence |
|---|---|---|---|
| C1 | Epic Done-when 2 and initiative R1: the returned XML is "unchanged". The README and the guide skill say the same. | AD-4, amended in 1.16, lets search remove pages, AD-21 withhold protected pages, and AD-20 deliver a result in parts. | spine AD-4; `epic-read-only-release.md:36`; `initiative-onenote-mcp-v1.md:28`; `README.md:30`; `SKILL.md:85` |
| C2 | Done-when 2 and AD-17: reads return the basis in `meta`, for every target. | Only page reads return `meta.basis`. No hierarchy use case sets `Basis`. | `GetPageContentUseCase.cs:42-43`; spine AD-17 |
| C3 | Epic Boundaries: one `NavigateTo`, "for the opt-in image fetch". | `navigate_to` (1.21) is a general tool: it shows any notebook, section group, section or page, and the skill calls it whenever the user asks to see something. Epic 3 and initiative R9 still list `NavigateTo` as theirs. | `NavigateToTool.cs:23-33`; `SKILL.md:35,221`; `epic-round-3-app-state-and-ui.md:17` |
| A2 | AD-2: "No other type references the COM interop types." | `StaThread`, `ComGateway`'s worker, holds and releases `IApplication`. `ComGateway` creates it. | `StaThread.cs:28,58-70,120-128`; `ComGateway.cs:454` |
| A3 | Spine diagram: Server depends on Core and Com only. "The arrows are the only dependencies allowed." | The grant CLI calls `OneNoteMcp.Xml`'s `EncryptedSections.Find` directly, through Core's project reference. Core's `ReadAccess` and `GetHierarchyUseCase` already call the same function. | `Cli/GrantCommand.cs:5,227-237` |

## 2. Impact analysis

- **Epics.** Epic 1 is done; only its Done-when 2 and Boundaries wording change. Epic 2 (safe writes) inherits C2's answer, because its hierarchy writes compare a node's `lastModifiedTime`; its Notes now say so. Epic 3 loses `NavigateTo` from its scope, as it has shipped. Epic 4 isn't affected. No epic is added, removed or resequenced.
- **Stories.** None reopen. A3 joins the "epic 2 first refactor" group in `deferred-work.md`.
- **Artifacts.** The spine (AD-2, AD-17) and its memlog, the README and the guide skill, the initiative (R1, R9), the epic 1, epic 2 and epic 3 files, and `deferred-work.md`.
- **Code.** None now. A3's refactor is deferred.

## 3. Approach and decisions

Direct adjustment: amend the wording to match the code. Risk is low: wording only, and AD IDs stay stable.

- **C2 (Phill, 2026-11-09): option (b).** A notebook's, section group's or section's basis is the `lastModifiedTime` attribute already on its element in the hierarchy XML. Hierarchy reads add no `meta.basis`, because one result can hold many nodes, and the caller passes `basis: {lastModifiedTime}`. Every node in the MCP Test hierarchy fixtures carries the attribute. Option (a) was rejected: leaving it for epic 2 only postpones the same answer.
- **A3 (Phill, 2026-11-09): option (b).** The grant CLI's call moves behind Core in the epic 2 first refactor, and the spine stays. Which sections count as protected is AD-21 policy, and the paradigm puts policy in Core, which already owns both the call and the `InvalidXml` mapping that `GrantCommand.cs:234-237` repeats. Option (a) was rejected: adding a Server → Xml arrow would let every future Server type reach Xml, to save about 15 lines once. Until the refactor lands, the code departs from the diagram, and the deferred entry records that.

## 4. Changes, as landed

### C1: "unchanged" XML

- **Epic 1, Done when 2:** "Every R1 and R2 tool returns the AD-9 envelope. Its XML is OneNote's own, changed only as AD-4 allows: search removals, protected pages withheld (AD-21) and delivery in parts (AD-20). Page reads return the basis (`lastModifiedTime`, `pageHash`, `elementHashes`) in `meta.basis`. For a notebook, section group or section, the basis is the `lastModifiedTime` already on its element in the XML (AD-17)." It replaces "the XML is unchanged, and the basis (…) is in `meta`".
- **Initiative R1:** "Returned XML is OneNote's own, changed only as AD-4 allows, and degradations are reported in `meta`. Reads return the basis (AD-17): a page's in `meta.basis`, and a hierarchy node's as the `lastModifiedTime` on its element."
- **README line 30:** "Results are always OneNote's own XML, so tables, tags, ink, images and attachments survive the trip. The server never edits it: it only leaves out whole pages, such as recycle-bin pages in search results and pages of a password-protected section you haven't granted, and the result names the pages it filtered or the sections it withheld."
- **Guide skill line 85 (Fable F1):** "It's OneNote's own XML: the server never edits it, and only leaves out whole pages, such as recycle-bin pages in search results and pages of a password-protected section the user hasn't granted. The result names the pages it filtered (`meta.filtered`) or the sections it withheld (`meta.withheld`)." It replaces "The server passes it through unchanged."

### C2: AD-17's read side

The "Reads return the basis" bullet keeps its source-by-target rule. Two of its sub-bullets now read:
- "Page reads return the basis as one object, `meta.basis: {lastModifiedTime?, pageHash?, elementHashes?}`. A field is left out only where OneNote's XML lacks it or AD-20 says so." The optional marks stay, as before (Fable F3), because AD-20's index response leaves out `elementHashes`.
- "Hierarchy reads add no `meta.basis`, because one result can hold many nodes. A notebook's, section group's or section's basis is the `lastModifiedTime` attribute on its element in the returned XML, and the caller passes it as `basis: {lastModifiedTime}`. A caller holding only an ID, such as one from `get_hierarchy_parent`, reads the node with `get_hierarchy` scope `self`. An oversized hierarchy result's index entries carry no `lastModifiedTime`, so the caller reads the node's part with `read_result` to get it." (Fable F10, F11.)

**Epic 2 Notes (Fable F12):** a dated Decision line records the rule and points here.

### C3: `navigate_to` is a general tool

- **Epic 1, Boundaries:** "the user-visible `navigate_to` tool (entry 21, bound by AD-11) … `navigate_to` shows any notebook, section group, section or page when the user asks. It came in for the opt-in image fetch, and it's all of `NavigateTo` the spine allows (`fNewWindow` stays false, AD-11), so epic 3 doesn't build it again." (Fable F2.)
- **Epic 1, Notes:** a dated Decision line covering C1–C3. It ends "epic 3 no longer owns `NavigateTo`, and initiative R9 marks it shipped" (Fable F7).
- **Epic 3, scope:** "the user-visible calls `NavigateToUrl`, `QuickFiling` and `Windows` … `NavigateTo` shipped in epic 1 as `navigate_to` (story 1.21)".
- **Initiative R9:** "`NavigateTo` (shipped in epic 1 as `navigate_to`)".
- **README line 28 and the guide skill line 35 (Fable F6):** "a notebook, section group, section or page".

### A2: AD-2's interop rule

"The COM interop types are `internal` to `OneNoteMcp.Com`. Inside it, only `ComGateway` and its `StaThread` worker reference them: `ComGateway` creates the `IApplication` and makes every call; the worker holds and releases it, on its own STA thread. The test projects also see them through `InternalsVisibleTo`, for the vtable smoke test and `FakeApplication` (AD-16)." (Fable F5.) It replaces "No other type references the COM interop types."

### A3: Server → Xml

The spine doesn't change. `deferred-work.md` gets an entry in the "epic 2 first refactor" group to move the grant CLI's `EncryptedSections.Find` call behind Core.

### Bookkeeping

- The spine memlog records three decisions: AD-2, AD-17 and A3. C1 and C3 change only epic, initiative and README wording, so they have no spine decision; the epic 1 Notes record them. A "Fable review applied" event follows.
- `deferred-work.md` resolves the correct-course entry and points here.
- Not applied: Fable F9, the `[ADOPTED]` tags on AD-17, AD-19, AD-20 and AD-21. It predates this change and belongs in a spine-wide pass.

## 5. Implementation handoff

- **Scope:** minor. The edits are applied on branch `docs/retro-wording`. The Fable final review ran on them, and Phill chose which findings to fix. All are applied except F9.
- **bmad-ticket changes:** none to the breakdowns. Epic 3's scope line changed; its `tickets.toml` has no `NavigateTo` entry yet.
- **Success criteria:** every C1–C3, A2 and A3 row in the retrospective is reconciled or has a deferred entry; the staged diff passes the notebook-name grep; one PR.
