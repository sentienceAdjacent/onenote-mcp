---
type: research
title: "Delivering the server exe through the Claude Code plugin"
parent: epic-read-only-release
status: draft
created: 2026-11-03
updated: 2026-11-03
---

# Delivering the server exe through the Claude Code plugin

This is input for story 1.10 (plugin packaging and exe delivery). It addresses the epic's Unknown: ".mcpb bundle or a launcher". The sources are the Claude Code docs as fetched on 2026-11-03. Rows marked UNCONFIRMED were not verified.

## Options

| Route | How | Documented | Costs and risks |
|---|---|---|---|
| **A. `archive` plugin source** (recommended) | The `marketplace.json` entry is `{"source":"archive","url":"https://github.com/pmcormier/onenote-mcp/releases/download/v<ver>/onenote-<ver>-win-x64.zip","sha256":"<hex>"}`. The zip is the plugin root: `.claude-plugin/plugin.json`, `.mcp.json` with `command: "${CLAUDE_PLUGIN_ROOT}/OneNoteMcp.exe"`, `skills/`, and the exe. | Yes: [marketplace-reference, archive plugin source](https://code.claude.com/docs/en/plugins/marketplace-reference.md). Requires Claude Code v2.1.224+. A mismatched `sha256` is refused. The plugin root may be one directory down in the zip. Download limits are in host-marketplace "Stay within the download limits for hosted files". | CI must rewrite `version` and `sha256` in `marketplace.json` for every release. Every install or update downloads the whole zip. A private repo needs `headers`/`headersHelper` auth, which is another reason entry 13 makes the repo public first. |
| B. `.mcpb` bundle | `mcpServers` points at a `.mcpb` path or `https://` URL. Claude Code extracts it into `.mcpb-cache/`. An MCPB manifest supports `server.type: "binary"` and `compatibility.platforms: ["win32"]`. | Yes: [manifest-reference, mcpServers](https://code.claude.com/docs/en/plugins/manifest-reference.md); [MCPB MANIFEST.md](https://github.com/modelcontextprotocol/mcpb/blob/main/MANIFEST.md). | UNCONFIRMED: the size limit for `.mcpb` downloads, and whether a bundle URL is refreshed on update. A repo-shared (project-scope) plugin skips `.mcpb` bundles (loading.md). |
| C. Launcher that downloads on first run | `.mcp.json` runs a script that fetches the release asset into `${CLAUDE_PLUGIN_DATA}\<version>\` and then runs it. | Not a documented pattern. Only the pieces are documented: `${CLAUDE_PLUGIN_DATA}`, `${CLAUDE_PLUGIN_ROOT}` and `MCP_TIMEOUT`. | We would have to own download, SHA check, cleanup of old versions, and the startup timeout. A first-run download of about 70–100 MB may exceed the MCP startup timeout (the default is UNCONFIRMED). |
| D. Commit the exe to the repo | Relative-path plugin with the exe in `plugin/`. | Works mechanically. Git LFS does **not** work: the clone gets pointer files. | Every version stays in git history. GitHub rejects files over 100 MiB. The marketplace clone timeout is 120 s by default. Also contradicts AD-15 ("no top-level bin/", exe on Releases). |

## Recommendation

Use Route A. It's documented, SHA-pinned, and keeps binaries out of git. It locks the exe version to the plugin `version`. It has no runtime download code and no first-start timeout risk. It fits AD-15: the repo stays the marketplace, and the exe ships on GitHub Releases.

## Questions for story 1.10's refinement

1. **Local development loop.** With Route A, a `/plugin marketplace add <local path>` install fetches the *released* zip, not the local build. Story 1.10's Verify says "starts the server from a locally built package". Options:
   - a second, dev-only marketplace file with a relative-path source pointing at a locally assembled plugin folder;
   - `claude --plugin-dir` for local testing.
   Decide before building.
2. **Versioning.** Set `version` in `plugin.json` only, not in the marketplace entry too; `claude plugin validate` warns when both are set. The release workflow (story 1.11) writes `version` and `sha256`.
3. **Updates.** Auto-update is off by default for third-party marketplaces. A new version loads next session or after `/reload-plugins`. The README should say so.
4. **Validation.** `claude plugin validate <path> [--strict] [--json]` exists (cli-reference): exit 0 is a pass, 1 a failure, 2 an internal error. It checks the manifest schema, that paths stay inside the root, `userConfig`, and MCP entries. UNCONFIRMED: whether it validates an archive-source zip's contents.
5. **The `${CLAUDE_PLUGIN_DATA}` note** in the spine (AD-19 install lifecycle) is unaffected. Settings, logs and the journal stay under `%APPDATA%`/`%LOCALAPPDATA%`.
