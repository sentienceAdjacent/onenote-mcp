---
id: 11
type: story
title: "publish: a clearer refusal for a notebook with an unsynced placeholder page"
parent: none
covers: [R8]
after: []
assignee: ""
refined: false
hitl: false
risk: low
estimate: ""
---

# publish: a clearer refusal for a notebook with an unsynced placeholder page

## Description

Story 3.7 refuses a notebook package (`publish`, `onepkg`) when any section in the notebook has `areAllPagesAvailable="false"` (`NotSupported`, before `Publish`), because packing it raises OneNote's modal "cannot pack these sections … Continue anyway?" dialog, and a Yes writes the package without those pages (3.7 sitting S3). The backlog 10 sitting (2026-11-17) found what the flag means: a page stub added from another computer that never synced ("Page Not Yet Available" in OneNote; `GetHierarchy` leaves the stub out). Nothing on this PC can download it, so the refusal's message ("isn't fully downloaded to this PC") and its advice ("open those sections in OneNote so they download") are wrong.

The refusal stays exactly as it is; only its words change (Phill, 2026-11-17): the message says a page in the section hasn't synced from the computer that added it, so packing would leave it out; the `userAction` says to sync it from that computer, or delete its placeholder page in OneNote, then try again, or export the other sections one by one. The `publish` tool description and the spine's statements of the reason (AD-7 Formats by scope, AD-11 Exports, AD-21 Exports) follow. Messages keep naming section IDs only.

The ticket was filed with an opt-in, `allowUnsyncedPages`, that let the user answer OneNote's dialog. Phill dropped it on 2026-11-17 (Notes).

## Acceptance Criteria

- A notebook package holding a section with `areAllPagesAvailable` `false` (or `0`), at any depth, is still refused with `NotSupported` before `Publish`, with the new message and `userAction`; nothing else about the refusal changes.
- The tool description and the three spine statements give the corrected reason and advice.

Verify: `dotnet build OneNoteMcp.slnx -warnaserror` and `dotnet test tests/OneNoteMcp.Tests` pass, with the S3 refusal tests asserting the new message and `userAction` (seen failing first).

## Boundaries

- Must not: change the refusal's behaviour, code or order; add an opt-in; touch OneNote in tests; record any real notebook, section or page name or ID.
- Spine edits stay to the three statements of the reason, so they merge cleanly beside other lanes; the AD-7 operation-class table row (l.326) still says "not fully downloaded" and is a follow-up once those lanes merge.

## References

- parent — none (standalone backlog leaf)
- `src/OneNoteMcp.Core/UseCases/PublishUseCase.cs` (`NotebookNotDownloadedUserAction`, the refusal), `src/OneNoteMcp.Server/Tools/PublishTool.cs`
- `_bmad-output/initiative-onenote-mcp-v1/backlog/story-download-a-section-before-packing-its-notebook-plan.md` — backlog 10's Sitting findings
- `_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/story-publish-notebooks-and-section-groups-plan.md` — story 3.7, S3 and its follow-up line
- Spine AD-7 Formats by scope, AD-11 Exports, AD-21 Exports

## Notes

- Filed 2026-11-17 at Phill's request, after the backlog 10 sitting.
- 2026-11-17, Phill: build only the clearer refusal (message, `userAction`, tool description, spine wording). The `allowUnsyncedPages` opt-in is dropped: its plan review showed the dialog blocks the COM call, freezing writes (`ComBusy`) until the user answers, and a Yes after a timeout is silent. It can return as its own story if unsynced pages come up often.
