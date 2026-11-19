---
id: 10
type: story
title: "Download a section before packing its notebook"
parent: none
covers: [R8]
after: []
assignee: ""
refined: false
hitl: true
risk: medium
estimate: ""
---

# Download a section before packing its notebook

## Description

Story 3.7 refuses a notebook package (`publish`, `onepkg`) when any section in the notebook has `areAllPagesAvailable="false"`. The 3.7 sitting (S3, 2026-11-17) found that packing such a notebook raises OneNote's modal "cannot pack these sections because some sections have not yet been fully synced and could not be downloaded. These pages will not be saved. Continue anyway?" dialog, which blocks the COM call, and that answering Yes writes the package without those pages. Today the user has to open each such section in OneNote by hand before trying again.

First, a probe: does `SyncHierarchy` on a section with `areAllPagesAvailable="false"` actually download its pages, so that a fresh `GetHierarchy` then shows the attribute gone or `true`? How long does it take, does it take focus or raise a dialog, and does it behave the same for a OneDrive personal and a OneDrive for Business notebook? If it works, offer a "download first" step for `publish`: the shape (a separate tool, or an opt-in argument) is decided in the plan.

The probe needs Phill's approval before it runs against a real section, because the only sections known to lack pages are in his own notebooks. As for 3.7's S3, a probe of a real section records only the attribute before and after and the times, never its name or ID.

## Acceptance Criteria

Verify: the probe's findings are recorded (attribute before and after `SyncHierarchy`, time taken, focus and dialogs, both storage kinds where possible); and if `SyncHierarchy` downloads the pages, a "download first" step makes a notebook that 3.7 refused pack with no dialog, in a sitting.

## Boundaries

- Must not change: 3.7's refusal of a notebook holding a locked or not fully downloaded section, unless the download step makes it pass first.
- Never answer OneNote's pack dialog for the user, and never pack a notebook that would drop pages.
- No real notebook names or content in any file, commit, log or plan.

## References

- parent — none (standalone backlog leaf)
- `_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/story-publish-notebooks-and-section-groups-plan.md` — story 3.7, its S3 finding and the refusal
- AD-7 Formats by scope and AD-11 in the architecture spine

## Notes

- Filed 2026-11-17 at Phill's request, with 3.7's decision to refuse `areAllPagesAvailable="false"` up front.
- Open from 3.7, untested: whether a section-scope or page-scope `publish` of a section that isn't fully downloaded raises the same dialog or drops pages. This probe can check it in the same sitting.
- 2026-11-17 sitting (notebook B, one OneDrive for Business section): `SyncHierarchy`, opening the section and Shift+F9 all left `areAllPagesAvailable` false. The flag marks a page stub added from another computer that never synced, which nothing on this PC can download, so "download first" isn't possible and 3.7's refusal stays. The probe (`--probe-section-sync`, PR #87) stays in the stress harness for reuse. Follow-up: correct the refusal's `userAction` (see backlog 11).
