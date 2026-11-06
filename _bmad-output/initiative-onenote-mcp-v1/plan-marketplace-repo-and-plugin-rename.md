---
title: 'Separate marketplace repo and final plugin names'
type: 'chore'
ticket: ''
created: '2026-11-05'
status: 'built'
baseline_revision: 'c8fbc820a7e850d6c43094771cdfbf102ba2ef5f'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/story-release-pipeline-plan.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The plugin installs as `onenote-mcp@onenote-mcp`, its tools are `mcp__plugin_onenote-mcp_onenote__*`, and its catalog lives inside this repo. Phill wants simple, non-repeating names and one permanent marketplace that will host all his plugins and skills.

**Approach:** Rename the plugin to `onenote` and its MCP server key to `desktop`. Move the catalog to a new public repo `sentienceAdjacent/plugins` (marketplace name `sentienceadjacent`), which pins each plugin's release itself with its own token. Then release `0.4.0-rc.4` and pin the new marketplace to it. Install becomes `/plugin marketplace add sentienceAdjacent/plugins` then `/plugin install onenote@sentienceadjacent`.

## Boundaries & Constraints

**Always:**
- The marketplace repo pulls: a workflow (`workflow_dispatch` plus a daily schedule) reads onenote-mcp's releases, verifies the downloaded zip against the release's own `.sha256`, and commits the `archive` pin to its own `main` with its own `GITHUB_TOKEN`.
- The pin only moves forward by semver, and the all-zero sha means "never released".
- The release asset name stays `onenote-<ver>-win-x64.zip`.
- The future skill (story 1.12) is named `guide`, giving `/onenote:guide`.
- Release discipline from 1.11 is unchanged: tag checks, CI reruns, no version reuse.

**Never:**
- No token that can write across repos, and no PAT.
- Don't pin a pre-rc.4 release under the name `onenote`: those zips contain a plugin named `onenote-mcp`.
- Don't rename log file prefixes (`onenote-mcp-*.log`), test meta names or temp folders. They're product names, not the plugin name.
- Out of scope: the issue form and `OneNoteMcp report` (story 1.13), the real install test, archiving the private repo, the stress-test spike.

**Decisions (Phill, 2026-11-05):**
- Spine: amend AD-15's rule and the layout block in this build. The marketplace is `sentienceAdjacent/plugins`, and the skill is `guide`. Run a Fable review of the change before it is final.
- The plugins repo is MIT for its catalog and scripts. Each plugin keeps its own licence.
- Sync pins the newest release by semver, prereleases included. Revisit at 1.0.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| New release | onenote-mcp publishes v0.4.0-rc.4; sync runs | marketplace pins rc.4's url and sha in one bot commit | No error expected |
| Nothing new | sync runs; newest release already pinned | no commit, success | — |
| Older release | newest release is older than the pin | catalog left alone, step succeeds with a message | — |
| Hash mismatch | downloaded zip ≠ its `.sha256` | no commit | job fails |
| Missing assets | release has no zip or `.sha256` | no commit | job fails, names the release |
| First pin | entry carries the all-zero sha | pins the newest release | — |

</frozen-after-approval>

## Code Map

**This repo**
- `plugin/.claude-plugin/plugin.json`: `name` → `onenote`.
- `plugin/.mcp.json`: server key `onenote` → `desktop`.
- `.claude-plugin/marketplace.json`: delete it, and the `.claude-plugin/` folder.
- `scripts/pack-plugin.ps1:15,29`: stage dir `dist/stage/onenote-mcp` → `dist/stage/onenote`.
- `.github/workflows/ci.yml:34-51`: drop "Validate marketplace" (`claude plugin validate --strict .`), since the repo root is no longer a marketplace; validate the staged plugin at the new path; fix the comments.
- `.github/workflows/release.yml`:
  - Header comment (l.1-7): drop the marketplace commit.
  - l.92-96: same validate changes as ci.yml.
  - l.109: the stage path.
  - The publish job: remove the `checkout main` step and the "Pin marketplace.json" step (l.176-243). Keep `contents: write`, which creating the release needs.
- `tests/OneNoteMcp.Tests/PluginPackagingTests.cs`:
  - Drop the marketplace tests: `MarketplaceEntry_*`, the marketplace half of `PluginName_*`, and the `SemVer` helper they alone use.
  - Assert name `onenote`, server key `desktop`, and no `.claude-plugin/marketplace.json`.
  - Update the validate lines in `ReleaseWorkflow_RunsEveryCiCheck_WithTheSamePins`.
  - Keep the archive-URL and version-pattern tests: the asset naming still binds the marketplace.
- `README.md:54-61,92,128-146`:
  - Update the install, update and uninstall commands.
  - Add a short "Your own marketplace" section: an `archive` entry pointing at our release zip and sha, and the option to host the zip internally.
  - In "Cutting a release", replace the pin bullet with running the `sync` workflow in `sentienceAdjacent/plugins` (or waiting for the daily run).
- `CLAUDE.md:17`: stage path, and the validate commands.
- `Directory.Build.props`: `<Version>` → `0.4.0-rc.4`, in its own PR after the rename merges.
- Spine `architecture-onenote-mcp.md`: AD-15 l.308 and the layout block l.418 (see Open Questions).
- Epic `epic-read-only-release.md` Notes: a dated Decision for the names and the marketplace repo.

**New repo `sentienceAdjacent/plugins`** (local clone `D:\Development\sentienceAdjacent-plugins`)
- `.claude-plugin/marketplace.json`:
  - `name: sentienceadjacent`, and an owner.
  - Plugin `onenote` with an `archive` source, url and sha set from rc.4 (or the all-zero placeholder until the first sync).
- `.github/sources.json`: one row per plugin that is pinned from releases: `{plugin, repo, asset}`, with `asset` = `onenote-{version}-win-x64.zip`.
- `scripts/sync.sh`: for each row, run the pin logic.
  - Reuse `semver_gt` and the forward-only logic from this repo's release.yml.
  - Sources: `gh release list` / `gh release download`, then `jq` to edit.
- `.github/workflows/sync.yml`: `schedule` plus `workflow_dispatch`, with `permissions: contents: write`. It runs `sync.sh`, then `claude plugin validate --strict .`, then commits as `github-actions[bot]`.
- `.github/workflows/ci.yml`: on push and PR, `claude plugin validate --strict .` with the same Claude Code pin as onenote-mcp (2.1.318).
- `README.md`: what the catalog is, the install commands, and that each plugin keeps its own licence.
- `LICENSE`: MIT, the catalog and scripts only.

## Tasks & Acceptance

**Execution:**
- Implementation pass: local only. Make every file change in this repo and create the plugins repo as a local `git init` at `D:\Development\sentienceAdjacent-plugins`, uncommitted. No push, no GitHub repo creation, no tags, no PRs. The remote tasks, and the Fable review of the AD-15 amendment, run after code review.
- [ ] Rename, in this repo: `plugin.json`, `.mcp.json`, `pack-plugin.ps1`, `ci.yml`, `release.yml`, `PluginPackagingTests.cs`, `README.md` and `CLAUDE.md` as mapped.
- [ ] `.claude-plugin/marketplace.json`: delete it.
- [ ] Spine and epic: amend AD-15 (rule l.308, layout l.418, diagram l.413), log it in the spine memlog, add an epic Decision, then a Fable review of the amendment.
- [ ] Open a PR, wait for CI to go green, and merge.
- [ ] Create `sentienceAdjacent/plugins` (public) with the files mapped, and push. Its CI must be green with the all-zero placeholder.
- [ ] Bump to `0.4.0-rc.4` in a PR, merge, tag `v0.4.0-rc.4`, and push the tag. Then confirm the release run is green.
- [ ] Run the `sync` workflow, and confirm the bot commit pins rc.4 with the release's sha.

**Acceptance Criteria:**
- Given the merged rename, when CI runs, then the build, the tests, the pack and the staged-plugin validate all pass, and no step validates a marketplace in this repo.
- Given the rc.4 release, when `sync` runs twice, then the first run commits the rc.4 pin and the second makes no commit.
- Given the plugins repo, when `claude plugin validate --strict .` runs on it, then it passes.
- Given the staged rc.4 plugin, when it's inspected, then `plugin.json` names `onenote` and `.mcp.json` has a single server, `desktop`.

## Implementation Notes

- 2026-11-05, implementation pass (local only): `.github/sources.json` rows carry a fourth field, `since: "0.4.0-rc.4"`. Without it, the first sync against the all-zero placeholder would pin rc.3 (the newest public release today), which the Never list forbids. `sync.sh` ignores releases older than `since`.
- The release job keeps its "tagged commit is on main" check (release discipline), with its comment reworded; only the catalog checkout and pin steps are gone.
- `sync.sh` was exercised locally against the real rc.3 release with `since` lowered, using a test-only Python stand-in for `jq` (no jq on this machine): first pin wrote rc.3's url and the sha `3483722d…` (equal to the old in-repo pin), a rerun changed nothing, an older-than-pinned release left the catalog alone with exit 0, a wrong asset name failed naming the release, and a corrupted download failed with the catalog untouched. With the real `since`, it reports no release yet and exits 0. Real jq runs first in the sync workflow.

## Plan Change Log

## Review Triage Log

**Pass 1 (quick lens):** 0 high, 3 medium, 3 low, 2 false.

- **medium, patch:** `sync.sh` reads `gh release list` through process substitution, so a gh failure exits 0 and prints "no release yet". Bash ignores a process-substitution exit status under `set -e`. Fix: capture the list into a variable first.
- **medium, patch:** GitHub disables scheduled workflows in public repos after 60 days without activity. Both READMEs promise a daily run. Fix: document the manual `gh workflow run sync` as the step.
- **medium, patch:** story 12 in `tickets.toml` and `initiative-onenote-mcp-v1.md:51` still name the `onenote` skill, which contradicts the Always rule (skill `guide`). Fix: rename them in the entry and the initiative.
- **low, patch:** README "Your own marketplace" doesn't say that releases before rc.4 ship `onenote-mcp`. Fix: one sentence.
- **low, patch:** README has no step for anyone who added the old in-repo catalog. Fix: one line.
- **low, patch:** the CI-parity test's `Contains("dist/stage/onenote")` also matches `onenote-mcp`. Fix: anchor it to the end of the line.
- **false:** spine still `final` before its Fable review. The review is running now and finishes before this PR merges; nothing ships unreviewed.
- **false:** dangling "(see Open Questions)" in the plan. The fix edits this build's plan, which the rules reject.

## Design Notes

**Why pull, not push:**
- No secret in onenote-mcp can write to another repo.
- The catalog repo is the one place that decides what's published, which scales to many plugins.
- The cost is a delay of up to a day, or one `gh workflow run sync -R sentienceAdjacent/plugins` after each release.

**Why reuse:** `sync.sh` reuses the release workflow's `semver_gt` unchanged. It was reviewed in 1.11, including leading-zero and prerelease precedence.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` and `dotnet test tests/OneNoteMcp.Tests`: expect green.
- `pwsh -File scripts/pack-plugin.ps1`, then `claude plugin validate --strict dist/stage/onenote`: expect it to pass.
- In the plugins clone: `claude plugin validate --strict .`: expect it to pass.
- `gh run watch` on the release run and the sync run: expect both green.
- `gh api repos/sentienceAdjacent/plugins/contents/.claude-plugin/marketplace.json`: expect it to pin rc.4.
