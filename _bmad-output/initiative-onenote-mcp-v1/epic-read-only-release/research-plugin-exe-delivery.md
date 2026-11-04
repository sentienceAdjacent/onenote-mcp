---
type: research
title: "Delivering the server exe through the Claude Code plugin"
parent: epic-read-only-release
status: final
decision: "Route A: an archive plugin source pointing at a SHA-256-pinned release zip whose root is the plugin."
source: "Claude Code plugin docs and the MCPB manifest spec, fetched 2026-11-03 and re-checked 2026-11-04 (Sources)."
created: 2026-11-03
updated: 2026-11-04
---

# Delivering the server exe through the Claude Code plugin

## Recommendation

Use **Route A**, an `archive` plugin source. The release zip is the plugin root and holds the exe. The marketplace entry pins the zip by SHA-256.

- It's documented and SHA-pinned, it keeps binaries out of git, and there's no runtime download code or first-start timeout risk.
- It fits AD-15: the repo stays the marketplace and the exe ships on GitHub Releases.
- It needs a wording change to AD-19, which named only `${CLAUDE_PLUGIN_DATA}` or the `.mcpb` cache. `${CLAUDE_PLUGIN_ROOT}` is already a per-version directory, so AD-19's rules hold (see Route A facts).

This note is input for story 1.10 (plugin packaging and exe delivery) and story 1.11 (release pipeline). It closes the epic's Unknown ".mcpb bundle or a launcher". Code signing and SmartScreen are out of scope here: the route doesn't change them, and story 1.11 owns them.

Confidence is marked per claim: **[doc]** quoted from the docs in Sources; **[inferred]** our reasoning from documented behaviour; **[unverified]** not checked.

## Options

| Route | How | Costs and risks |
|---|---|---|
| **A. `archive` plugin source** (recommended) | The `marketplace.json` entry is `{"source":"archive","url":"https://github.com/pmcormier/onenote-mcp/releases/download/v<ver>/onenote-<ver>-win-x64.zip","sha256":"<hex>"}`. The zip is the plugin root: `.claude-plugin/plugin.json`, `.mcp.json` with `command: "${CLAUDE_PLUGIN_ROOT}/OneNoteMcp.exe"`, `skills/`, and the exe. | Each release needs a catalog commit after the build (Release flow). Each new version downloads the zip again. A private repo can't authenticate the download (Route A facts), which is another reason entry 13 makes the repo public first. |
| B. `.mcpb` bundle | `mcpServers` points at a `.mcpb` path or `https://` URL. Claude Code extracts it into `.mcpb-cache/`. An MCPB manifest supports `server.type: "binary"` ("Compiled executable") and `compatibility.platforms: ["win32"]` [doc], so a self-contained .NET exe fits. | There's no SHA-256 pin for bundles [doc]. No download size limit is documented for them [doc]. A bundle referenced by URL "isn't read at install time" [doc], so it's fetched at load, and no refresh rule is documented. `--config <server>.<key>` works only for a bundle packaged inside the plugin [doc]. A project-scope plugin skips bundles [doc]; that doesn't affect our user-scope install. |
| C. Launcher that downloads on first run | `.mcp.json` runs a script that fetches the release asset into `${CLAUDE_PLUGIN_DATA}\<version>\` and runs it. | Not a documented pattern. We'd own the download, the SHA check, cleanup of old versions and the startup timeout. `MCP_TIMEOUT` defaults to 30000 ms [doc]. Stdio servers connect in the background by default, so a slow first run doesn't block the session, but a first download of about 70–100 MB can still miss the 30 s bound [inferred]. |
| D. Commit the exe to the repo | Relative-path plugin with the exe in `plugin/`. | Every version stays in git history. GitHub blocks files over 100 MiB [doc, GitHub]. Git LFS doesn't help: "The clone never downloads LFS content, so LFS-tracked files arrive as pointer files" [doc]. Marketplace clones time out after 120 s by default (`CLAUDE_CODE_PLUGIN_GIT_TIMEOUT_MS`) [doc]. It also contradicts AD-15 (no top-level `bin/`, the exe on Releases). |

## Route A facts

- Requires Claude Code v2.1.224 or later. A `sha256` mismatch is refused. The plugin root may sit one directory down in the zip. [doc]
- Download limits: the zip is at most 256 MiB, the server must respond within 120 s, at most five redirects, all `https://`. [doc]
- Extraction limits: 100,000 entries, 512 MiB per file, 1 GiB total, a 50× compression ratio. [doc]
- "A request that a redirect sends to a different origin carries none of the headers you configured." [doc] GitHub release downloads redirect to another origin, so `headers` auth for a private repo would work only through the API asset URL with `Accept: application/octet-stream` [inferred]. `headersHelper` also needs `"strict": false` [doc].
- Install location: `cache/<marketplace>/<plugin>/<version>/`, "one directory per installed version", and `${CLAUDE_PLUGIN_ROOT}` points at it. [doc]
- Old versions: on update or uninstall, Claude Code marks the previous version directory and removes it 14 days later, "so a session that already loaded the old version keeps running". [doc] That meets AD-19's "an upgrade never overwrites an exe that is running" [inferred].

## Release flow (for story 1.11)

1. **The catalog commit.** `marketplace.json` lives on the default branch, which `/plugin marketplace add` clones and which an install refreshes first [doc]. The zip's SHA-256 is known only after CI builds the zip from the tag, so the tagged commit's catalog still points at the previous release [inferred]. Story 1.11 picks one of:
   - a bot commit to `main` after the release, which needs write permission past branch protection;
   - building and hashing before tagging;
   - serving `marketplace.json` from a release asset or Pages as a `url` marketplace source.
2. **Where each value lands.** `version` goes into the zip's `.claude-plugin/plugin.json`, from `<Version>` at build time. `sha256` goes into the `marketplace.json` entry after the build. Set `version` in `plugin.json` only, because the manifest's `version` wins over the entry's [doc].
3. **Version string and re-downloads.** The manifest `version` decides whether users re-download: "a manifest that pins `1.0.0` keeps every user on the cached copy until its author changes the string" [doc]. A rebuilt zip under an unchanged `<Version>` never reaches users who already have that version. So every release candidate and hotfix rebuild needs a new version string. AD-14's single `<Version>` already forces that if the release job refuses a tag that differs from `<Version>`.

## Questions for story 1.10's refinement

1. **Local development loop.** A `/plugin marketplace add <local path>` install fetches the *released* zip, not the local build. Two options:
   - `claude --plugin-dir ./dist/onenote-<ver>-win-x64.zip` loads "a plugin from a directory or `.zip` archive" for one session, extracted to a session temp directory [doc]. It tests the exact release artifact. **Recommended.**
   - A dev-only marketplace with a relative-path source loads in place, with `${CLAUDE_PLUGIN_ROOT}` = the source directory [doc]. The exe would then have to sit in a gitignored `plugin/` folder, which is in tension with AD-15.

   With `--plugin-dir`, ticket 1.10's Verify text ("`/plugin marketplace add` on the local repo plus `/plugin install`") needs changing.
2. **Validation.** `claude plugin validate <path> [--strict] [--json]`. Exit codes: 0 is a pass (with or without warnings), 1 a failure (or a warning under `--strict`), and 2 a validator error with nothing on stdout. `--json` needs v2.1.259+. [doc] Run from a marketplace, it doesn't open the files of plugins listed in other directories, and for an archive entry it checks only the entry's fields [doc]. So CI validates twice: the repo root, then the staged plugin folder before zipping.
3. **Updates.** Auto-update is off by default for third-party marketplaces [doc]. Users update with `claude plugin update onenote-mcp@<marketplace>` or through `/plugin`. The new version loads at the next launch or after `/reload-plugins` [doc]. The README should say so.
4. **README.**
   - State the minimum Claude Code version, v2.1.224.
   - Add `CLAUDE_CODE_PLUGIN_PREFER_HTTPS=1` for machines without a GitHub SSH key. In-session installs and updates don't fall back to HTTPS on their own [doc].
   - "Other MCP clients: download `OneNoteMcp.exe`" assumes a bare exe on the release. Story 1.11 decides whether each release attaches the exe beside the plugin zip.
   - Story 13's clean-machine check covers all of these.
5. **Data locations.** Settings, logs and the journal stay under `%APPDATA%`/`%LOCALAPPDATA%` (AD-19), not in `${CLAUDE_PLUGIN_ROOT}`. Version directories are deleted after 14 days, so nothing that must survive an upgrade can live there.

## Sources

All accessed 2026-11-03 and re-checked 2026-11-04.

- Claude Code, Plugin marketplace reference: archive plugin source, entry fields, validation table. https://code.claude.com/docs/en/plugins/marketplace-reference.md
- Claude Code, Plugin manifest reference: `mcpServers`, environment variables. https://code.claude.com/docs/en/plugins/manifest-reference.md
- Claude Code, How plugins load: cache layout, version resolution, refresh before install, cleanup of previous versions, auto-update defaults, project-scope `.mcpb` skip. https://code.claude.com/docs/en/plugins/loading.md
- Claude Code, Host a marketplace: download and extraction limits, redirects and headers, LFS, HTTPS preference. https://code.claude.com/docs/en/plugins/host-marketplace.md
- Claude Code, Plugin commands reference: `plugin validate`, `--plugin-dir`, `plugin update`. https://code.claude.com/docs/en/plugins/cli-reference.md
- Claude Code, Environment variables: `MCP_TIMEOUT`, `MCP_CONNECTION_NONBLOCKING`. https://code.claude.com/docs/en/env-vars.md
- Claude Code, Plugin troubleshooting: clone timeout, URL bundles not read at install. https://code.claude.com/docs/en/plugins/troubleshooting.md
- MCPB, MANIFEST.md (spec 0.3): `server.type: "binary"`, `compatibility.platforms`. https://github.com/modelcontextprotocol/mcpb/blob/main/MANIFEST.md
- GitHub Docs, About large files on GitHub: the 100 MiB file limit. https://docs.github.com/en/repositories/working-with-files/managing-large-files/about-large-files-on-github
