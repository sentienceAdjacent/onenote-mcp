# Adversarial review: spine update 1 (grant key by path, journal notebookPath, AD-17 lastModifiedTime source)

- Reviewer lens: build two units one level down that each obey every AD to the letter, yet don't fit together.
- Scope: the 2026-11-03 update (AD-6 grant key, AD-10 settings and journal shapes, AD-17 basis source), plus the ADs those edits lean on (AD-7, AD-8, AD-19).
- Verdict: **not ready to set final.** The direction is right: path keying fixes a real ID-regeneration hole. But "normalized path" and "the basis source" are each stated as a property, not as a single owned function. That leaves at least four pairs of compliant units that don't fit together. Two of them fail quietly: grants re-prompt forever, and stale-write rejections loop.

Severity: **High** means a likely clash in v1 with silent or looping failure. **Medium** means a likely clash with a visible error. **Low** means a gap to close or document.

---

## F1 (High): "normalized path" is a property, not a function, and the key's source isn't fixed

**Where:** AD-6 *Grant key*; AD-10 `notebooks{<normalizedPath>: …}`; AD-7 `Create` (auto-grant).

**Two compliant units that clash:**

1. **Settings type versus CLI, on key casing.**
   - The `grant` CLI stores `ToLowerInvariant(path).TrimEnd('\\')`.
   - The elicitation path in Core stores the path as OneNote reports it, and compares with `OrdinalIgnoreCase`.
   - Both are "compared case-insensitively, with no trailing separator". Yet `settings.json` now holds `"C:\Notes\Work"` and `"c:\notes\work"` as two keys.
   - A settings type that deserializes into a `Dictionary` with an ignore-case comparer throws on the duplicate. The file then reads as invalid, so **every** write in every process fails with `SettingsInvalid`.
   - A type with an ordinal comparer instead keeps both. `revoke` then removes one, and the other still grants access.
2. **Canonicalization depth.** One unit reuses the AD-7 `ExternalWrite` canonicalizer (`GetFullPath`, junctions resolved). Another trims only. Cases where they disagree:
   - a notebook under a junction or a mapped drive versus its UNC path;
   - `/` versus `\`;
   - a URL with `%20` versus a space.

   The result is a stale grant with every check passing.
3. **Where the path comes from.** The `Create` auto-grant writes its key from the path the server *passed* to `OpenHierarchy`, which may be relative, differently cased, or carry a trailing slash. The access check compares against the `path` attribute that OneNote *reports* in `GetHierarchy`. A notebook created inside a OneDrive-synced folder may even be reported as a cloud URL. So the user is re-prompted for a notebook the server just created and "auto-granted". `list_grants` shows a dead `created` entry.
4. **Which name the user types.** AD-6 says the user "types the notebook's name". The `Notebook` element has both `name` and `nickname`, and the display name is `nickname`.
   - One unit matches `nickname`. Another matches `name`.
   - Neither is told what to do when two open notebooks share a display name. They have different paths, so one of them may get the wrong grant.

**Fix: tighten AD-6.**
- There is exactly one `NotebookPath.Normalize(string)` in Core. Settings, access, journal, the CLI and `list_grants` all call it, and nothing else builds a key. Spell out the algorithm:
  - **Input:** only the `path` attribute of the `Notebook` element, as returned by `GetHierarchy`.
  - **Steps:** trim trailing `\` and `/`. Nothing else: no `GetFullPath`, no junction resolution, no URL decoding.
  - **Comparison:** `StringComparer.OrdinalIgnoreCase`.
  - **Stored key:** the normalized string, case preserved from the first write.
- The settings type loads `notebooks` with that comparer and treats a case-only duplicate as `SettingsInvalid`. It should name the offending key.
- **Every grant writer**, including the `Create` auto-grant, takes the key from a `GetHierarchy` read-back of the notebook. A notebook it creates is read back by the ID that `OpenHierarchy` returns. No writer ever uses a caller-supplied path.
- **Name matching:** the elicitation and the CLI match the typed text against `nickname` (falling back to `name`), case-insensitively. If more than one open notebook matches, they refuse with a message that lists the paths.
- **Evidence gap:** probe the reported `path` form for a cloud notebook (URL encoding, trailing `/`) and for a notebook created in a OneDrive-synced folder. Add both to Deferred if they can't be probed now.

---

## F2 (High): the AD-17 source rule binds the reader but not the basis the caller sends back, and contradicts hierarchy-object deletes

**Where:** AD-17 *Reads return the basis* (new sub-bullet); *Writes check the basis*; AD-7 classes `SoftDelete`, `PermanentDelete` and `HierarchyWrite`.

**Two compliant units that clash:**

1. **A skill recipe and the write tool.**
   - AD-4 returns raw hierarchy XML, and every `<one:Page>` in it carries `lastModifiedTime`.
   - A skill recipe, or the model itself, fills `basis.lastModifiedTime` from a `get_hierarchy` or `find_pages` result. AD-17 says the *read tools'* `meta` uses the page XML, and this recipe never touched `meta`.
   - The write tool obeys "`dateExpectedLastModified` comes from the basis" and passes the hierarchy value.
   - The probe showed the hierarchy lagging by weeks to months on 2 of 12 pages. On those pages, every write returns `0x80042010` (`StaleWrite`, "re-read"). The recipe then re-reads the hierarchy, gets the same lagging value, and loops.
   - The write side can't detect this, because the basis carries no record of where it came from.
2. **A page-delete story and a section-delete story.**
   - Step 3 ("read the current state") for a page `SoftDelete` might check existence through `GetHierarchy`, and so take the no-basis `dateExpectedLastModified` from the hierarchy. The amendment pins only the *read tools'* `meta`, not step 3.
   - A section or section-group delete has no page XML at all. The only `lastModifiedTime` it has is the hierarchy's.
   - A literal reader of "the hierarchy value is never used for the basis" therefore has no legal value. `DateTime.MinValue` is banned too. Meanwhile AD-17 binds `SoftDelete`, `PermanentDelete` and `HierarchyWrite`.
3. **Format drift.**
   - One read story passes the page-XML string through unchanged, for example `…T12:34:56.000Z`.
   - Another parses it and reformats it as ISO-8601, which can drop the milliseconds or emit 7-digit ticks.
   - The write side parses either form back into a `DateTime`. If precision is lost, `dateExpectedLastModified` is off by milliseconds, and the result is a spurious `0x80042010` or a missed one.
   - Neither AD-17 nor the Dates convention says the value round-trips byte for byte.

**Fix: tighten AD-17.**
- **Scope the source rule.**
  - For a **page** target: the reader's `meta.lastModifiedTime` **and** step 3's fallback both come from the root `lastModifiedTime` of a `GetPageContent` read.
  - For a **section or section group** target: the value comes from that node's `lastModifiedTime` in `GetHierarchy`, and the spine says so explicitly. Alternatively, state that hierarchy deletes don't use `dateExpectedLastModified` and rely on a hierarchy-hash basis.
- **Make the basis a single unit the caller can't assemble from hierarchy XML.** The read tools return `meta.basis` (`{lastModifiedTime, pageHash, elementHashes}`) as one object, and write tools accept only that object. The skill rule is "pass `meta.basis` from `get_page_content` unchanged; never build it". On top of that, add a server-side guard: at step 3, if `basis.lastModifiedTime` is earlier than the live page-XML value, but every hash in the basis matches, use the live value and add a warning. This turns a lagging basis into a working write instead of a loop.
- **Pin the format:** `meta.lastModifiedTime` is the attribute string exactly as OneNote returned it. Only `OneNoteMcp.Xml` parses it, using one shared parser.

---

## F3 (Medium): the journal's `notebookPath` has no stated form, and `restore_from_journal` doesn't say how it finds "the same notebook"

**Where:** AD-10 journal contents (new `notebookPath`); AD-8 `restore_from_journal`.

**Two compliant units that clash:**

1. **Two restore designs after a close and reopen.** Once the notebook has been reopened, every ID is new, so the entry's `parentId` is always gone. AD-8 then says to restore into "a `Recovered` section of the same notebook".
   - Restore unit A identifies "the same notebook" by `notebookId`. That ID is dead, so A returns `ObjectNotFound`. Restore is then useless after any reopen, which is exactly the case the update was meant to handle.
   - Restore unit B identifies it by `notebookPath`.
   - Both comply with the spine.
2. **Two path forms.** The journal writer records the raw `path` attribute. Restore compares it, with `Normalize` applied to one side only. Or the writer records the normalized, possibly lowercased, form and restore shows that to the user as the notebook's location. Either way, the trailing-separator and casing variants from F1 come back.
3. **Grant check on restore.** Should the AD-7 `Create` "write on the parent's notebook" check use the entry's `notebookPath`, or the live parent that restore resolved? Suppose the section was moved to another notebook and kept its ID (only page moves change IDs). Unit A checks the grant on the old notebook. Unit B writes into the new one.

**Fix: tighten AD-8 and AD-10.**
- `notebookPath` is stored as `NotebookPath.Normalize(path)`, the same function as F1.
- `restore_from_journal` resolves the target notebook only by `notebookPath`, matched against the open notebooks. `notebookId` is informational, like `lastSeenId`.
- `parentId` is used only if it still exists **and** its owning notebook, resolved through the gateway, has that path. Otherwise restore goes to `Recovered` in that notebook.
- If no open notebook has that path, restore returns `ObjectNotFound` with `userAction` "open the notebook at <path>, then retry".
- The grant check uses the notebook restore actually writes to.

---

## F4 (Medium): `lastSeenId` and the settings write have no single owner, and the read-modify-write isn't bounded by the mutex

**Where:** AD-10 settings (`lastSeenId`, mutex, `File.Replace`); AD-6 (elicitation, CLI); AD-19 schema migration.

**Two compliant units that clash:**

1. **Two owners of `lastSeenId`.**
   - The access-check unit updates `lastSeenId` whenever it sees a new ID. Settings are re-read at every check, so it updates "for information" every time. That makes a read-mostly path into a settings writer inside every write pipeline.
   - The grant unit writes `lastSeenId` only at grant time.
   - Both comply. The first adds mutex contention and file churn on every write.
   - Worse, a later story may "helpfully" use `lastSeenId` to carry a grant across a rename, which reverses AD-6's "goes stale".
2. **A lost update.** The CLI reads settings, prompts the user (which can take minutes), then takes `Local\OneNoteMcp.Settings` and writes the copy it read before the prompt. Meanwhile another process's `Create` auto-grant or elicitation grant has landed, and the CLI's `File.Replace` silently drops it.
   - AD-10 says the write happens *under* the mutex. It doesn't say the *read* that the write is based on happens under it too.
   - An elicitation unit that holds the mutex across the dialog to avoid this instead blocks every other grant writer for as long as the dialog stays open.
3. **Two migrators.** The ID-keyed shape was in the final spine. If any build or dev machine has written it, two new processes migrating at once could pick different keys: one re-keys by `Normalize(old.path)`, the other by the live hierarchy path. AD-19 says only "forward, done by the newer server when it writes". It doesn't give the version number for the path-keyed shape or the mapping.

**Fix: tighten AD-10.**
- Every settings mutation is one read-modify-write done entirely inside `Local\OneNoteMcp.Settings`: re-read, apply, write to a temp file, then `File.Replace`.
- The mutex is never held across a user prompt. Grant writers prompt first, then mutate under the lock.
- `lastSeenId` is written only when a grant is created or re-granted. The access check never writes settings.
- `lastSeenId` is never used to match or carry over a grant.
- State the `schemaVersion` of the path-keyed shape. If the ID-keyed shape never shipped, say "v1 is path-keyed; no migration". If it did, give the mapping: `key = Normalize(old.path)`, `lastSeenId = old key`, and on a duplicate keep the stricter grant (`permanentDelete` false wins).

---

## F5 (Low): "stale" conflates "notebook closed" with "path changed", and path reuse inherits a grant

**Where:** AD-10 "a grant whose path matches no open notebook is shown by `list_grants` as stale"; AD-6 *Grant key*.

1. **Closed versus stale.** A grant for a notebook that is merely closed matches no open notebook, so it shows as stale.
   - One `list_grants` or `grant` unit treats "stale" as safe to prune, for example a later "clean up stale grants" option.
   - Another treats it as a display state only.
   - Pruning deletes valid grants just because a notebook was closed today.
2. **Path reuse.** Delete a notebook's folder, then create or open a different notebook at the same path. The new notebook inherits the old grant. Under the old design, the ID plus path check caught this. Now that `lastSeenId` is informational, nothing does.
   - This is an accepted trade-off: after a close and reopen, a reused path and a reopened notebook can't be told apart.
   - It should still be written down, not left implicit.

**Fix:**
- In AD-10, `list_grants` reports `state: active | notebookNotOpen` for these. Keep `stale` only if a rename can actually be detected, otherwise drop the word. Nothing deletes a grant automatically.
- Add one line to AD-6 *Limits* and the README: a grant follows the path, so a different notebook opened at a granted path inherits the grant.
- The `Create` auto-grant over a path that is already granted (it was verified empty, so it's a reuse) replaces the entry and records `grantedVia: created`.

---

## Checked and holding

- **Multi-notebook payloads:** AD-6 still requires a grant per notebook touched. Path keying doesn't change how ownership is resolved, which still goes through the gateway.
- **AD-18 / AD-7 ordering:** the access check stays at step 1, outside the write lock, and the elicitation wait is outside the deadline. Path keying adds no new lock interaction, apart from F4's settings-mutex scope.
- **AD-4 `xs2013` decision:** this update leaves it unchanged, and it's correctly deferred with a named trigger.
