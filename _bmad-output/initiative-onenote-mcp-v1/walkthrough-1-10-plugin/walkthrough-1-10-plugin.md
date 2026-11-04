# Walkthrough: PR #3, story 1.10 (plugin packaging and exe delivery)

Target: branch `story/1.10-plugin` vs `e3dec472f80134c064fc34acce2a43660a84920a`. Plan: [story-plugin-packaging-and-exe-delivery-plan.md](../epic-read-only-release/story-plugin-packaging-and-exe-delivery-plan.md)

Current block: wrap-up

## - [x] 1. Intent (accepted)

Source: the plan's frozen Intent section, verbatim.

**Problem:** The server exists only as a local Debug build registered by hand. There is no plugin, no marketplace and no packaged artifact, so nothing can be installed the way AD-15 and AD-19 describe.

**Approach:** Make the repo a marketplace whose single plugin entry is an `archive` source (research note, Route A). Add a `plugin/` source folder, rename the exe to `OneNoteMcp.exe`, and add a pack script that publishes a self-contained exe, stages it with `plugin/` and zips it. CI validates the repo root and the staged plugin. The local loop is `claude --plugin-dir <zip>`.

## - [x] 2. Broad strokes (accepted)

The repo is now a Claude Code marketplace. Its one plugin is fetched from a release zip, and a script builds that zip from source. Open these in order:

1. [marketplace.json:13](../../../.claude-plugin/marketplace.json#L13): the catalog entry, an `archive` source pinned by `sha256`.
2. [plugin/.mcp.json:5](../../../plugin/.mcp.json#L5): what the plugin runs, `${CLAUDE_PLUGIN_ROOT}/OneNoteMcp.exe`.
3. [pack-plugin.ps1:48](../../../scripts/pack-plugin.ps1#L48): the publish, stage and zip that builds the plugin root.
4. [OneNoteMcp.Server.csproj:6](../../../src/OneNoteMcp.Server/OneNoteMcp.Server.csproj#L6): the exe rename and the single-file publish shape.
5. [ci.yml:40](../../../.github/workflows/ci.yml#L40): CI packs and validates on every push.

## - [x] 3. The exe: rename and publish shape (accepted)

`AssemblyName` changes the output to `OneNoteMcp.exe`. Namespaces and the project name stay `OneNoteMcp.Server`. When publishing, the runtime identifier defaults to win-x64. Any build with a runtime identifier then becomes self-contained, single-file and compressed, with trimming and ReadyToRun explicitly off, because COM interop and the MCP SDK's reflection aren't trim-safe.

- [OneNoteMcp.Server.csproj:6](../../../src/OneNoteMcp.Server/OneNoteMcp.Server.csproj#L6): AssemblyName and the pinned RootNamespace.
- [OneNoteMcp.Server.csproj:11](../../../src/OneNoteMcp.Server/OneNoteMcp.Server.csproj#L11): the win-x64 default, applied only when publishing.
- [OneNoteMcp.Server.csproj:14](../../../src/OneNoteMcp.Server/OneNoteMcp.Server.csproj#L14): the single-file properties.

## - [x] 4. Plugin source and the marketplace entry (accepted)

`plugin/` is the plugin root as checked in. It holds a manifest with no `version` and the MCP config. The marketplace entry points at the v0.1.0 release zip URL with an all-zero `sha256`. That's a placeholder: story 1.11 writes the real hash after its first release, and until then any install is refused on the mismatch. The version lives only in the staged manifest, because the manifest's `version` overrides the entry's and a checked-in copy would drift.

- [plugin.json](../../../plugin/.claude-plugin/plugin.json): name `onenote-mcp`, display fields, no version, no licence (still undecided).
- [.mcp.json:5](../../../plugin/.mcp.json#L5): the `onenote` stdio server.
- [marketplace.json:15](../../../.claude-plugin/marketplace.json#L15): the placeholder hash.

## - [x] 5. The pack script (accepted)

The script reads `<Version>` through MSBuild and deletes earlier output. It publishes, then copies `plugin/` and the exe into `dist/stage/onenote-mcp` with no `bin/` folder, and writes the version into the staged `plugin.json`. It then zips the stage's contents so the zip root is the plugin root, and prints the SHA-256. `Invoke-Native` turns any non-zero exit into a failure and prints the command's output first. `#Requires -Version 7` refuses Windows PowerShell 5.1 at once.

- [pack-plugin.ps1:27](../../../scripts/pack-plugin.ps1#L27): `Invoke-Native`.
- [pack-plugin.ps1:36](../../../scripts/pack-plugin.ps1#L36): version read.
- [pack-plugin.ps1:44](../../../scripts/pack-plugin.ps1#L44): clean start.
- [pack-plugin.ps1:60](../../../scripts/pack-plugin.ps1#L60): version injection.
- [pack-plugin.ps1:67](../../../scripts/pack-plugin.ps1#L67): zip and hash.

## - [x] 6. Guards: unit tests and CI (accepted; changed during review: tests renamed)

Unit tests keep the checked-in files consistent:
- neither manifest carries a version;
- the names match;
- the MCP command targets `OneNoteMcp.exe` at the plugin root;
- the entry is an archive whose URL follows `<Version>`;
- there's no `plugin/bin`.

CI installs a pinned Claude Code (2.1.318), packs, then validates twice with `--strict`. A marketplace validate checks only an archive entry's fields, so the staged plugin is validated as well.

- [PluginPackagingTests.cs:60](../../../tests/OneNoteMcp.Tests/PluginPackagingTests.cs#L60): the URL-follows-version test.
- [PluginPackagingTests.cs:27](../../../tests/OneNoteMcp.Tests/PluginPackagingTests.cs#L27): the no-version tests.
- [ci.yml:32](../../../.github/workflows/ci.yml#L32): Node and Claude Code install.
- [ci.yml:43](../../../.github/workflows/ci.yml#L43): the two validations.

## - [x] 7. Periphery (accepted)

- [.gitignore](../../../.gitignore): ignores `dist/`.
- [CLAUDE.md](../../../CLAUDE.md): new run path and a pack/local-loop line.
- [Story 1.3 plan](../epic-read-only-release/story-result-envelope-error-codes-operation-classes-and-tool-contr-plan.md): status `done` (Phill).
