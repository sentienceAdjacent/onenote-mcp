# OneNote MCP

A local [MCP](https://modelcontextprotocol.io) server that lets Claude Code, and other MCP clients, read and manage the notebooks in your **OneNote desktop app** on Windows. It talks to OneNote through its COM automation API on your own machine. No cloud account, Microsoft Graph or sign-in is involved.

> **Status: early preview.** The read-only tools work, and preview builds are published as GitHub prereleases. Writing, exports and the safety features that come with them are planned, not built yet. Items marked *(planned)* below don't exist yet.

## What it does

**Available now (read-only):**

| Tool | What it does |
|---|---|
| `get_hierarchy` | notebooks, section groups, sections and pages as OneNote's XML, at any depth (`scope`) |
| `get_hierarchy_parent` | the parent of a page, section or section group |
| `get_page_content` | a page as OneNote's own XML, with a `basis` for safe edits later. Binaries are optional |
| `get_binary_page_content` | one image or attachment, base64 |
| `find_pages` | full-text search, including pages Windows Search hasn't indexed yet; recycle-bin pages are filtered out |
| `find_meta` | find pages carrying a given `one:Meta` tag |
| `get_hyperlink_to_object` / `get_web_hyperlink_to_object` | `onenote:` and web links to a notebook, section, page or paragraph |
| `get_special_location` | OneNote's backup, unfiled-notes and default notebook folders |

Results are always OneNote's XML, unchanged, so tables, tags, ink, images and attachments survive the trip. When something is degraded, such as a cloud image that isn't on this PC, a locked section or search results that may be incomplete, the result says so in `warnings` instead of failing.

***(planned)*** **Writes** (create, edit, rename, move and delete, only on notebooks you grant), **exports** (PDF, Word and others, into a folder you control), and a bundled skill that teaches Claude to use the tools well.

## Requirements

- Windows 10 or 11
- **OneNote desktop** (Microsoft 365, `ONENOTE.EXE`), **running**. The Windows 10 "OneNote for Windows" store app has no COM API.
- Claude Code (recommended), or any MCP client that can run a local stdio server
- Run the server at the same privilege level as OneNote, normally *not* as administrator

## Safety model

| | |
|---|---|
| **Reading** | Allowed on every notebook. The server never changes anything today. |
| **Focus** | The server never brings OneNote to the front or moves your view unless you ask. It never closes or kills OneNote. |
| **Logs** | IDs, error codes and timings only, never note content. |
| ***(planned)*** **Writing** | A grant for each notebook, given by typing the notebook's name in a confirmation dialog or with `OneNoteMcp grant`. The model can't grant itself access. |
| ***(planned)*** **Permanent delete** | A second, separate grant. Normal deletes go to OneNote's recycle bin. |
| ***(planned)*** **Undo** | A compressed snapshot in a local journal before every change to a page, with `restore_from_journal`. |
| ***(planned)*** **Stale edits** | A write is refused if the page changed since it was read. |

**Limits, stated honestly:**
- Search results may be incomplete while Windows Search catches up. Results always say `possiblyIncomplete`.
- Images in cloud notebooks that OneNote hasn't downloaded can't be read. They're reported as unavailable instead of failing.
- *(planned, for writes)* Grants guard against mistakes and casual misuse, not against hostile software already running as you. A grant belongs to the notebook's location (its folder or OneDrive URL), so a different notebook that later appears at a granted location inherits the grant. A Claude Code hook can auto-answer confirmation dialogs. The journal will hold copies of your note content on disk.

## Install

Only preview builds (`0.x` prereleases) exist so far.

### Claude Code plugin (recommended)

```text
/plugin marketplace add sentienceAdjacent/onenote-mcp
/plugin install onenote-mcp@onenote-mcp
```

This installs the MCP server and fetches the matching pre-built `OneNoteMcp.exe` from GitHub Releases, pinned by SHA-256. No .NET SDK is needed. Updates come from `/plugin marketplace update onenote-mcp`.

### Other MCP clients

1. Download `OneNoteMcp.exe`, and its `.sha256` to check it, from the [releases page](https://github.com/sentienceAdjacent/onenote-mcp/releases). Previews are listed as prereleases. Put it in a folder of your choice.
2. Add it to your client's MCP config as a stdio server. For Claude Desktop, use `%APPDATA%\Claude\claude_desktop_config.json`:

   ```json
   {
     "mcpServers": {
       "onenote": { "command": "C:\\path\\to\\OneNoteMcp.exe" }
     }
   }
   ```

3. Restart the client.

### From source

You need the .NET 10 SDK.

```powershell
git clone https://github.com/sentienceAdjacent/onenote-mcp
cd onenote-mcp
dotnet publish src/OneNoteMcp.Server -c Release -r win-x64
```

Then point your client at the published `OneNoteMcp.exe`, as in the previous section.

### Uninstall

Remove the plugin with `/plugin uninstall onenote-mcp`, or delete the exe and its MCP config entry. *(planned)* `OneNoteMcp uninstall --purge` will also remove settings, grants, the journal and logs. Until then, delete `%APPDATA%\OneNoteMcp` and `%LOCALAPPDATA%\OneNoteMcp` by hand.

## For agents installing this

Follow these steps in order, and stop to report to the user if any check fails:

1. Confirm the OS is Windows. Check that `ONENOTE.EXE` exists (normally `C:\Program Files\Microsoft Office\root\Office16\ONENOTE.EXE`), and that OneNote is running (`Get-Process ONENOTE`).
2. Confirm the shell is **not** elevated: `([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrator')` must be `False`.
3. In Claude Code, run the two `/plugin` commands above. Otherwise follow "Other MCP clients".
4. Verify: call `get_hierarchy` (the default scope is `notebooks`). It should list the user's notebooks.
5. Don't edit `%APPDATA%\OneNoteMcp\settings.json`, and never grant write access yourself once writes exist.

## Files it uses

| Path | Contents |
|---|---|
| `%APPDATA%\OneNoteMcp\settings.json` | timeouts, and later grants, the journal and export settings. Optional |
| `%LOCALAPPDATA%\OneNoteMcp\logs\` | logs: IDs and error codes only, never note content |
| *(planned)* `%LOCALAPPDATA%\OneNoteMcp\journal\` | compressed page snapshots |
| *(planned)* `Documents\OneNote Exports\` | default export folder |

## How it works

```text
MCP client ──stdio──► OneNoteMcp.exe ──COM (one STA thread)──► OneNote desktop
```

- **One server process per client:** several can run at once.
- **COM calls go one at a time,** with timeouts. If OneNote restarts, the server reconnects.

The design decisions are in the [architecture spine](_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md).

## Contributing

Issues and pull requests are welcome. Every pull request needs the contributor agreement in [CONTRIBUTING.md](CONTRIBUTING.md).

## Cutting a release (maintainers)

1. **Bump `<Version>`** in `Directory.Build.props`, in a PR. Use a new string every time, with a prerelease suffix for candidates (`0.4.0-rc.1`, `0.4.0-rc.2`, then `0.4.0`). Never reuse one: users who already have a version never download it again, even when it was rebuilt. Leave `marketplace.json` alone; the release workflow updates it.
2. **Merge** the PR to `main`.
3. **Tag** the merge commit `v<Version>` and push the tag:

   ```powershell
   git tag v0.4.0-rc.1 origin/main
   git push origin v0.4.0-rc.1
   ```

`.github/workflows/release.yml` then:

- refuses the tag unless it is exactly `v<Version>` and its commit is on `main`, before building anything. It also refuses a version that already has a release;
- reruns every CI check on the tagged commit: the build with warnings as errors, the unit tests, the offline integration tests, `scripts/pack-plugin.ps1`, and both `claude plugin validate --strict` runs;
- publishes a GitHub Release, marked as a prerelease when the version has a suffix, with notes generated from the commits. Its assets are the plugin zip, the bare `OneNoteMcp.exe`, and a `.sha256` for each. It downloads them back and checks the hashes;
- commits the zip's `url` and `sha256` to `.claude-plugin/marketplace.json` on `main`, as `github-actions[bot]`. The tagged commit keeps the previous release's values. That commit doesn't trigger CI. Each tag gets its own run, so tags pushed close together all release, and the catalog only moves forward: a run whose version isn't newer than the one `main` pins fails that step and leaves the catalog alone.

If a check fails, nothing is published: fix the cause and release a new version. Don't move the tag, and don't upload a rebuilt zip under an existing version. If only the final catalog commit fails, the release itself is good: pin `marketplace.json` by hand from the published `.sha256`. Release builds are unsigned for now.

## Acknowledgements

Much of what this project knows about OneNote's COM API, its XML and its quirks comes from [OneMore](https://github.com/stevencohn/OneMore) by Steven M Cohn and its [developer documentation](https://onemoreaddin.com/developers/Setup.htm). OneMore is a long-lived OneNote add-in, and its write-ups of measured behaviour and API defects saved us a lot of rediscovery. This project's code is its own. No OneMore source is included.

## License

Free to use, change and share for any noncommercial purpose, and inside any organization for its own internal use, including at work. Selling it, licensing it for a fee, or offering it as part of a paid product or service needs a separate license. Ask at license@sentienceadjacent.com. Forks and copies must keep the same terms and the attribution notice.

The exact terms are the [PolyForm Noncommercial License 1.0.0 plus an internal-use permission](LICENSE.md). Third-party components keep their own licenses ([notices](THIRD-PARTY-NOTICES.md)).
