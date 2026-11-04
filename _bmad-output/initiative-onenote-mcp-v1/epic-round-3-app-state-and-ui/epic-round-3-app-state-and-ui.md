---
type: epic
title: "Round 3: app state and UI"
parent: initiative-onenote-mcp-v1
covers: [R9]
after: []
assignee: ""
risk: medium
---

# Round 3: app state and UI

## Description

This epic covers the rest of the COM surface:
- `OpenHierarchy` on an existing notebook, `CloseNotebook`, `SyncHierarchy` and `SetFilingLocation`;
- the user-visible calls `NavigateTo`, `NavigateToUrl`, `QuickFiling` and `Windows`, called only when the user explicitly asks (AD-11);
- events, if they can be built;
- the remaining Application properties (`COMAddIns`, `LanguageSettings`): each gets a tool or a dated Decision not to expose it.

## Outcome

Claude can drive OneNote's app state ("open that notebook", "show me this page"), and every Application method is covered.

## Done when

1. Every `AppState` tool works against real OneNote. User-visible tools say so in their descriptions, and the skill calls them only when the user asks.
2. Events (`OnHierarchyChange`, `OnNavigate`) either ship as MCP notifications, or a dated Decision records why they don't.
3. Every OneNote Application method has a released tool, or a dated Decision recording why it isn't exposed.
4. It ships as a GitHub release through the epic-1 pipeline, with the skill and README updated.

## Boundaries

The `AppState` class, its tools, any event sink on the STA thread, and the skill guidance for user-visible calls. Out of scope: anything that writes content.

## References

- parent — _bmad-output/initiative-onenote-mcp-v1/initiative-onenote-mcp-v1.md, Requirement R9
- architecture — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md, AD-2, AD-7, AD-11, Deferred (events)

## Notes

- Waits on epic 3 because both change the class dispatch, the tool registration and the skill.
- Unknown: Microsoft says events can't be added in managed code, so this may need a native sink or be dropped.
- Unknown (OneMore lessons, `../epic-read-only-release/research-onemore-lessons.md` row 14): `NavigateToUrl` fails with `0x80042014` if called again within about 2 s, and with two windows open on one page the API targets the first window.
