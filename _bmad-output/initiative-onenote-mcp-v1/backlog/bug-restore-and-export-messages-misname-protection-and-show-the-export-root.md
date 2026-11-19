---
id: 19
type: bug
title: "Restore calls a path-protected section password-protected, and FileExists messages show the export root"
parent: none
covers: [R8]
after: []
assignee: ""
refined: false
hitl: false
risk: low
severity: P3
estimate: ""
---

# Restore calls a path-protected section password-protected, and FileExists messages show the export root

## Description

Two messages say the wrong thing.
- `restore_from_recycle_bin` refuses a section that is protected only by its path (under an export root or the backups folder) as "password-protected", and gives password-only advice. The merge, move and publish refusals already word this by the reason.
- `publish` and `open_package` report `FileExists` with the full target path, export root included. The class that builds the target says its messages name only the caller's own path, never a root.

Both refusals stay as they are; only their wording changes.

## Reproduction

1. Restore: in a notebook whose folder is under an export root (unit fixture, as 3.15's tests set it up), soft-delete a section, then restore it with a `parentId` elsewhere. Actual: a message saying "password-protected section". Expected: the path reason ("is under an export root or the backups folder, so it is treated as password-protected").
2. Export: `publish` a page as `x.pdf` (a relative name) when that file already exists in the default export root. Actual: "Something already exists at <export root>\x.pdf". Expected: the caller's own path (`x.pdf`), as the class's documentation says. Or, if naming the target is wanted, the documentation changes to match.

## Cause Hypothesis

Restore decides "protected" with the shared protection rule, marker or path, but its refusal texts are fixed strings written before the path rule existed (story 3.15). The `FileExists` messages put the resolved target path, which includes the root, into the text, against the class's own documentation.

## Acceptance Criteria

Verify:
- Unit tests show restore's protected-section refusals use the path wording for a section protected only by its path, and the password wording for a marked one.
- `FileExists` from `publish` and `open_package` with a relative name doesn't contain the export root, or the class documentation and the README are changed to say it does, per the decision below.

1. **The expected behavior holds**
   **Given** a recycled section protected only by its path, or an export whose target already exists
   **When** the restore or the export is refused
   **Then** the restore message gives the path reason, and the `FileExists` message matches the class's documented rule
2. **Tests cover the condition found and fixed**
   **Given** the unit test suite
   **When** it runs
   **Then** a test that fails on the defect and passes on the fix covers each message
3. **Or: no change is needed, with proof**
   **Given** the reproduction
   **When** it is run on the current code
   **Then** the wording is already right, with the evidence recorded in Notes; this supersedes 1 and 2

## References

- retro — _bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/epic-round-2-external-and-structural-retrospective.md, findings V4 and V6; action item 7
- architecture — _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md, AD-9 (messages), AD-21 (protected by path)

## Notes

- Decision (Phill, 2026-11-19, Inbox): filed from the epic 3 retrospective, action item 7.
- Where: `RestoreFromRecycleBinWrite` (its protected refusals, around the `TargetOf` messages); `ExportTarget` (its two `FileExists` messages, and its class documentation line saying "never a root"). The shared wording helper is `Protection.Phrase`.
- Open question: for `FileExists`, should the message drop the root, or should the documentation change? `meta.outputPath` already shows the full target on success.
