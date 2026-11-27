---
title: 'Plugins repo checks its validator pin matches this repo'
type: 'chore'
ticket: '25'
created: '2026-11-27'
status: 'built'
baseline_revision: '09298b4c525a5074753911de6d66cb93584fd670' # sentienceAdjacent/plugins (chore/validator-pin-check); this repo's base is e17c4fac8d17702a56bf5145c571952d78beada0
route: 'oneshot'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** onenote-mcp's CI (`.github/workflows/ci.yml`, `release.yml`) and the catalog repo `sentienceAdjacent/plugins` (`.github/workflows/ci.yml`, `sync.yml`) each pin the Claude Code version that runs `claude plugin validate` (2.1.318 today). Only comments keep the four pins in step; the check was moved to the plugins repo (D32) and never added (D13).

**Approach:** Add `scripts/check-validator-pin.sh` to the plugins repo and run it as a step in its `ci.yml`. The script reads every `@anthropic-ai/claude-code@<version>` pin from its own `ci.yml` and `sync.yml` and from onenote-mcp's `ci.yml` and `release.yml` on `main` (fetched over HTTPS from raw.githubusercontent.com, no token: the repo is public), and fails unless every file has a pin and all pins are equal, naming each file's pin. An environment override for the upstream base URL (any URL curl reads, `file://` included) lets the script be tested locally against fixture files.

**Decisions:** approved by Phill with the ticket, 2026-11-27. The check lives only in `ci.yml` (pushes and PRs, plus a manual `workflow_dispatch` to re-check after onenote-mcp bumps its pin); `sync.yml` is left alone so a pin drift never blocks a release from being pinned. No change to onenote-mcp's release pipeline.

</frozen-after-approval>

## Implementation Notes

Oneshot: one small shell script plus one CI step and one trigger, well under 100 lines, in a repo with no build. The code lands in `sentienceAdjacent/plugins` on branch `chore/validator-pin-check`; only this plan lands in onenote-mcp.

Files changed in `sentienceAdjacent/plugins`: `scripts/check-validator-pin.sh` (new), `.github/workflows/ci.yml` (new step plus `workflow_dispatch`), `.github/workflows/sync.yml` (comment only), `README.md` (a short "Validator pin" section).

- The script reads only non-comment lines, and every other mention of `@anthropic-ai/claude-code` must carry an exact semver (optional prerelease); a bare install, a dist-tag such as `latest` or a range fails by name. A package whose name only starts the same is skipped. These came from the review (below).
- Local fixture runs (2026-11-27, scratchpad, `PIN_LOCAL_ROOT` + `PIN_UPSTREAM_BASE=file:///...`): 13 cases, all as expected. Pass: matching pins; an old version in a comment; another package sharing the prefix. Fail: upstream pin differs; upstream ci/release disagree; local ci/sync disagree; a file with no pin; a file of comments only; unreachable upstream; two versions in one file; a bare install beside a pinned one; `@latest`; `@^2.1.318`. Live run against onenote-mcp main: exit 0, all four at 2.1.318.

## Review Triage Log

Quick lens, pass 1 (high 0, medium 2, low 1, false 1, maybe-false 0):
- medium, patch -- a bare unversioned install in a file that also names a pinned version passed (reviewer reproduced it). Fixed: every non-comment mention must name an exact version.
- medium, patch -- comment lines were scanned, so an old version in a comment next to the pin would fail CI. Fixed: comment lines are skipped.
- low, patch -- `@latest` counted as a pin and a range gave a misleading "no pin" message. Fixed by the exact-version rule; both now fail naming what they install.
- false -- "last verification item not done": the branch was not yet committed or pushed at review time; it is pushed with `-u origin chore/validator-pin-check` after this.

## Verification

**Commands (in the plugins worktree):**
- `bash -n scripts/check-validator-pin.sh` -- expected: parses.
- `bash scripts/check-validator-pin.sh` -- expected: exit 0 against the live onenote-mcp main (2.1.318 everywhere).
- Local fixtures via the upstream-base override: matching pins exit 0; an upstream pin that differs, upstream `ci.yml` and `release.yml` disagreeing, a local pin that differs, and a file with no pin each exit non-zero with a message naming the files; an unreachable upstream exits non-zero.
- After pushing, the branch's own `ci` run (it triggers on push) passes.
