---
id: 11
type: story
title: "publish: let OneNote skip unsynced placeholder pages (opt-in)"
parent: none
covers: [R8]
after: []
assignee: ""
refined: false
hitl: true
risk: medium
estimate: ""
---

# publish: let OneNote skip unsynced placeholder pages (opt-in)

## Description

Story 3.7 refuses a notebook package (`publish`, `onepkg`) when any section in the notebook has `areAllPagesAvailable="false"` (`NotSupported`, before `Publish`), because packing it raises OneNote's modal "cannot pack these sections … Continue anyway?" dialog, and a Yes writes the package without those pages (3.7 sitting S3). The backlog 10 sitting (2026-11-17) found what the flag means: a page stub added from another computer that never synced ("Page Not Yet Available" in OneNote; `GetHierarchy` leaves the stub out). Nothing on this PC can download it, so the refusal's advice to "open those sections in OneNote so they download" is wrong.

Two changes, decided by Phill (2026-11-17):

1. **The default stays the refusal, with corrected advice.** `PublishUseCase.NotebookNotDownloadedUserAction` says that a page in the section hasn't synced from the computer that added it, so packing would drop it: sync it from that computer, or delete its placeholder page in OneNote, then try again; or export the other sections one by one (`get_hierarchy` with scope `sections` lists them). The message keeps naming section IDs only. The publish tool's description and the guide skill's `NotSupported` row follow.
2. **An opt-in to let OneNote skip them.** A new optional `publish` argument, `allowUnsyncedPages: true`, valid only for a notebook package. With it, the server skips this refusal (never the locked-section one) and calls `Publish`. OneNote shows its "cannot pack … Continue anyway?" dialog, and the user answers it:
   - **Yes:** OneNote writes the package without the placeholder pages, as S3's notebook B showed. The success carries a new warning naming the affected section IDs (IDs only), saying their unsynced pages aren't in the package.
   - **No answer before `timeouts.publishSeconds`:** the existing `Timeout` / `outcomeUnknown` path.
   - The server never answers or automates the dialog (AD-11), and the skill tells the model to warn the user a dialog will appear and that they must answer it.

## Acceptance Criteria

- Without `allowUnsyncedPages`, a notebook with a section whose `areAllPagesAvailable` is `false` is refused with `NotSupported` before `Publish`, as today, with the new `userAction`.
- With `allowUnsyncedPages: true`, the same notebook is packed once the user answers Yes, and the result carries the new warning with the affected section IDs.
- `allowUnsyncedPages` on anything other than a notebook package is `InvalidArgument`; a locked section is still `SectionLocked`.
- The plan settles the open items below before the build.

Verify: unit tests on the FakeGateway for the default refusal and its `userAction`, the argument's validation, the warning on success and the timeout path; then, in a sitting with Phill's approval on a real section that has an unsynced placeholder page, a notebook package with `allowUnsyncedPages: true` shows the dialog, Yes writes a package without the placeholder (size and seconds recorded only), and No gives the outcome the plan decided.

## Boundaries

- Must not: answer, dismiss or automate OneNote's dialog; change the default refusal's behaviour; relax the locked-section refusal; record any real notebook, section or page name or ID in a file, commit, log or plan.
- Needs a spine amendment before the build: AD-7 Formats by scope records the refusal with no opt-in, and the new warning code goes in AD-9.
- Open items for the plan:
  - What `Publish` returns when the user answers No (probably an HRESULT to map to a code and `userAction`).
  - Whether a section- or page-scope export of such a section raises the same dialog or silently drops the page (untested; backlog 10's sitting had no second section to try).
  - Whether the warning can say how many pages were left out (`GetHierarchy` omits the stubs, so probably not).
- The sitting needs a real section with an unsynced placeholder page and Phill's approval. Phill is deleting the current one, so a new stub may have to be made (a page added on another computer while that computer is offline), or the sitting waits until one exists.

## References

- parent — none (standalone backlog leaf)
- `src/OneNoteMcp.Core/UseCases/PublishUseCase.cs` (`NotebookNotDownloadedUserAction`, the refusal in `RefuseASectionThatWouldBlockThePackageAsync`), `src/OneNoteMcp.Server/Tools/PublishTool.cs`, `plugin/skills/guide/SKILL.md`
- `_bmad-output/initiative-onenote-mcp-v1/backlog/story-download-a-section-before-packing-its-notebook-plan.md` — backlog 10's Sitting findings
- `_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/story-publish-notebooks-and-section-groups-plan.md` — story 3.7, S3 and its follow-up line
- Spine AD-3 (publish timeout), AD-7 Formats by scope, AD-9, AD-11

## Notes

- Filed 2026-11-17 at Phill's request, after the backlog 10 sitting.
