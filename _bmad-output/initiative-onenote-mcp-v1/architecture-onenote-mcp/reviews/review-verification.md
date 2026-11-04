# Review: verification lens, architecture-onenote-mcp

- **Reviewed:** `architecture-onenote-mcp.md` (status `draft`, updated 2026-11-03) and `.memlog.md`
- **Lens:** is every committed decision backed by web research or a reality check, or is it asserted from training data?
- **Date:** 2026-11-03. Read-only review. The only file written is this one.
- **Method:** primary sources (nuget.org API, the shipped nupkg, Microsoft Learn, code.claude.com). Two local checks: one validated live OneNote XML against the 2013 XSD, the other tested negative cases on that XSD.

## Verdict

**Mostly verified, with three corrections to make before final.**

The stack pins hold. .NET 10 is LTS. ModelContextProtocol 2.3.1 is the latest on nuget.org, and its server can use both elicitation and stdio. GetHierarchyParent, FindMeta and the Publish formats all exist with the shapes the spine assumes.

Three problems remain:

- AD-7 depends on an XSD whose official download link is now dead.
- Two Deferred rows are out of date. Anthropic now documents both items first-party.
- First-party docs show a hook path that weakens AD-6's claim that only the user creates a grant.

## Findings

### F1 (high): the official 2013 XSD link is dead. AD-7's validation step needs a pinned source

- **Claim:** AD-7 step 4 validates every write against the "2013 XSD". `src/OneNoteMcp.Xml` holds "XSD validation". Neither the spine nor the memlog records where the XSD comes from.
- **Evidence:**
  - Microsoft's only publication of the schema is a 2013 blog post, now in the Learn archive marked `ROBOTS: NOINDEX`: https://learn.microsoft.com/en-us/archive/blogs/descapa/onenote-2013-com-api-xml-schema-onenote-2013-xsd
  - That post's download link, `https://msdnshared.blob.core.windows.net/.../0336.OneNoteApplication_2013_xsd.txt`, now returns **HTTP 403 AuthorizationFailure** (checked 2026-11-03).
  - The Microsoft Learn developer reference doesn't link any XSD.
  - Copies still exist:
    - Wayback snapshot (200 OK, 47,961 bytes): http://web.archive.org/web/20200727033613/https://msdnshared.blob.core.windows.net/media/MSDNBlogsFS/prod.evol.blogs.msdn.com/CommunityServer.Blogs.Components.WeblogFiles/00/00/00/63/17/0336.OneNoteApplication_2013_xsd.txt
    - GitHub copies, for example `stevencohn/OneMore` at `Reference/0336.OneNoteApplication_2013.xsd`, plus `sevagas/OneNote-resources` and `idvorkin/onom`.
- **Reality check (done here):** I validated live output from this machine's OneNote (M365 desktop, `xs2013`, read only) against the Wayback XSD using .NET `XmlSchemaSet`:
  - one full `GetHierarchy(hsPages)` document (57 KB);
  - 15 `GetPageContent(piBasic)` pages (2 KB to 188 KB).
  - **All 16 had zero schema errors.**
  - A negative test showed the validator is really enforcing the schema. It rejected an undeclared attribute, an unknown child element, and an ID that fails the `ObjectID` pattern.
  - So the XSD fits current M365 output for these shapes. Not covered: binary-included reads (`piBinaryData`), and the partial XML used for updates.
- **Implications:**
  1. AD-7 depends on an artifact Microsoft no longer serves. The repo has to vendor it.
  2. Microsoft never stated redistribution terms for the file. Record the provenance: the Wayback URL, a SHA-256 hash and the 2013 blog post.
  3. The XSD's `ObjectID` pattern means a hand-built or placeholder ID fails validation. That's good, and it matches AD-4's opaque-ID rule. But step 4 validates partial update XML (see the UpdatePageContent notes on Learn). Prove the XSD accepts minimal partial documents, such as a new Outline with no ID, before step 4 becomes a hard gate.
- **Suggested fix:**
  - Add the vendored XSD to the Stack table, or add an AD-7 note: "vendored from Wayback snapshot of the 2013 blog download, SHA-256 …".
  - Add a Deferred row: "XSD accepts partial update XML: verify in the first write story; if not, validate only full-page round trips."

### F2 (medium): Deferred row "Elicitation support in Claude Code: third-party sources only" is out of date

- **Claim:**
  - The spine's Deferred row says elicitation support has "third-party sources only".
  - The memlog cites "v2.1.76+, third-party sources only".
- **Evidence:** first-party docs at https://code.claude.com/docs/en/mcp, section "Respond to MCP elicitation requests":
  - Form mode and URL mode are both supported, through a terminal dialog. Cloud sessions answer with a card at claude.ai/code or in the Desktop Code tab.
  - "`elicitation/create` is served only in interactive terminal sessions". In a non-interactive (`-p`) session, Claude Code asks the server to cancel instead.
  - Claude Code declares `elicitation: {form: {}, url: {}}` to stdio servers in the user's MCP config on v2.1.287 or later. Other connections on older protocol revisions get a bare `elicitation: {}`, which allows form mode only.
- **Server side, checked in the shipped nupkg** (`modelcontextprotocol.core/2.3.1`, `lib/net10.0` XML docs):
  - `ModelContextProtocol.Server.McpServer.ElicitAsync(ElicitRequestParams, CancellationToken)` and `ModelContextProtocol.Server.StdioServerTransport` both exist.
  - SDK docs: https://csharp.sdk.modelcontextprotocol.io/v1/concepts/elicitation/elicitation.html. They gate on `server.ClientCapabilities?.Elicitation`.
- **Implications:**
  - Elicitation itself is confirmed.
  - The v2.1.76 figure in the memlog is unverified. Use the documented version, or drop it.
  - The settings-file route in AD-6 is required, not just a fallback. Headless runs never get an elicitation.
  - AD-6 should gate on the capability the client declares, not on assumptions about the client.
- **Suggested fix:**
  - Change the Deferred row to: "confirmed first-party (form mode, interactive sessions only); test in access story."
  - Fix the memlog version.

### F3 (medium): Elicitation hooks can answer a grant prompt without the user. AD-6's "only the user creates a grant" needs a caveat

- **Claim:** AD-6 says "Only the user creates a grant, through MCP elicitation…". The memlog adds "no tool lets the model grant itself access".
- **Evidence:**
  - https://code.claude.com/docs/en/mcp says: "To auto-respond to elicitation requests without showing a dialog, use the `Elicitation` hook."
  - Per https://code.claude.com/docs/en/hooks:
    - `Elicitation` / `ElicitationResult` hooks match on the MCP server name.
    - They can return `action: accept | decline | cancel` along with form `content`.
    - Hooks load from user, project and local settings, from plugins and from managed policy.
  - So a repo's `.claude/settings.json`, or any installed plugin, could auto-accept an `onenote` grant elicitation with no dialog.
  - The model can't do this directly, but a file the model may be able to write (project settings) can.
  - I didn't read the hooks page's dedicated Elicitation section, because it was truncated. The decision-control fields come from the page's summary tables.
- **Suggested fix:**
  - Add to AD-6 (or Deferred): "Elicitation is a client-mediated channel. Hooks can auto-answer it."
  - Mitigations to weigh:
    - Grants made by elicitation could be session-scoped until confirmed in `settings.json`.
    - Or the elicitation form could require typing the notebook's name, so a generic accept-all hook doesn't pass.
    - At minimum, every grant is logged with how it was created.
  - Also, project-scoped hooks need the user to accept a trust dialog. Record that as the residual assumption.

### F4 (medium): Deferred row "Plugin layout details … unverified" can now be settled from first-party docs

- **Evidence** (https://code.claude.com/docs/en/plugins-reference):
  - **Both forms work and merge.** A plugin-root `.mcp.json` loads first, then `mcpServers` from `plugin.json`, inline or as a path. A later name replaces an earlier one.
  - **`mcpServers` can be an `https://` `.mcpb` bundle URL.** Claude Code "downloads the bundle into `.mcpb-cache/`, then reads it". That's a first-party answer to "how the launcher fetches the release exe", and possibly a better one than a hand-written launcher. The GitHub Release would attach a `.mcpb` instead of, or as well as, a bare `.exe`.
  - **Paths:** `${CLAUDE_PLUGIN_ROOT}` (changes on update, so don't write state there) and `${CLAUDE_PLUGIN_DATA}` (persists across updates; the right cache for a downloaded exe) both resolve in stdio `command`/`args`/`env`. On Windows they resolve with forward slashes.
  - **A top-level `bin/` folder means "claude.ai and Cowork don't install"** the plugin. Avoid `bin/` in `plugin/`.
  - **`claude plugin validate`** checks MCP entries (v2.1.281+). Add it to the CI in `.github/workflows/`.
  - **Plugin names:** a name with `claude` as a whole word draws a validator warning. Names starting `claude-` are an error.
- **Not checked:** the marketplace-reference page (`marketplace.json` fields), and whether a `.mcpb` can carry a self-contained win-x64 .NET exe. Verify both in the packaging story.
- **Suggested fix:** AD-15 can name `.mcp.json` (or inline `mcpServers`) and drop "unverified". Leave the launcher choice (`.mcpb` vs a fetch script into `${CLAUDE_PLUGIN_DATA}`) deferred, but list `.mcpb` as the leading option.

### F5 (low): smaller verification notes

- **.NET 10 LTS: confirmed.** Released 2025-11-11. Latest patch 10.0.14 (2026-10-25). End of support 2028-11-14. Source: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core. .NET 11 (STS) was at RC3 on that page. "C# shipped with .NET 10" is C# 14; naming it is optional.
- **ModelContextProtocol 2.3.1: confirmed latest**, listed and published **2026-11-03 07:46 UTC**, the same day as this review. Sources: https://api.nuget.org/v3-flatcontainer/modelcontextprotocol/index.json and https://api.nuget.org/v3/registration5-semver1/modelcontextprotocol/2.3.1.json. Patch churn is likely, so commit a lock file (`packages.lock.json`) and pin exactly. Targets: net8.0, net9.0, net10.0 and netstandard2.0.
- **GetHierarchyParent: confirmed.** `GetHierarchyParent(BSTR objectID, out BSTR parentID)` "throws if the object has no parent (for example a Notebook)". AD-6's parent-chain walk must treat that exception as "reached the notebook", not map it to `Internal`. Source: https://learn.microsoft.com/en-us/office/client-developer/onenote/application-interface-onenote
- **FindMeta: confirmed.** Same page. Signature `(startNodeID, searchStringName, out xml, fIncludeUnindexedPages, xsSchema)`. Like FindPages, it needs Windows Search.
- **Publish formats: confirmed.** `pfOneNote = 0` (.one), `pfOneNotePackage = 1` (.onepkg), plus `pfOneNote2007 = 8`. Source: https://learn.microsoft.com/en-us/office/client-developer/onenote/enumerations-onenote-developer-reference
  - The Publish description says it "exports the page you specify". The docs don't say whether `.one`/`.onepkg` works for a section or notebook ID.
  - The spine already defers this to the permanent-delete story, which is correct. Keep it deferred and unverified.
  - Note also that `bstrTargetFilePath` "must be one that does not already exist". That matches AD-8's unique file names.
- **Events: an official caveat to note.** The Deferred row says events need "a long-lived listener on the STA thread". The same Microsoft Learn page says "Events cannot currently be added in managed code." Given AD-1 (C#), record this caveat on the row, so the later round knows managed events may need a custom connection-point sink or may not work at all.
- **Windows shutdown semantics** (https://code.claude.com/docs/en/mcp): on Windows, Claude Code closes the server's stdin, then ends the process tree 300 ms later. This affects:
  - AD-3: release COM on stdin EOF, quickly.
  - AD-8: a kill mid-gzip can leave a truncated snapshot. Write to a temp name and rename, and have `restore`/retention skip unreadable files.
  - AD-10: the temp-plus-rename write already covers settings.
  - Before v2.1.296, Claude Code killed the process tree at once.
- **Spike provenance:** CLAUDE.md says late binding fails in pwsh 7. The spike's `read.jsonl` shows Windows PowerShell 5.1 creating `OneNote.Application` and reading through it fine. My validation check also ran through Windows PowerShell 5.1 `New-Object -ComObject`. This doesn't change AD-1, since the server is C#. But the spike record or CLAUDE.md should name the host ("pwsh 7 fails; Windows PowerShell 5.1 works") so nobody generalises the claim to PowerShell as a whole.

## Decisions checked and found adequately grounded

These are grounded by spike 1 evidence or the sources above: AD-1, AD-2, AD-3 (HRESULTs observed in the spike), AD-4 (`xs2013` recommended explicitly by Microsoft Learn), AD-9 codes, AD-11 (NavigateTo focus probe), and AD-12 (indexing behaviour from the spike plus the Windows Search requirement on Learn).

AD-5, AD-13, AD-14 and AD-16 are design conventions with no external facts to check.
