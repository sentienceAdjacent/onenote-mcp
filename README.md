# OneNote MCP

A local [MCP](https://modelcontextprotocol.io) server that lets Claude Code, and other MCP clients, read and manage the notebooks in your **OneNote desktop app** on Windows. It talks to OneNote through its COM automation API on your own machine. No cloud account, Microsoft Graph or sign-in is involved.

> **Status: in design.** The architecture is settled ([architecture spine](_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md)) and a capability spike has proven the COM API on M365 OneNote desktop ([spike findings](_bmad-output/initiative-onenote-mcp-v1/epic-read-only-release/spike-what-can-the-onenote-desktop-com-api-do-on-this-machine-findings.md)). No release exists yet. The install steps below describe how it will work.

## What it does

- **Reads everything:** notebooks, section groups, sections and pages, plus page content as OneNote's own XML, so tables, tags, ink, images and attachments survive the trip.
- **Writes:** create, edit, rename, move and delete. Writing works only on notebooks you have granted, and destructive changes are backed up first.
- **Search:** search notebooks, including pages the Windows Search index hasn't caught up with yet.
- **Exports:** export to PDF, Word and other formats, into a folder you control.

The tools map roughly one-to-one onto OneNote's COM API (`get_hierarchy`, `get_page_content`, `update_page_content`, …). A bundled skill teaches Claude how to use them well.

## Requirements

- Windows 10 or 11
- **OneNote desktop** (Microsoft 365 or OneNote 2016+, `ONENOTE.EXE`), **running**. The Windows 10 "OneNote for Windows" store app has no COM API.
- Claude Code (recommended), or any MCP client that can run a local stdio server
- Run the server at the same privilege level as OneNote, normally *not* as administrator

## Safety model

| | |
|---|---|
| **Reading** | Allowed on every notebook. |
| **Writing** | Needs a grant for each notebook. You give it by typing the notebook's name in a confirmation dialog, or by running `OneNoteMcp grant` in a terminal. The model can't grant itself access. |
| **Permanent delete** | Needs a second, separate grant. Normal deletes go to OneNote's recycle bin. |
| **Undo** | Before every change to a page, the server saves a compressed snapshot to a local journal (30 days by default). `restore_from_journal` brings the content back as a new page. |
| **Stale edits** | A write is refused if the page changed since it was read. |
| **Focus** | The server never brings OneNote to the front or moves your view unless you ask for it. It never closes or kills OneNote. |
| **Exports** | Exports only go to `Documents\OneNote Exports`, or to folders you add. |

**Limits, stated honestly:**
- Grants guard against mistakes and casual misuse, not against hostile software already running as you.
- A grant belongs to the notebook's location (its folder or OneDrive URL), not to the notebook itself. If you delete a granted notebook and a different one later appears at the same location, it inherits the grant. Revoke grants you no longer need with `OneNoteMcp grant --revoke`.
- A Claude Code hook can auto-answer confirmation dialogs.
- The journal holds copies of your note content on disk.
- Search results may be incomplete when Windows Search is behind. Results say so.
- Images in cloud notebooks that OneNote hasn't downloaded can't be read. The server reports them as unavailable instead of failing.

## Install (planned)

### Claude Code plugin (recommended)

```text
/plugin marketplace add <owner>/onenote-mcp
/plugin install onenote-mcp
```

This installs the MCP server and the `onenote` skill together, and fetches the matching pre-built `OneNoteMcp.exe` from GitHub Releases. No .NET SDK is needed.

### Other MCP clients

1. Download `OneNoteMcp.exe` from the [latest release](https://github.com/<owner>/onenote-mcp/releases) to a folder of your choice.
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
git clone https://github.com/<owner>/onenote-mcp
cd onenote-mcp
dotnet publish src/OneNoteMcp.Server -c Release -r win-x64 --self-contained
```

Then point your client at the published `OneNoteMcp.exe`, as in the previous section.

### Grant write access

```powershell
OneNoteMcp grant "Work Notes"            # write
OneNoteMcp grant "Work Notes" --permanent-delete
OneNoteMcp grant "Work Notes" --revoke
```

Inside Claude Code you can also just ask Claude to write. The server opens a confirmation dialog the first time.

### Uninstall

```powershell
OneNoteMcp uninstall --purge   # removes settings, grants, the journal and logs
```

Then remove the plugin with `/plugin uninstall onenote-mcp`, or delete the exe and its MCP config entry.

## For agents installing this

Follow these steps in order, and stop to report to the user if any check fails:

1. Confirm the OS is Windows. Check that `ONENOTE.EXE` exists (normally `C:\Program Files\Microsoft Office\root\Office16\ONENOTE.EXE`), and that OneNote is running (`Get-Process ONENOTE`).
2. Confirm the shell is **not** elevated: `([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrator')` must be `False`.
3. In Claude Code, run the two `/plugin` commands above. Otherwise follow "Other MCP clients".
4. Verify: call `get_hierarchy` with scope `notebooks`. It should list the user's notebooks.
5. Do **not** grant write access yourself, and do not edit `%APPDATA%\OneNoteMcp\settings.json`. Tell the user how to grant access, as shown above.

## Files it uses

| Path | Contents |
|---|---|
| `%APPDATA%\OneNoteMcp\settings.json` | grants, journal location and retention, timeouts, export folders |
| `%LOCALAPPDATA%\OneNoteMcp\journal\` | compressed page snapshots (default location; configurable) |
| `%LOCALAPPDATA%\OneNoteMcp\logs\` | logs: IDs and error codes only, never note content |
| `Documents\OneNote Exports\` | default export folder |

## How it works

```text
MCP client ──stdio──► OneNoteMcp.exe ──COM (one STA thread)──► OneNote desktop
```

- **One server process per client:** several can run at once, and they share settings and the journal safely.
- **COM calls go one at a time,** with timeouts. If OneNote restarts, the server reconnects.

The design decisions are in the [architecture spine](_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md).

## Acknowledgements

Much of what this project knows about OneNote's COM API, its XML and its quirks comes from [OneMore](https://github.com/stevencohn/OneMore) by Steven M Cohn and its [developer documentation](https://onemoreaddin.com/developers/Setup.htm). OneMore is a long-lived OneNote add-in, and its write-ups of measured behaviour and API defects saved us a lot of rediscovery. This project's code is its own. No OneMore source is included.

## License

To be decided.
