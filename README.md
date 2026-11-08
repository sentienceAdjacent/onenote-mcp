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
| `read_result` | reads a result that was too large to return whole, part by part (see below). It never calls OneNote |
| `list_grants` | lists the password-protected sections you've allowed Claude to read, how and until when. It can't create or change a grant |

**User-visible, only when you ask:**

| Tool | What it does |
|---|---|
| `navigate_to` | shows a notebook, section or page in OneNote, optionally selecting one object on it. It takes keyboard focus and changes what OneNote shows, so the plugin's `guide` skill tells Claude to call it only when you explicitly ask, for example to download a page's missing images. It changes no content |

Results are always OneNote's XML, unchanged, so tables, tags, ink, images and attachments survive the trip. When something is degraded, such as a cloud image that isn't on this PC, a locked section or search results that may be incomplete, the result says so in `warnings` instead of failing.

**Password-protected sections.** A section with a password stays private even after you unlock it in OneNote. Claude sees that it exists, but its pages are left out of hierarchy listings and searches (`meta.withheld` names the section by ID, with a `readNotGranted` warning), and reading inside it fails with `ReadNotGranted`, until you grant a read of that one section:

- **In Claude Code**, reading one of its pages raises a dialog. Type the section's name (the field's hint shows it, to copy) and pick from one list: **this section, for 5 minutes** (the default), **1 hour**, **8 hours**, **24 hours**, **7 days** or **until I revoke it**. For any other number of hours (1 to 168), use the terminal command below. Declining, or a client that can't show dialogs, grants nothing. The dialog's name field gets a new name on every request, so the client has no earlier answer to fill it in from.
- **In a terminal**, with any client: `OneNoteMcp grant --read-section "Section name"`. It lists the matches if several open sections share the name, and offers hours or until revoked. It refuses to run without an interactive terminal, so a program can't run it for you.
- **Revoke** with `OneNoteMcp grant --revoke --read-section "Section name"`. `list_grants` shows what's stored.
- **Where `OneNoteMcp` is.** A plugin install doesn't put the exe on your PATH: it sits in the plugin's install folder, `%USERPROFILE%\.claude\plugins\cache\sentienceadjacent\onenote\<version>\OneNoteMcp.exe` (the newest `<version>` folder). Run it by its full path, in quotes, and in PowerShell with `&` in front: `& "$env:USERPROFILE\.claude\plugins\cache\sentienceadjacent\onenote\<version>\OneNoteMcp.exe" grant --read-section "Section name"`. When a read is refused, the error's `userAction` names the exact path of the exe that's running.

A grant is for one section, keyed by its file path: renaming or moving the section means granting again. OneNote won't move a page from a password-protected section to its recycle bin (deleting one is permanent), but pages deleted before a section was protected stay readable there. **This section, for 5 minutes** lets Claude read the whole section (pages, listings and searches) for that long, then asks again. It's held in memory by that one server process, never saved, can't be revoked, and `list_grants` shows it while it lasts; the minutes are `readGrants.onceMinutes` in `settings.json` (1 to 60, default 5). A grant for hours or until revoked that can't be saved (because `settings.json` is invalid) works the same way, with a `grantNotSaved` warning. Pages of protected sections are never kept in the search cache, even under a grant. Saved grants live in `settings.json` (`protectedSections`). A large result read under a grant for hours or until revoked can be paged with `read_result` only while that grant lasts. One read under the 5-minute grant stays pageable for the stored result's life, `results.ttlMinutes` after the original call (default 30), even after the window ends; `read_result` never asks.

**Large results.** A result up to `results.inlineMaxChars` characters (100,000 by default) comes back whole. Its text is one line of JSON (the result's `meta` and `warnings`), then the XML exactly as OneNote returned it, not escaped into JSON. A bigger result, such as a long page or a whole notebook's pages, is kept in the server's memory and comes back as an index: one entry per child element, with its size and a label (a section's or page's name, or the first 80 characters of an element's text). `read_result` then returns any element, run of elements, deeper index, or character range of it, each an exact substring of what OneNote returned, so nothing is lost or rewritten. A stored result lasts 30 minutes after its last read, and is never written to disk.

***(planned)*** **Writes** (create, edit, rename, move and delete, only on notebooks you grant), **exports** (PDF, Word and others, into a folder you control), and a bundled skill that teaches Claude to use the tools well.

## Requirements

- Windows 10 or 11
- **OneNote desktop** (Microsoft 365, `ONENOTE.EXE`), **running**. The Windows 10 "OneNote for Windows" store app has no COM API.
- Claude Code (recommended), or any MCP client that can run a local stdio server
- Run the server at the same privilege level as OneNote, normally *not* as administrator

## Safety model

| | |
|---|---|
| **Reading** | Allowed on every notebook, except the content of password-protected sections, which needs your read grant for that section (above). The server never changes anything today. |
| **Focus** | The server never brings OneNote to the front or moves your view unless you ask. Only `navigate_to` does, and no other tool calls it. It never closes or kills OneNote. |
| **Logs** | IDs, error codes and timings only, never note content. |
| ***(planned)*** **Writing** | A grant for each notebook, given by typing the notebook's name in a confirmation dialog or with `OneNoteMcp grant`. The model can't grant itself access. |
| ***(planned)*** **Permanent delete** | A second, separate grant. Normal deletes go to OneNote's recycle bin. |
| ***(planned)*** **Undo** | A compressed snapshot in a local journal before every change to a page, with `restore_from_journal`. |
| ***(planned)*** **Stale edits** | A write is refused if the page changed since it was read. |

**Limits, stated honestly:**
- Search results may be incomplete while Windows Search catches up. Results always say `possiblyIncomplete`.
- To make repeat searches fast, `find_pages` remembers each page's text in memory (never on disk) until the page's last-modified time changes. A page that another program (another add-in, or a second OneNote MCP session) edits through COM while it isn't open in OneNote keeps its old time, so a search can match it against its old text until it next changes or the server restarts. Edits made in OneNote itself are seen. Set `search.cacheMaxMB` to `0` to turn this off.
- **Missing images.** Images in notebooks stored online often aren't on this PC until OneNote downloads them, and the API can't read them until then. They're reported as unavailable (`binaryUnavailable`) instead of failing, and the server never fetches them by itself. Two ways to get them: turn on File > Options > Sync > "Download all files and images" in OneNote, or ask Claude to show the page in OneNote (`navigate_to`, which takes focus), which downloads its images (in the one page measured, within 2 seconds; large pages or slow connections may take longer), then read it again. `navigate_to` only works with OneNote open in a window: if OneNote is running without one, it returns ok but shows and downloads nothing.
- A File Printout's printout document (`one:XPSFile`) can't be read through the API at all. It's reported as unavailable too; the printout's page images are separate images.
- **Grants** (read grants now, write grants later) guard against mistakes and casual misuse, not against hostile software already running as you. A grant belongs to its section's or notebook's location (its file, folder or OneDrive URL), so a different section that later appears at a granted location inherits the grant without asking. A Claude Code elicitation hook can answer the grant dialog automatically, and headless runs cancel it (so nothing is granted). The plugin blocks Claude from changing `settings.json`: a `PreToolUse` hook denies any file edit of it, and any shell command that names it, and the skill tells Claude never to edit it or run the grant command. That block is best effort: a deliberately disguised shell command can get past it, and nothing stops another program from editing the file.
- *(planned, for writes)* The journal will hold copies of your note content on disk.

## Install

Only preview builds (`0.x` prereleases) exist so far.

### Claude Code plugin (recommended)

```text
/plugin marketplace add sentienceAdjacent/plugins
/plugin install onenote@sentienceadjacent
```

If you added `sentienceAdjacent/onenote-mcp` as a marketplace before, first run `/plugin uninstall onenote-mcp@onenote-mcp` and `/plugin marketplace remove onenote-mcp`.

This installs the MCP server and fetches the matching pre-built `OneNoteMcp.exe` from GitHub Releases, pinned by SHA-256. No .NET SDK is needed. Updates come from `/plugin marketplace update sentienceadjacent`. The catalog lives in [sentienceAdjacent/plugins](https://github.com/sentienceAdjacent/plugins), and its tools appear as `mcp__plugin_onenote_desktop__*`.

### Your own marketplace

To pin a version yourself, for example in a team marketplace, add an `archive` entry that points at a release zip and the hash from its `.sha256` asset:

```json
{
  "name": "onenote",
  "source": {
    "source": "archive",
    "url": "https://github.com/sentienceAdjacent/onenote-mcp/releases/download/v<ver>/onenote-<ver>-win-x64.zip",
    "sha256": "<the hex hash from onenote-<ver>-win-x64.zip.sha256>"
  }
}
```

Use `0.4.0-rc.4` or later: earlier releases ship a plugin named `onenote-mcp`. You can also host the zip internally and point `url` at your copy: keep the same bytes, so the hash still matches.

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

Each tool tells Claude Code it may return up to 500,000 characters (`_meta["anthropic/maxResultSizeChars"]`), so results up to the inline limit reach the model whole. Other clients don't know that key, and may cut a result short or save it to a file well below 100,000 characters. For them, lower the inline limit in `%APPDATA%\OneNoteMcp\settings.json` to what the client shows whole, so larger results come back as an index instead:

```json
{
  "schemaVersion": 1,
  "results": { "inlineMaxChars": 20000 }
}
```

`results` takes whole numbers: `inlineMaxChars` 20,000 to 380,000 (default 100,000), `ttlMinutes` 1 to 1,440 (default 30; how long a stored result lives after its last read), and `maxStoreMB` 16 to 4,096 (default 256; past it, the least recently read results are dropped). A value out of range makes the whole file invalid, and the server then uses the defaults.

`search.cacheMaxMB` (0 to 1,024, default 64) caps the estimated size of what `find_pages` remembers between searches: each page's text and ID counted as UTF-16, plus a small fixed overhead per page; past it, the least recently used pages are dropped. `0` turns the cache off, so every search reads every candidate page again:

```json
{
  "schemaVersion": 1,
  "search": { "cacheMaxMB": 0 }
}
```

`readGrants.onceMinutes` (1 to 60, default 5) is how long the grant dialog's **this section, for N minutes** choice lasts:

```json
{
  "schemaVersion": 1,
  "readGrants": { "onceMinutes": 10 }
}
```

As with `results` and `search`, a value out of range makes the whole file invalid: the server then uses the defaults, and your saved read grants count as absent until you fix it.

### From source

You need the .NET 10 SDK.

```powershell
git clone https://github.com/sentienceAdjacent/onenote-mcp
cd onenote-mcp
dotnet publish src/OneNoteMcp.Server -c Release -r win-x64
```

Then point your client at the published `OneNoteMcp.exe`, as in the previous section.

### Uninstall

Remove the plugin with `/plugin uninstall onenote@sentienceadjacent`, or delete the exe and its MCP config entry. *(planned)* `OneNoteMcp uninstall --purge` will also remove settings (read grants included), the journal and logs. Until then, delete `%APPDATA%\OneNoteMcp` and `%LOCALAPPDATA%\OneNoteMcp` by hand.

Large results are held in the server's memory only and vanish when it exits, so there are no result files to remove. Claude Code itself may save a large tool result to disk, under `~/.claude/projects/<project>/<session>/tool-results/`, when a result is bigger than it shows inline. Those files are Claude Code's, can hold note content, and neither uninstalling nor `--purge` removes them: delete them there if you need to.

## For agents installing this

Follow these steps in order, and stop to report to the user if any check fails:

1. Confirm the OS is Windows. Check that `ONENOTE.EXE` exists (normally `C:\Program Files\Microsoft Office\root\Office16\ONENOTE.EXE`), and that OneNote is running (`Get-Process ONENOTE`).
2. Confirm the shell is **not** elevated: `([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrator')` must be `False`.
3. In Claude Code, run the two `/plugin` commands above. Otherwise follow "Other MCP clients".
4. Verify: call `get_hierarchy` (the default scope is `notebooks`). It should list the user's notebooks.
5. Don't edit `%APPDATA%\OneNoteMcp\settings.json`, never run `OneNoteMcp grant` yourself, and never grant access yourself. Grants are the user's.

## Files it uses

| Path | Contents |
|---|---|
| `%APPDATA%\OneNoteMcp\settings.json` | timeouts, large-result limits (`results`), the search cache cap (`search`), read grants for password-protected sections (`protectedSections`), and later write grants, the journal and export settings. Optional |
| `%LOCALAPPDATA%\OneNoteMcp\logs\` | logs: IDs and error codes only, never note content |
| *(planned)* `%LOCALAPPDATA%\OneNoteMcp\journal\` | compressed page snapshots |
| *(planned)* `Documents\OneNote Exports\` | default export folder |

## How it works

```text
MCP client ──stdio──► OneNoteMcp.exe ──COM (one STA thread)──► OneNote desktop
```

- **One server process per client:** several can run at once.
- **COM calls go one at a time,** with timeouts. If OneNote restarts, the server reconnects.
- **Large results stay in memory,** in each server process, keyed by a `resultId`. The server indexes them by the exact character positions of their elements and serves parts by those positions, so it never re-serialises XML. `read_result` reads only that memory and never calls OneNote. Logs record a stored result's ID and size, never its content.

The design decisions are in the [architecture spine](_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md).

## Contributing

Issues and pull requests are welcome. Every pull request needs the contributor agreement in [CONTRIBUTING.md](CONTRIBUTING.md).

## Cutting a release (maintainers)

1. **Bump `<Version>`** in `Directory.Build.props`, in a PR. Use a new string every time, with a prerelease suffix for candidates (`0.4.0-rc.1`, `0.4.0-rc.2`, then `0.4.0`). Never reuse one: users who already have a version never download it again, even when it was rebuilt.
2. **Merge** the PR to `main`.
3. **Tag** the merge commit `v<Version>` and push the tag:

   ```powershell
   git tag v0.4.0-rc.1 origin/main
   git push origin v0.4.0-rc.1
   ```

`.github/workflows/release.yml` then:

- refuses the tag unless it is exactly `v<Version>` and its commit is on `main`, before building anything. It also refuses a version that already has a release;
- reruns every CI check on the tagged commit: the build with warnings as errors, the unit tests, the offline integration tests, `scripts/pack-plugin.ps1`, and `claude plugin validate --strict` on the staged plugin;
- publishes a GitHub Release, marked as a prerelease when the version has a suffix, with notes generated from the commits. Its assets are the plugin zip, the bare `OneNoteMcp.exe`, and a `.sha256` for each. It downloads them back and checks the hashes.

**Then pin the catalog:** run the `sync` workflow in [sentienceAdjacent/plugins](https://github.com/sentienceAdjacent/plugins) with `gh workflow run sync -R sentienceAdjacent/plugins`. Its daily run is only a backstop: GitHub pauses scheduled workflows in a public repo after 60 days without activity (re-enable it from the Actions tab). It checks the new zip against its `.sha256` and commits the pin to that repo's `main`. The pin only moves forward by semver, so an older release never replaces a newer one.

If a check fails, nothing is published: fix the cause and release a new version. Don't move the tag, and don't upload a rebuilt zip under an existing version. If only the sync fails, the release itself is good: fix the cause and run `sync` again. Release builds are unsigned for now.

## Acknowledgements

Much of what this project knows about OneNote's COM API, its XML and its quirks comes from [OneMore](https://github.com/stevencohn/OneMore) by Steven M Cohn and its [developer documentation](https://onemoreaddin.com/developers/Setup.htm). OneMore is a long-lived OneNote add-in, and its write-ups of measured behaviour and API defects saved us a lot of rediscovery. This project's code is its own. No OneMore source is included.

## License

Free to use, change and share for any noncommercial purpose, and inside any organization for its own internal use, including at work. Selling it, licensing it for a fee, or offering it as part of a paid product or service needs a separate license. Ask at license@sentienceadjacent.com. Forks and copies must keep the same terms and the attribution notice.

The exact terms are the [PolyForm Noncommercial License 1.0.0 plus an internal-use permission](LICENSE.md). Third-party components keep their own licenses ([notices](THIRD-PARTY-NOTICES.md)).
