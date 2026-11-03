Owner rule (engram-hub, 2026-10-21): Fable final review.
Very near the end of this effort, before any planning doc's status becomes final,
approved, complete or hardened (for bmad-ticket: when an initiative or epic is
finalized or published), and after the skill's own reviewer gate if it has one:
1. Launch an independent, read-only review with the Agent tool (model: fable,
   effort: high). Give it the doc, its companions and its sources of truth (spec,
   spine, trigger docs, the CLAUDE.md learned rules). Tell it to invoke the
   bmad-review skill (this prompt counts as the explicit "skill:bmad-review"
   directive), with also_consider = consistency with those sources of truth. Tell it
   to run every lens itself, one after another, and not to spawn subagents, so every
   lens runs on Fable at high effort. Also have it read and apply, read-only, the
   owning skill's own validation checklist (for example, bmad-architecture's
   good-spine checklist in references/reviewer-gate.md, or the PRD validation
   checklist). It must not use any BMAD skill that writes.
   It returns findings with severity, location, quoted evidence and a suggested fix.
2. Verify each finding against the source yourself. Drop any that don't hold up.
3. Show the owner only the confirmed findings, in plain English. The owner decides
   which to fix. Apply the agreed fixes, then set the status.
Never skip this step, including in headless runs. If Fable is unavailable, stop
and tell the owner instead of silently using another model.
