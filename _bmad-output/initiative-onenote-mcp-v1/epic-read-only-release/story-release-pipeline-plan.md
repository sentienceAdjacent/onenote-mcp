---
title: 'Release pipeline'
type: 'feature'
ticket: '11'
created: '2026-11-05'
status: 'built'
baseline_revision: '6031ab0799e34de49eaa07701b8dd21c3b2076e1'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/research-plugin-exe-delivery.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Nothing publishes a release. `marketplace.json` points at a v0.4.0 zip that doesn't exist, with a zero sha256, so the plugin can't be installed (story 1.10 left it that way).

**Approach:**
- Add a tag-triggered `release.yml`. It refuses a tag that differs from `<Version>` (AD-14), and reruns the same build, tests, pack and `claude plugin validate` as CI on the tagged commit.
- It then publishes a GitHub Release with the exact zip it hashed.
- `marketplace.json` gets the new URL and sha256 by the mechanism chosen in the open questions.
- Release candidates prove install, and upgrade with a session open.

## Boundaries & Constraints

**Always:**
- **Tag rule:** tag `v<Version>`, where `<Version>` may carry a semver prerelease (`0.4.0-rc.1`). A mismatch fails the job before anything is built (AD-14). A version string is never reused (AD-19).
- **Same checks as CI:** the release job runs the CI checks itself on the tagged commit: build `-warnaserror`, unit tests, the offline integration tests, `pack-plugin.ps1`, and both `claude plugin validate --strict` runs. A failure means no release.
- **Hash what ships:** the sha256 comes from the zip that is uploaded, never from a rebuild, because the zip isn't reproducible (1.10).
- **Prereleases:** a prerelease version gives a GitHub prerelease.
- **Pack script:** `pack-plugin.ps1` accepts a semver prerelease suffix and writes `dist/onenote-<ver>-win-x64.zip.sha256`.
- **Permissions:** the workflow has the least permissions it needs. `contents: write` goes only to the job that needs it.
- **Release notes:** generated from commits. No secrets beyond `GITHUB_TOKEN` unless signing needs them.

- Decision (2026-11-05, Phill), the sha256: once the release is published, the workflow commits the new `url` and `sha256` to `main` as `github-actions`. That needs `contents: write` in that job only. The tagged commit keeps the previous release's values.
- Decision (2026-11-05, Phill), signing: no recurring signing cost. Ship unsigned, and record whether the installed exe carries Mark-of-the-Web. Free signing (SignPath's OSS programme) is revisited once the repo is public and licensed.
- Decision (2026-11-05, Phill), assets: each release attaches the zip, the bare `OneNoteMcp.exe`, and a `.sha256` for each.

- Decision (2026-11-05, Phill), the private repo: stay private for now; Phill is still weighing going public against a separate public distribution repo, which would need an AD-15 amendment.
  - Here, verify by downloading the release assets with `gh release download`, checking each sha256, and loading the zip with `claude --plugin-dir`.
  - The upgrade-while-running check uses two local zips. With an rc.1 session open, install rc.2 into a second plugin directory, check that rc.1's exe is untouched, and check that a new session reports rc.2.
  - The marketplace install moves to 1.13.

**Never:**
- No change to server code or the tool contract.
- No tag or release made by an agent without Phill's go-ahead in the session: tags and releases are outward-facing.
- No making the repo public in this story unless Phill decides so below.

</frozen-after-approval>

## Code Map

- `.github/workflows/ci.yml` -- the steps to mirror: windows-latest, dotnet `10.0.x`, build, unit tests, the offline integration tests, Node 22, `npm i -g @anthropic-ai/claude-code@2.1.318` (pinned), pack, and validate ×2 (L24–51). Its triggers are push and pull_request, with `contents: read`. It runs on tag pushes too, but the release job must not depend on that run.
- `scripts/pack-plugin.ps1` -- L38's regex `^\d+\.\d+\.\d+$` rejects prereleases, so relax it to semver with an optional `-<pre>`. L69–71 print the sha256. Add the `.sha256` file. The zip root is the plugin root.
- `.claude-plugin/marketplace.json` -- an `archive` source with `url` `…/releases/download/v{ver}/onenote-{ver}-win-x64.zip` and `sha256`.
- `tests/OneNoteMcp.Tests/PluginPackagingTests.cs` L60–73 -- ties the URL to `<Version>` and the sha to `^[0-9a-f]{64}$`. Neither plugin.json nor the entry has a `version` (L27–37). Prerelease versions flow through `ServerSetup.ReadVersion` and `ToolContractTests` (which compare major.minor) unchanged.
- `Directory.Build.props` L4 -- `<Version>0.4.0</Version>`.
- The repo is private, on a free plan. There's no branch protection (the API answers 403, "upgrade or make public"), so a workflow push to `main` with `contents: write` is possible. Default workflow permissions are `read`.
- `research-plugin-exe-delivery.md` -- release flow L46–51 (the three sha options), private-repo headers L40, version reuse L51, bare exe L65, signing L22.

## Tasks & Acceptance

**Execution:**
- [x] `scripts/pack-plugin.ps1` -- prerelease versions and the `.sha256` file.
- [x] `.github/workflows/release.yml` -- on `push: tags: ['v*']`: the version check, the CI steps, pack, the release with assets, then the sha step in the chosen form.
- [x] `tests/OneNoteMcp.Tests/PluginPackagingTests.cs` -- add cases: a prerelease `<Version>` gives the expected URL, and the release workflow's tag check matches the props, read from the YAML.
- [x] `README.md` -- a short maintainer section, "Cutting a release": bump `<Version>`, merge, tag, and what the workflow does.

**Acceptance Criteria:**
- Given the repo, when the Release build and the unit tests run, then both pass with 0 warnings.
- Given `<Version>` 0.4.0-rc.1 merged and Phill's go-ahead, when `v0.4.0-rc.1` is pushed, then a GitHub prerelease appears with the zip, its sha256 the bare exe, and a `.sha256` for each, and `marketplace.json` ends up pinning that URL and hash.
- Given rc.1 installed and a Claude Code session using it, when rc.2 is released and the plugin is updated, then the update succeeds, the running rc.1 exe isn't overwritten, and a new session runs rc.2 (`serverInfo.version`). This is checked from local zips per the private-repo Decision.

## Implementation Notes

- Build and test with `-c Release --artifacts-path <temp dir>`, because the running `onenote` MCP session locks the Debug output.
- Don't push tags or create releases: the lead does those with Phill.

- Release-candidate runs with Phill (2026-11-05):
  - **rc.1:** tagged after PR #12 bumped the version. Release run 42112551163 passed both jobs. It produced a prerelease with four assets (the zip at 34.0 MB, the bare exe, and a `.sha256` for each), and every hash checked OK after `gh release download`. Bot commit `af7578b` pinned `marketplace.json` to rc.1, with the sha equal to the zip's.
  - **rc.1 load:** a headless `claude -p --plugin-dir <rc.1 zip>` saw all nine tools, and `get_special_location` returned `ok:true`.
  - **rc.2:** PR #13, then the tag. The release passed, and `76a5f44` pinned rc.2. The forward-only check passed because rc.2 is newer than rc.1.
  - **Upgrade while running:** with an rc.1 server process running from its own folder, unpacking rc.2 into a separate folder left rc.1's exe untouched and locked, and rc.1 kept running. A new process reported `serverInfo.version` 0.4.0-rc.2.
  - **Mark-of-the-Web:** none on the files `gh` downloaded, none on the zips, and none on a copy Claude Code had extracted earlier. Claude Code's own marketplace download wasn't checked, because the repo is private. That check moves to 1.13.

## Plan Change Log

- 2026-11-05: `MarketplaceEntry_IsAnArchiveForThePropsVersion` became `MarketplaceEntry_IsAnArchiveOfAReleaseNoNewerThanTheProps`. Under the sha256 Decision the tagged commit and later bumps keep the previous release's values, so "URL == `<Version>`" would fail every bump PR. The test now requires the URL's tag and file name to agree, the version to fit `VERSION_PATTERN`, and, unless the sha256 is the all-zero "no release yet" placeholder, the pinned version to be no newer than `<Version>` by semver precedence.
- 2026-11-05: added a release-job gate that the tagged commit is an ancestor of `origin/main` (needs `fetch-depth: 0`), because the publish job commits the catalog to `main`.
- 2026-11-05: the GitHub Release and the catalog commit share one `publish` job, so `contents: write` is granted to exactly one job; the workflow default is `permissions: {}`.
- 2026-11-05: `<Version>` stays 0.4.0 in this branch; the bump to 0.4.0-rc.1 is the lead's release PR (README "Cutting a release"). The rc.1 pack was verified locally with a temporary bump.

## Review Triage Log

- 2026-11-05, quick lens, pass 1: 7 findings. 1 medium, 3 low patched, 1 low rejected, 2 false.
  - medium, patch: nothing enforced AD-19's no-reuse rule or stopped a downgrade. Tagging an older commit on `main` would repin the catalog backwards, and a deleted, re-pushed tag would republish a version. Fix: refuse when a release already exists, and pin only a strictly newer version.
  - low, patch: `VERSION_PATTERN` allowed prerelease numbers with leading zeros (`rc.01`), which SemVer §9 forbids. Fix: tighten the pattern, with a reject case.
  - low, patch: a single concurrency group drops the middle run when three tags are pushed quickly. Fix: one group per tag. The strictly-newer check guards the order in which runs finish.
  - low, patch: the pins test didn't compare `dotnet-version`, `node-version` or the `actions/*` versions. Fix: assert them too.
  - low, rejected: the all-zero placeholder sha skips the lag check forever. Putting it back after a release would be a deliberate edit, and closing that needs extra test state.
  - false: "the MotW check isn't recorded". It's a step in the rc run with Phill, and the lead records it there.
  - false: "acceptance criteria 2 and 3 have no evidence". They depend on the tag run, which is gated on Phill.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -c Release -warnaserror --artifacts-path <tmp>` -- expected: 0 warnings
- `dotnet test tests/OneNoteMcp.Tests -c Release --artifacts-path <tmp>` -- expected: all pass
- `pwsh -File scripts/pack-plugin.ps1` with `<Version>` 0.4.0-rc.1, done locally -- expected: the zip and its `.sha256` file
- With Phill: record whether the installed exe carries Mark-of-the-Web (`Get-Item <exe> -Stream Zone.Identifier`).
- With Phill: push `v0.4.0-rc.1`, then `v0.4.0-rc.2`, and check from local zips per the private-repo Decision.
