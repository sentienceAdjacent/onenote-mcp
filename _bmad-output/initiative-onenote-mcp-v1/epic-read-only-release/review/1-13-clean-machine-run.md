# 1.13 install test on the work machine

Preflight is done and passed. Work down the list. Each step says **where** to type, **what** to type, and what to **write down**.

- **PowerShell** means a normal PowerShell 7 window (`pwsh`), not "Run as administrator". Keep one open for the whole test.
- **Claude** means a Claude Code session: run `claude` in that PowerShell window. To quit it, type `/exit`.
- When Claude asks to run a command, read it and approve it. Deny anything that isn't part of the README route: admin, policy changes, security exclusions.
- **If Windows or a policy blocks or warns about anything, stop** and write down what it said. Don't work around it.
- Write down only yes/no, counts and PASS/FAIL. Notebook names can go in chat, but not in this file.

The test installs OneNoteMcp three ways, one at a time, removing each one before the next. The plugin goes last because you keep it.

---

## Route 1: manual download

**1.** **Claude:** paste this prompt:

> Follow the "For agents installing this" section of https://github.com/sentienceAdjacent/onenote-mcp/blob/main/README.md using the "Other MCP clients, or Claude Code without the plugin" route. Download into `%LOCALAPPDATA%\Programs\OneNoteMcp`. Then list my OneNote notebooks.

**2.** When it tells you to restart: `/exit`, run `claude` again, and type `List my OneNote notebooks.`
Write down: **notebooks listed?** and **did the SHA-256 check pass?** (Claude reports it.)

**3.** `/exit`. **PowerShell:**

```powershell
"Mark-of-the-Web: " + [bool](Get-Item "$env:LOCALAPPDATA\Programs\OneNoteMcp\OneNoteMcp.exe" -Stream Zone.Identifier -ErrorAction SilentlyContinue)
```

Write down: **True or False.**

**4.** **PowerShell:** remove route 1.

```powershell
claude mcp remove --scope user onenote
Remove-Item -Recurse -LiteralPath "$env:LOCALAPPDATA\Programs\OneNoteMcp"
```

---

## Route 2: build from source

**5.** **Claude** (start `claude`): paste this prompt:

> Follow the "For agents installing this" section of https://github.com/sentienceAdjacent/onenote-mcp/blob/main/README.md using the "From source" route. Clone into `%USERPROFILE%\onenote-mcp-source-test`. Then list my OneNote notebooks.

**6.** When it tells you to restart: `/exit`, run `claude` again, and type `List my OneNote notebooks.`
Write down: **notebooks listed?**

**7.** `/exit`. **PowerShell:** remove route 2.

```powershell
claude mcp remove --scope user onenote
Remove-Item -Recurse -Force -LiteralPath "$env:USERPROFILE\onenote-mcp-source-test"
```

---

## Route 3: the plugin (you keep this one)

**8.** **Claude** (start `claude`): paste this prompt:

> Install the OneNote MCP server by following the "For agents installing this" section of https://github.com/sentienceAdjacent/onenote-mcp/blob/main/README.md, using the Claude Code plugin route. Then list my OneNote notebooks.

**9.** When it tells you to restart: `/exit`, run `claude` again, and type `List my OneNote notebooks.`
Write down: **notebooks listed?**

**10.** `/exit`. Close every other Claude Code window too. A running server keeps its log file open, and the purge needs it closed.

---

## Purge

**11.** **PowerShell**, in your clone's `scripts` folder:

```powershell
pwsh -File .\clean-install-check.ps1 -Phase Before
```

Write down: the **Mark-of-the-Web** line (yes or no).

**12.** **PowerShell:** run the purge, and type `purge` when it asks.

```powershell
$exe = Get-ChildItem "$env:USERPROFILE\.claude\plugins\cache\sentienceadjacent\onenote" -Recurse -Filter OneNoteMcp.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
& $exe.FullName uninstall --purge
"Exit code: $LASTEXITCODE"
```

Write down: **the exit code.** It should be 0.

**13.** **PowerShell:**

```powershell
pwsh -File .\clean-install-check.ps1 -Phase After
```

Write down: **PASS or FAIL**, and paste the lines above it.

**14.** **Claude** (start `claude`): type `List my OneNote notebooks.`
Write down: **does it still work?** It should: the purge removed only settings and logs, and the plugin stays installed.

You're done. The plugin stays installed for everyday use.

---

## Send back

| Step | Result |
|---|---|
| Preflight | All as expected. .NET 10 SDK and Git present |
| 2. Manual: notebooks listed? SHA-256 passed? | |
| 3. Manual: Mark-of-the-Web | |
| 6. Source: notebooks listed? | |
| 9. Plugin: notebooks listed? | |
| 11. Plugin: Mark-of-the-Web | |
| 12. Purge exit code | |
| 13. PASS or FAIL (plus its lines) | |
| 14. Still works after the purge? | |
| Any block or warning, at any step | |
| Anything confusing in the README | |

## Later: everyday use

- **Updates:** `/plugin marketplace update sentienceadjacent`, then update `onenote` in `/plugin`.
- **It only reads.** Password-protected sections stay closed until you grant access yourself.
- **Large results may be saved to disk by Claude Code** under `%USERPROFILE%\.claude\projects\...\tool-results\`, and they can contain note content. Handle them under your company's data rules.
