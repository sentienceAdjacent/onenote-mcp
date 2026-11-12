---
title: 'Findings: write probes, part 2: hierarchy, delete and backup'
type: 'spike-findings'
findings_for: '1'
status: 'draft'
created: '2026-11-11'
machine: 'Windows 11 Pro 10.0.26300, OneNote desktop (M365), not elevated; the OneNote build was not re-recorded by these runs'
evidence: '%LOCALAPPDATA%\OneNoteMcp-Test\probes\write-probes-hierarchy-*.jsonl and write-probes-cloud-jam-*.jsonl (outside the repo)'
---

# Findings: write probes, part 2: hierarchy, delete and backup

**Draft.** All four runs are in: 1 (local), 2 (cloud), 3 (protected-section deletes) and 4 (notebook deletes), with the cloud-jam investigation.

**Top finding (run 4): COM can't delete a notebook.** Every notebook-level `DeleteHierarchy`, soft and permanent, local and cloud, failed `0x80042014` hrObjectDoesNotExist, and the notebook stayed open and on disk. One local soft delete failed `0x80042010` first, its date check. A notebook backs up only as a package (`.onepkg`); `pfOneNote` is refused. So a notebook delete isn't a COM operation: AD-7's notebook rows and epic 5's delete stories must refuse it, or leave it to the user (constraint 18).

**Top finding (run 3): password-protecting a section gives the section and every page in it new IDs.** Names, page `dateTime` and the pages' hierarchy `lastModifiedTime` stay the same; `encrypted="true"` appears. Seen on MCP Test and on MCP Test Cloud, in every run that protected a section (constraint 15). OneDrive's recycle bin showed the section's old `.one` deleted at the moment Phill protected it (observed by Phill), so protecting writes a new file, which explains the new IDs. Anything keyed on a section's or page's ID breaks the moment a user protects that section: grants and keys (AD-6, AD-21), the AD-8 journal, AD-7 backups, and IDs an agent remembers. Epic 5's stories must re-resolve by name or path.

**Top finding (run 3): a protected section's backup can end up on disk in the clear.** Proven: once OneNote has opened the backup of an unlocked protected section (`OpenHierarchy`, as the probe did), the `.one` on disk holds its page titles and body text as plain UTF-16, local and cloud. An encrypted section's own `.one` has no readable text. Not proven: whether `Publish` itself writes plaintext, because opening the file made OneNote rewrite it (it grew from about 19.2 KB to 21.4 KB). The deciding probe is **pending** (constraint 17). If `Publish` writes plaintext, AD-7's backup-before-permanent-delete puts protected content on disk unprotected, against AD-21. Either way, opening a backup does.

**Short answer:** hierarchy writes work, but seven more behaviours change the spine:

1. **`DeleteHierarchy` checks the hierarchy's date, for pages too.** On a page whose two dates differed, the hierarchy date was accepted and the page-XML date refused `0x80042010`, displayed or not. That is the opposite of `UpdatePageContent` (spike 2.2 constraint 3), so AD-17's "a page's basis is its page XML date" can't apply to page deletes.
2. **`UpdateHierarchy` moves silently.** A known ID listed under another parent is moved there, with no error. A partial child list keeps the siblings it leaves out (unlike a page Outline) and reorders the ones it lists.
3. **A section group can't be backed up.** `Publish` fails for a section group in every form tried, so AD-7's "back up, then permanently delete" can't be met for a group. Pages and sections publish to `.one` in about 0.1 s.
4. **A backup hasn't been shown to bring anything back.** The published file opens, but the hierarchy listed no pages in it, and Phill saw it only as a hidden Open Section. On a cloud notebook a permanent delete leaves no copy in OneDrive's recycle bin (Phill), so the backup is the only way back.
5. **Relative names must be checked before COM.** `..\` made a phantom `..` section group that jammed MCP Test's sync. A name without `.one` returns `0x80042004`; `*` and `?` return `0x80042006` on create and are silently stripped on rename; an existing name returns the existing section's ID, with no error.
6. **A section-group rename on a cloud notebook didn't reach OneDrive** (Phill: the old name 4.5 h later in OneNote for the web), while a section rename did.
7. **A soft-deleted section group dissolves.** The group leaves the hierarchy, and its sections go to the recycle bin one by one. A page already in the recycle bin can't be permanently deleted (`0x8004200c`).

One problem isn't settled: MCP Test Cloud's sync jammed during the first cloud run (`0xE0000644 9zu13`, observed by Phill), and six repro runs couldn't reproduce it (see Failure modes).

**How it ran.** Opt-in scenarios in `tests/OneNoteMcp.Integration/WriteProbes/` (`HierarchyProbes.cs`, `CloudJamRepro.cs`), run on 2026-11-11 and early 2026-11-12 (UTC) with Phill present, on MCP Test (local) and MCP Test Cloud (OneDrive). Every write went through `TestNotebook`'s guard, inside a fresh `probe run <stamp>` section group; no write reached any other notebook. Each row below cites a probe name and a run stamp: search for it in the JSONL logs. Times are UTC unless marked local. The OneDrive account ID is written `<cid>`. "Observed by Phill" marks what Phill saw in OneNote, OneNote for the web or OneDrive and reported; it isn't in the logs.

**Which run counts.**
- **Run 1, local.** `write-probes-hierarchy-local-20261111-152447` for create, hierarchy writes, soft and permanent deletes, and the hidden-page dates. Its dates step then stopped at an unanswered pause, and its backup step blocked (Failure modes). `-164036` for the dates (all of them), backup, HRESULTs and the modal dialog: it ran those four steps only, and finished (83 rows). `-033107` (stopped mid-run, see Failure modes) adds the `..\` and `*?` create rows, and the only local page whose two dates differed. `-150859` and `-151215` only record the jam (Failure modes).
- **Run 2, cloud.** `write-probes-hierarchy-cloud-20261111-222514` for everything except the page dates: its dates pause went unanswered. `write-probes-hierarchy-cloud-20261112-021319` re-ran the dates step.
- **Run 3, protected deletes.** `write-probes-protected-delete-20261112-032127`: the local pass, the password-dialog modal rows, the cloud pass and the HRESULT summary, complete. Two earlier runs add only the ID change:
  - `-023431` (local and cloud) couldn't find the protected section afterwards (`protected-*-protected`: `node: null`), because protecting it had changed its ID. The team lead's read-only reads of MCP Test Cloud before and after that protect are extra cloud evidence. The probe was then fixed to find the section by name.
  - `-024414` (local) contributes `protected-local-protected` and `protected-local-locked-state`. It then ended when the PC ran low on memory and Claude Code's process was reaped, not by a probe failure.
- **Run 4, notebook deletes.** `write-probes-notebook-delete-20261112-034716`, complete: local throwaways a, b and c, then `MCP Test Throwaway Cloud`, which Phill created in OneDrive Documents at a pause.
- **The jammed cloud run** `-20261111-165147`, its retry `-165825`, and the repro runs `write-probes-cloud-jam-repro-20261111-{191703,192800,193209,193545,211303,221810}` and `write-probes-cloud-jam-timeout-20261111-221624` count only for Failure modes and the cloud create timings.

## Constraints for the architecture

To be logged as `constraint` lines in `architecture-onenote-mcp/.memlog.md` once the draft is final.

1. **AD-17: a delete's date is the hierarchy's.**
   - `DeleteHierarchy` compared the date it was sent with the target's hierarchy `lastModifiedTime`, pages included.
   - Where a page's two dates differed: the hierarchy date was accepted (`dates-cloud-page-hierarchy`, `-021319`). The page-XML date was refused `0x80042010` on a hidden page (`dates-cloud-page-pageXml`, `-021319`) and on displayed pages (`dates-cloud-page-shown-pageXml`, `-021319`; `dates-local-page-shown-pageXml`, `-033107`).
   - Where they were equal, both were accepted (`dates-local-page-*`, `-152447`, `-164036`).
   - A stale date (1 h older) was always refused: pages local and cloud, displayed or not, and sections (`dates-*-stale`, every run).
   - Sections: the hierarchy date was accepted (`dates-local-section-hierarchy`, `-152447`, `-164036`; `dates-cloud-section-hierarchy`, `-222514`, `-021319`).
   - `DateTime.MinValue` skips the check: the delete was applied on local and cloud pages (`dates-*-page-minValue`). The server must never send it by accident.
   - **The opposite of part 1.** Part 1's Dates row found `UpdatePageContent` accepts the page XML's date and refuses a differing hierarchy date `0x80042010` (spike 2.2 constraint 3). On a page whose two dates differ, `DeleteHierarchy` does the reverse: it accepts the hierarchy date and refuses the page-XML date (`dates-cloud-page-hierarchy`, `dates-cloud-page-pageXml`, `-021319`). `MinValue` skips its check.
   - **So the date a write sends depends on the method.** Page-content writes (`UpdatePageContent`, and `DeletePageContent` by inference) send the page XML's root `lastModifiedTime`, as AD-17 says. `DeleteHierarchy` sends the target's hierarchy `lastModifiedTime`, whether the target is a page, a section or a section group.
   - **For epic 5's delete stories and AD-17's wording.** AD-17 should name the date per method, not per target kind: "for a page, the page XML's date" holds only for page-content writes. A page delete reads the page's hierarchy `lastModifiedTime` (`GetHierarchy`) as the date it sends. The soft-delete and permanent-delete stories then pick how the caller's basis fits in: either the basis for a page delete becomes the hierarchy date, or the server checks the caller's page-XML basis itself (step 5) and sends the hierarchy date read in the same step 3. Neither story may send `MinValue`.
   - It also resolves the Deferred row on which date `DeleteHierarchy` accepts.
2. **AD-17: dates don't track hierarchy writes well.**
   - A rename didn't move the renamed node's own `lastModifiedTime`, for a section or a section group, local or cloud (`update-*-rename-section`, `-rename-section-group`: before and after equal). A moved section kept its own date too (`update-*-accidental-move`).
   - The parent's date has 1 s resolution. It moved after 11 of the 21 applied `UpdateHierarchy` writes in the three runs (`-033107`, `-152447`, `-222514`) and stayed in the same second after the rest; each section-group rename left it unchanged.
   - Reordering pages moved the section's date (`update-*-reorder-pages`). A page added to a section moved the section's date (`update-*-other-writer-lmt`).
   - So the hierarchy basis can't catch a concurrent rename. AD-17 already calls it best effort; these rows say how weak.
3. **AD-7 (Create): validate the relative name before COM.**
   - `..\` from a nested group was accepted and made a section group named `..` (`create-local-relative-name-up`, `-033107`: parent `..`, path `…\probe sub <stamp>\..\probe up <stamp>.one`). MCP Test then showed sync error `0xE0000644 9zu13` until Phill deleted the `..` group and synced (observed by Phill), and two later runs blocked on their first create (Failure modes). Refuse `..`, `\` and `/` in a relative name.
   - A section name without `.one` returns `0x80042004` hrSectionDoesNotExist, creating nothing (`create-*-section-no-extension`, local and cloud; `r3-no-extension`, `-193545`). AD-9 would map that to `ObjectNotFound`, which misleads. Add `.one` or refuse before COM.
   - `*` and `?` in a create return `0x80042006` hrFileDoesNotExist (`create-local-invalid-name`, `-033107`). Refuse characters Windows file names can't hold.
   - An existing name returns the existing section's ID, with no error and no second section (`create-*-section-existing-name`, local and cloud: `sameId: true`, `withThatName: 1`). A create tool must check the name first, or report "already existed" rather than "created".
   - The parent must be a notebook or section group: a section or a page as the parent returns `0x80042014` (`create-*-under-section`, `create-*-under-page`).
4. **AD-7 (HierarchyWrite): what `UpdateHierarchy` does with the XML.**
   - **Move:** a section listed under another group, by its ID, is moved there, with no error (`update-*-accidental-move`, local and cloud; the cloud move synced, observed by Phill). Until epic 3's move story, the HierarchyWrite pipeline must refuse any ID that isn't already a child of the node it is under in step 3's read.
   - **Partial lists:** siblings left out are kept, and the listed ones are reordered to the order sent (`update-*-partial-children`: s2, s1 sent; s2, s1 then the rest). This is unlike `UpdatePageContent`, where an OE left out is deleted. The reorder synced on cloud (observed by Phill).
   - **Order:** listing every child in reverse put section groups before sections and returned `0x80042001` hrInvalidXML, applying nothing (`update-*-reorder-sections`). The 2013 schema's `SectionGroup` and `Notebook` types list `Section` elements before `SectionGroup` elements, so the XSD gate catches it. A section reorder works when sections come first. Reordering section groups among themselves wasn't tried.
   - **Add:** a `Section` or `SectionGroup` without an ID is added; siblings stay (`update-*-add-section`, `-add-section-group`).
   - **Names:** a sibling's name returns `0x80042017` hrInvalidName (`update-*-rename-existing-name`). A name with `*` and `?` returns ok and is stored with them stripped (`hr-local-update-invalid-name`, `-164036`: `probe bad*? <stamp>` read back as `probe bad <stamp>`). Validate names before COM and read the name back.
5. **AD-7 (HierarchyWrite): renaming a section group in a cloud notebook.** The local hierarchy showed the new name and path (`update-cloud-rename-section-group`, `-222514`), but OneDrive kept the old name: Phill saw the old name in OneNote for the web 4.5 h later, while the section rename beside it synced. The section moved into that group afterwards reads back with the *old* folder name in its path (`update-cloud-accidental-move`, `-222514`: `…/probe upd sub <stamp>/probe s3 <stamp>.one`, where the group is now `probe upd sub renamed <stamp>`). Entry 2 decides whether to refuse it on cloud notebooks, warn, or ask for a Sync Now and a check.
6. **AD-7 / AD-8 (PermanentDelete): the backup.**
   - `Publish` `pfOneNote` of a section writes a `.one` file in about 0.1 s (local 14,880 bytes, 105–120 ms; cloud 15,128 bytes, 128 ms; `backup-*-section-publish`). A page publishes too (11,136 bytes, `backup-*-page`). AD-3's 300 s `Publish` timeout is far above that.
   - A section group fails every try: `.one` gives `0x80042014`, no extension `0x8004201a`, `pfOneNotePackage` `.onepkg` `0x80042014`; nothing is written (`backup-*-section-group-try-1..3`, local and cloud). So a section group's permanent delete can't meet AD-7's backup rule. Entry 2 picks: back up each section in it, or refuse the delete. This answers the Deferred row on a section group's permanent delete; the delete itself wasn't run.
   - Publishing over an existing file returns `0x8004201a`, as spike 1 found (`backup-*-existing-file`).
   - Publishing into a folder that doesn't exist raised OneNote's modal "We couldn't find that file." and the call never returned (observed by Phill; `backup-call-timeout`, `-152447`: 60 s). The server must create or check the backup folder first.
   - On MCP Test Cloud, OneDrive's recycle bin held none of run 2's three permanently deleted sections (`probe del a`, `probe del b` (deleted from OneNote's recycle bin), `probe bak s`; observed by Phill). Run 3's permanently deleted protected section, also deleted from OneNote's recycle bin, *did* appear there (`probe vault 1112-032127.one` from `OneNote_RecycleBin`, 45.7 KB; observed by Phill). So a permanent delete may or may not leave an OneDrive copy (Evidence gaps). The backup is the only way back that can be counted on.
7. **AD-8: restoring from a backup is unproven.**
   - `OpenHierarchy` opened the published `.one` and returned an ID in 42–55 ms. A pages-scope `GetHierarchy` of that ID then listed no pages, where the original had two (`backup-local-section-open-backup`, `-152447`, `-164036`; `backup-cloud-section-open-backup`, `-222514`: `samePageNames: false`).
   - The protected sections' backups did the same, and the opened copy didn't carry `encrypted` either (`protected-*-open-backup`, `-032127`: `asOpened` with no pages, `encryptedKept: false`).
   - In runs 1 and 2 Phill saw the backup copies only as hidden Open Sections, and the close pauses were skipped. In run 3 he closed each one (`protected-*-close-backup` resumed after 91 s and 79 s).
   - AD-8 returns `NotSupported` for `onenoteBackup` entries in v1, with a `userAction` naming the file. That stays right, but the `userAction` can't promise the file brings the section back until a later probe shows its pages.
8. **AD-7 (SoftDelete): where things go.**
   - A page goes to the section's Deleted Pages under a new ID (`soft-delete-*-page`, local and cloud).
   - A section moves to `OneNote_RecycleBin` and keeps its ID, with `isInRecycleBin` (`soft-delete-*-section`).
   - A section group leaves the hierarchy: no group appears in the recycle bin, and its section appears there on its own (`soft-delete-*-section-group`, `-152447`, `-222514`). On OneDrive the group's folder and an `Open Notebook.onetoc2` went to OneDrive's recycle bin (observed by Phill).
   - So `restore_from_recycle_bin` can't restore a group as a unit; the hierarchy snapshot AD-7 takes before a soft delete is the only record of the group.
   - This repeats spike 1 rows 19 and 22 for pages and sections.
9. **AD-7 (PermanentDelete): what can be permanently deleted.**
   - A page and a section (live or recycled) were removed from the hierarchy, local and cloud (`permanent-delete-*`).
   - A page already in the recycle bin returns `0x8004200c` hrPageReadOnly and stays (`permanent-delete-*-recycled-page`, local and cloud).
   - A recycled section group can't be addressed, because none exists (constraint 8).
10. **Local files lag the hierarchy.** Read straight after the call:
    - A soft-deleted section's `.one` was still at its old path and absent from the recycle-bin path (`soft-delete-local-section`, `-033107`, `-152447`).
    - A permanently deleted section's `.one` was still on disk (`permanent-delete-local-section`, `-033107`, `-152447`; `backup-local-section-delete`, `-152447`, `-164036`).
    - A recycled section's file appeared at its recycle-bin path only after its permanent delete (`permanent-delete-local-recycled-section`, `-033107`).
    - Verify writes against the hierarchy, never the file system. Whether OneNote removes the old files later wasn't checked (Evidence gaps).
11. **AD-3 / AD-9: modal dialogs.**
    - With File > Options open (`-164036`): `UpdateHierarchy` returned `0x80042030` and applied nothing.
    - `GetHierarchy`, `OpenHierarchy` (create), and `DeleteHierarchy` soft and permanent went through and applied.
    - `Publish` returned `0x80042006` hrFileDoesNotExist. Its cause is unclear (Evidence gaps).
    - The section password dialog gave exactly the same results (`modal-password-*`, `-032127`): `GetHierarchy` and `OpenHierarchy` ok, `UpdateHierarchy` `0x80042030` and not applied, both `DeleteHierarchy` modes ok and applied, `Publish` `0x80042006`.
    - As in spike 2.2, only the update method is blocked.
    - Two dialogs block instead of returning an error: "OneNote is syncing changes" and "We couldn't find that file." (Failure modes). Both surface as our `Timeout`, and AD-9 already makes a timed-out write `outcomeUnknown`.
12. **AD-3: cloud creates are slow.**
    - On MCP Test Cloud, a new section or group took 3.6–6.4 s (`create-cloud-*`, `-222514`). An existing name took 1.4 s.
    - In the burst repro, one section took 12.8 s (`r4-burst`, `-211303`).
    - `UpdateHierarchy` took 124–158 ms and `DeleteHierarchy` 104–147 ms on cloud. Locally every hierarchy call took under 0.4 s.
    - A create that timed out at a 2 s limit still landed about 3 s later (`r5-timeout-call-timeout`, `-221624`; the landing observed by Phill).
    - So a create's timeout must allow tens of seconds on cloud, and a timed-out create is `outcomeUnknown`, as AD-9 says.
13. **AD-9: codes.** Recorded as each row's `code`.
    - Already mapped and confirmed: `0x80042000` and `0x80042001` → `InvalidXml`; `0x80042010` → `StaleWrite`; `0x80042014` → `ObjectNotFound`; `0x8004201a` → `FileExists`; `0x80042030` → `ModalDialogOpen`.
    - `0x80042017` hrInvalidName (unmapped, so `Internal`) needs a code: `InvalidArgument` fits.
    - `0x8004200c` hrPageReadOnly (deliberately `Internal` "until epic 2's write tools") needs one now.
    - `0x80042004` for a missing `.one` and `0x80042006` for bad characters are argument errors; constraint 3's checks before COM should stop them reaching AD-9.
    - `Publish`'s `0x80042014` and `0x8004201a` on a section group should surface as `BackupFailed`, not `ObjectNotFound` or `FileExists`.
14. **AD-11: focus.** `OpenHierarchy` creates kept the foreground window (`focusKept: true`, local and run 2), except two run-group creates: the jammed cloud run's (`create-cloud-section-group`, `-165147`) and run 3's local one (`create-protected-local-section-group`, `-032127`), both `focusKept: false`. The cause wasn't isolated, so a create can't be assumed to leave focus alone.
15. **AD-6 / AD-7 / AD-8 / AD-21: protecting a section changes its IDs.**
    - After Phill password-protected `probe vault <stamp>` in OneNote's UI, the probe found it by name: `sectionIdChanged: true`, `pageIdsChanged: 3` (all three pages), `encrypted: "true"`. Local and cloud in run 3 (`protected-local-protected`, `protected-cloud-protected`, `-032127`), and local in `-024414`.
    - The pages kept their names and their hierarchy `lastModifiedTime` (their creation time; `protected-*-soft-delete-page`, `before`). The section's own date became the time of the protect.
    - `-023431`: the probe looked the section up by its pre-protect ID and got `node: null`, local and cloud. The team lead's read-only reads of MCP Test Cloud before and after showed the section's ID going from `{2B6E43F2…}` to `{1592095C…}`, all three page IDs changing to sequential values (ending …05218, …05219, …05220), names, page `dateTime` and `lastModifiedTime` unchanged, and `encrypted="true"` appearing.
    - Why: OneDrive's recycle bin shows `probe vault 1112-023431.one` deleted at the moment Phill protected the cloud section in run `-023431`, and `probe vault 1112-032127.one` (30.8 KB, from `probe run 1112-032127`) at the moment he protected it in run 3 (observed by Phill). Protecting writes a new `.one` in place of the old one.
    - **Implications.** AD-6 and AD-21 already key grants on paths (`NotebookKey`, `SectionKey`), which survive this; anything stored by ID doesn't. That covers the AD-8 journal's `parentId` (its `parentNamePath` and `SectionKey` fallbacks still work), the ID inside an AD-7 backup or hierarchy snapshot, the created-ID sets and recycle-bin lookups in epic 5's stories, and IDs an agent remembers between calls. Epic 5's stories must re-resolve a section or page by path or name when its ID is gone, and treat `ObjectNotFound` on a remembered ID as "re-read", as AD-9 already says. Like a reopen (spike 2.2 constraint 9), protecting is another user action that regenerates IDs.
16. **AD-7 / AD-21: deletes and backups in protected sections** (run 3, `-032127`, local and cloud alike unless noted).
    - **Page soft delete, unlocked: still unanswered.** The probe sent the page-XML date, which after the protect was the section's protect time, while the page's hierarchy date was its creation time. OneNote refused it `0x80042010`, and nothing moved (`protected-*-soft-delete-page`; also `-024414`). That matches constraint 1, but it means the Deferred question (recycle bin, refused, or permanent?) wasn't reached. A re-run with the hierarchy date is needed.
    - **Locked section.**
      - It keeps its ID, shows `locked="true"` and lists no pages (`protected-*-locked-state`).
      - A page soft delete returns `0x8004201b` hrSectionEncryptedAndLocked, mapped to `SectionLocked` (`protected-*-soft-delete-page-locked`).
      - `Publish` returns `0x80042014`, which maps to `ObjectNotFound` and misleads; the PermanentDelete pipeline should check `locked` first and answer `SectionLocked` (`protected-*-publish-locked`).
      - A create with the locked section as the parent returns `0x80042014` (`protected-*-create-under-locked`). Only sections can be password-protected in OneNote, so a locked section group or notebook can't exist; the answer to "parent locked" is that a locked section can't be a parent, like any section (constraint 3).
    - **Backup of an unlocked protected section:** `Publish` `pfOneNote` writes a `.one` (local 19,232 bytes, 210 ms; cloud 19,184 bytes, 152 ms; `protected-*-backup-publish`), and once opened that file holds plaintext; whether `Publish` alone writes plaintext is pending (constraint 17).
    - **The whole section soft-deleted:** it moves to `OneNote_RecycleBin` with the same ID and keeps `encrypted="true"`. Read straight after, it is already `locked="true"` with no pages listed, although it was unlocked when deleted (`protected-*-soft-delete-section`).
    - **In the recycle bin:** locked, `encrypted` and `locked`, no pages (`protected-*-recycled-locked`); unlocked by Phill, `encrypted` and its 3 pages (`protected-*-recycled-unlocked`). So `encrypted` survives the recycle bin, locked and unlocked, and AD-21's marker still holds there.
    - **Its permanent delete** (unlocked, after a successful backup) goes through and the section leaves the hierarchy (`protected-*-permanent-delete`). Locally its `.one` was still on disk straight after, as in constraint 10. On the cloud, its `.one` (45.7 KB) then showed in OneDrive's recycle bin (observed by Phill), unlike run 2's unprotected sections (constraint 6). That copy is presumably still encrypted, since it is the section's own file and not a `Publish` output; unverified.
    - **Relocking** (observed by Phill): neither the local nor the cloud protected section relocked right away when he left it after unlocking, and neither did the recycled copy. So "unlocked a moment ago" can be "unlocked now" for a while; AD-21 can't count on a quick relock.
17. **AD-7 / AD-8 / AD-21: a protected section's backup, once opened, is plaintext.**
    - **Proven.** The team lead read the two backups from run 3 byte by byte, read-only, after the probe had opened each with `OpenHierarchy` (`protected-*-open-backup`, `-032127`): `protected-local-vault-1112-032127.one` and `protected-cloud-vault-1112-032127.one` in `%LOCALAPPDATA%\OneNoteMcp-Test\probes\publish\`. Both hold the page titles (`probe vault p1 …`, `probe vault p2 …`) and the page body text (`probe text`) as plain UTF-16. For contrast, MCP Test's encrypted `Ballast Vault.one` has no readable UTF-16 text at all.
    - **Not proven: what `Publish` itself writes.** At `Publish` the files were 19,232 and 19,184 bytes (`protected-*-backup-publish`). At the byte check they were 21,440 and 21,392, so OneNote rewrote them when they were opened. The plaintext may come from `Publish` or from that rewrite.
    - **Deciding probe (pending):** publish an unlocked protected section with `pfOneNote` and check the file's bytes without opening it. A locked section can't be published at all (`0x80042014`, constraint 16).
    - **If `Publish` writes plaintext:** AD-7's PermanentDelete backup of a protected section leaves its content on disk in the clear. It would be readable by any process running as the user and by anything that copies the disk, for as long as the backup folder keeps it, which is what AD-21 exists to stop. Epic 5 would then pick one:
      - encrypt the backup file, for example with DPAPI for the current user, as AD-8 already does for protected journal entries;
      - refuse the backup, and so the permanent delete, of a protected section;
      - require an explicit grant that says the backup is plaintext.
      The backup folder's retention and location (AD-8's 30 days and 2 GB, if backups follow the journal) decide how long the plaintext stays.
    - **Either way:** opening a backup to check or restore it (constraint 7) writes plaintext to disk. Any open-and-compare or restore step must not run on a protected section's backup, or must treat the opened file as sensitive and remove it.
    - Pages are unaffected: AD-7 backs a page up as a page snapshot in the journal, which AD-8 already encrypts for protected sections.
18. **AD-7 (Create, HierarchyWrite, SoftDelete, PermanentDelete) / AD-8: notebooks** (run 4, `-034716`).
    - **Delete: COM can't.**
      - Every notebook-level `DeleteHierarchy` failed `0x80042014` hrObjectDoesNotExist, and the notebook stayed open with its folder unchanged on disk: local permanent delete after the failed soft delete (`nb-permanent-delete-after-soft`), after a backup (`nb-local-permanent-delete`), on a non-empty folder (`nb-permanent-delete-nonempty`), and cloud soft and permanent (`nb-cloud-soft-delete`, `nb-cloud-permanent-delete`).
      - The one local soft delete failed `0x80042010` instead (`nb-soft-delete-local`). The date sent was read before the nickname rename moved the notebook's date (03:47:19 sent, 03:47:20 read after), so no clean local soft delete ran. The cloud soft delete, with a current date, got `0x80042014`.
      - `MCP Test Throwaway Cloud` was still in OneDrive Documents afterwards, not in the recycle bin (observed by Phill).
      - So a notebook delete isn't a COM operation. AD-7's PermanentDelete row ("a section, section group or notebook gets a … backup") and any notebook soft delete must refuse with `NotSupported`, or tell the user to close the notebook and delete it in OneDrive or Explorer. This answers the Deferred row on a whole notebook's delete. `0x80042014` here means "can't delete a notebook", not "no such object", so the server should refuse before COM rather than pass `ObjectNotFound` on.
    - **Backup: only as a package.**
      - `pfOneNote` is refused: `.one` gives `0x80042014`, no extension `0x8004201a`. `pfOneNotePackage` `.onepkg` returns ok, local and cloud (132–133 ms; `nb-local-publish`, `nb-cloud-publish`).
      - The disk check straight after found no `.onepkg` (`kind: absent`), yet `OpenPackage` unpacked it a second later (`open-package`, 554 ms local, 363 ms cloud). The file may land after `Publish` returns, so AD-7's "backup succeeded" must wait for the file to exist, not trust the return.
      - The package opens back as a separate notebook named `<package>-unpacked` (`nb-*-open-backup`). Like the section backups, a pages-scope read of it listed no pages (constraint 7).
      - Section groups, by contrast, fail even as a package (constraint 6).
    - **Rename.** `nickname` is applied (`nb-rename-nickname`). `name` returns ok and is silently ignored: name, nickname, path and folder stay as they were (`nb-rename-name`: before and after equal). A notebook's display name is its `nickname`, as spike 2.2 constraint 9 found from the UI. A rename tool must set `nickname` and read it back.
    - **Create.** `OpenHierarchy` cftNotebook at an empty path creates it (72 ms, `nb-create-local`). At an open notebook's path it returns that notebook's ID (`nb-create-existing-path`: `sameId`, as spike 1 row 9). At a folder holding a text file it creates a notebook there and keeps the file (`nb-create-nonempty`; the folder then holds the `.onetoc2` and the `.txt`). AD-7's "a notebook at an absolute path verified empty" therefore has to be checked by the server; OneNote won't refuse a non-empty folder.

## Results, one row per Matrix question

| Area | Question | Result | Probes |
|---|---|---|---|
| Create | `OpenHierarchy` cftSection and cftFolder by parent ID + relative name: ID, path, focus | Works, local and cloud: a new ID, the path under the parent (cloud groups with a trailing `/`), focus kept. Local 0.1–0.25 s; cloud 3.6–6.4 s. | `create-*-section-group`, `-section`, `-nested-section-group`, `-section-in-nested-group` (`-152447`, `-222514`) |
| Create | An existing name: opens it or errors? | Returns the existing section's ID, no error, no duplicate. | `create-*-section-existing-name` |
| Create | Parent locked | A locked section can't be a parent: `0x80042014`, as for any section. Only sections can be password-protected, so no locked container exists. | `protected-*-create-under-locked` (`-032127`) |
| Create | Parent unknown (deleted) | Not probed (review finding 7): OneNote might create it outside the test notebooks. | `create-*-parent-gone` (`notProbed`) |
| Create | Parent a section (not a container); a page | `0x80042014`, nothing created. | `create-*-under-section`, `-under-page` |
| Create | Extra rows: no `.one`; `..\`; `*?` | No `.one`: `0x80042004`. `..\`: a phantom `..` group and a sync jam (local, `-033107`; not repeated). `*?`: `0x80042006` (`-033107`; not repeated). | `create-*-section-no-extension`, `create-local-relative-name-up`, `create-local-invalid-name` |
| Create | cftNotebook at an empty path, a non-empty path, an existing notebook's path | Empty: created. Non-empty: created there, the file kept. Existing open notebook's path: its ID returned. |  `nb-create-local`, `nb-create-nonempty`, `nb-create-existing-path` |
| HierarchyWrite | `UpdateHierarchy` add Section / SectionGroup without ID | Added; siblings kept. | `update-*-add-section`, `-add-section-group` |
| HierarchyWrite | Rename a section; a section group | Both: ID kept, name and hierarchy path change. Cloud: the section rename synced; the section-group rename never reached OneDrive (Phill). A sibling's name: `0x80042017`. `*?`: stripped silently. | `update-*-rename-section`, `-rename-section-group`, `-rename-existing-name`, `hr-local-update-invalid-name` (`-164036`) |
| HierarchyWrite | Rename a throwaway notebook: `name` or `nickname`? | `nickname`: applied. `name`: ok but silently ignored (nothing changed, folder too). | `nb-rename-name`, `nb-rename-nickname` |
| HierarchyWrite | Reorder sections; reorder pages | Sections: every child in reverse, groups first, returned `0x80042001` (schema order); a partial list with sections only reordered them. Pages: p3, p1, p2 applied. | `update-*-reorder-sections`, `-partial-children`, `-reorder-pages` |
| HierarchyWrite | A partial child list: omitted siblings kept or deleted? | Kept; the listed ones take the order sent. | `update-*-partial-children` |
| HierarchyWrite | A known ID placed under another parent | **Moved**, no error; synced on cloud (Phill). | `update-*-accidental-move` |
| HierarchyWrite | Does the parent's `lastModifiedTime` move after each? | Sometimes: 11 of 21 applied writes moved it, 1 s resolution; a rename never moves the renamed node's own date; a page added moves the section's. | `update-*` (`parentLmtBefore/After`), `update-*-other-writer-lmt` |
| Soft delete | Page | Deleted Pages, new ID, `isInRecycleBin`. Local and cloud. | `soft-delete-*-page` |
| Soft delete | Section | `OneNote_RecycleBin`, same ID, `isInRecycleBin`. Local file still at its old path straight after. | `soft-delete-*-section` |
| Soft delete | Section group | Group gone from the hierarchy; its section appears in the recycle bin on its own. OneDrive: the group's folder and an `Open Notebook.onetoc2` in OneDrive's recycle bin (Phill). | `soft-delete-*-section-group` (`-152447`, `-222514`) |
| Soft delete | Throwaway notebook | **Can't.** Cloud: `0x80042014`, still open. Local: `0x80042010` (stale date read) before reaching that. | `nb-cloud-soft-delete`, `nb-soft-delete-local` (`-034716`) |
| Permanent delete | Page; section | Gone from the hierarchy, local and cloud. Local `.one` still on disk straight after. Cloud: not in OneDrive's recycle bin (Phill). | `permanent-delete-*-page`, `-section`, `backup-*-section-delete` |
| Permanent delete | Section group | Not run: its backup failed, and AD-7 refuses the delete then. A recycled group doesn't exist to delete. | `backup-*-section-group-delete`, `permanent-delete-*-recycled-section-group` (`notProbed`) |
| Permanent delete | An item already in the recycle bin | Page: `0x8004200c`, kept. Section: deleted. | `permanent-delete-*-recycled-page`, `-recycled-section` |
| Permanent delete | Throwaway notebook; cloud throwaway in OneDrive | **Can't.** `0x80042014` every time (three local, one cloud); notebooks stay open and on disk. The cloud throwaway was still in OneDrive Documents, not in its recycle bin (Phill). | `nb-permanent-delete-after-soft`, `nb-local-permanent-delete`, `nb-permanent-delete-nonempty`, `nb-cloud-permanent-delete` (`-034716`) |
| Protected | Setup: password-protecting the section in OneNote's UI | The section and all its pages get new IDs; names and page dates unchanged; `encrypted="true"`; the old `.one` is replaced (OneDrive recycle bin, Phill). Local and cloud. | `protected-*-protected` (`-032127`, `-024414`, `-023431`); cloud reads by the team lead (`-023431`) |
| Protected | Soft delete of a page in an unlocked protected section | **Not answered.** Sent with the page-XML date, refused `0x80042010` local and cloud (the dates rule), so where it would go wasn't reached. Locked: `0x8004201b` `SectionLocked`. | `protected-*-soft-delete-page`, `-soft-delete-page-locked` (`-032127`) |
| Protected | A protected section soft-deleted whole: `encrypted` in the recycle bin, locked and unlocked; its permanent delete | Recycle bin, same ID, `encrypted` kept, locked and unlocked (locked straight after the delete). Permanent delete after a backup: gone. Local and cloud. No quick relock after unlocking (Phill). | `protected-*-soft-delete-section`, `-recycled-locked`, `-recycled-unlocked`, `-permanent-delete` (`-032127`) |
| Protected | Backup and backup-open of a protected section | Unlocked: `.one`, local 19,232 B, cloud 19,184 B, **plaintext after being opened** (titles and text in plain UTF-16, team lead's byte check); `Publish` alone: **pending**. Locked: `0x80042014`. Opened backup: no pages listed, no `encrypted`. | `protected-*-backup-publish`, `-publish-locked`, `-open-backup` (`-032127`) |
| Backup | `Publish` pfOneNote of a section | `.one`, about 0.1 s; local 14,880 B, cloud 15,128 B. A page: 11,136 B. | `backup-*-section-publish`, `backup-*-page` |
| Backup | Of a section group | Fails: `.one` and `.onepkg` `0x80042014`, no extension `0x8004201a`; no file. | `backup-*-section-group-try-1..3` |
| Backup | Of a throwaway notebook | Package only: `.onepkg` ok (local and cloud); `.one` `0x80042014`, no extension `0x8004201a`. The file wasn't on disk straight after; `OpenPackage` unpacked it a second later into an `-unpacked` notebook that listed no pages. | `nb-*-try-1..3`, `nb-*-publish`, `open-package`, `nb-*-open-backup` (`-034716`) |
| Backup | An existing target file | `0x8004201a`. A folder that doesn't exist: a modal, and the call blocks. | `backup-*-existing-file`, `backup-call-timeout` (`-152447`) |
| Backup | Can the backup bring the item back after its permanent delete? | **Not shown.** The file opens, but the hierarchy lists no pages in it; Phill saw only a hidden Open Section. | `backup-*-section-open-backup` |
| Dates | `DeleteHierarchy`: page-XML date, hierarchy date (where they differ), stale, `MinValue`; displayed and not | Hierarchy date accepted; page-XML date refused when it differs, displayed or not; stale refused; `MinValue` skips the check. This is the reverse of `UpdatePageContent` (part 1's Dates row), so the date sent depends on the method (constraint 1). Displayed page with the hierarchy date: not probed. | `dates-*-page-*` (`-021319`, `-152447`, `-164036`, `-033107`) |
| Dates | A section with its hierarchy date, and stale | Hierarchy date accepted; stale refused `0x80042010`. Local and cloud. | `dates-*-section-*` |
| Modal dialog | HRESULT per method, File > Options open | `UpdateHierarchy` `0x80042030`; `OpenHierarchy`, `DeleteHierarchy` (both modes) ok and applied; `Publish` `0x80042006`. Local only. | `modal-options-*` (`-164036`) |
| Modal dialog | The section password prompt | Same as File > Options: `UpdateHierarchy` `0x80042030`; the rest ok; `Publish` `0x80042006`. Local only. | `modal-password-*` (`-032127`) |
| HRESULTs | Each error per method, with its trigger, mapped to AD-9 | See the HRESULT table. | `hr-local-*` (`-164036`), the rows cited there |

## HRESULTs observed

| HRESULT | Name | Trigger | Our code (row) | Probes |
|---|---|---|---|---|
| `0x80042000` | hrMalformedXML | `UpdateHierarchy` with an unclosed root | `InvalidXml` | `hr-local-update-malformed` |
| `0x80042001` | hrInvalidXML | `UpdateHierarchy` with a `one:Bogus` child; with section groups listed before sections | `InvalidXml` | `hr-local-update-unknown-element`, `update-*-reorder-sections` |
| `0x80042004` | hrSectionDoesNotExist | `OpenHierarchy` cftSection, name without `.one` | `ObjectNotFound` | `create-*-section-no-extension`, `r3-no-extension` |
| `0x80042006` | hrFileDoesNotExist | `OpenHierarchy` of a missing `.one`; create with `*?` in the name; `Publish` with File > Options or the password dialog open | `Internal` | `hr-local-open-missing-file`, `create-local-invalid-name` (`-033107`), `modal-options-publish`, `modal-password-publish` (`-032127`) |
| `0x8004200c` | hrPageReadOnly | permanent delete of a page already in the recycle bin | `Internal` | `permanent-delete-*-recycled-page` |
| `0x80042010` | hrLastModifiedDateDidNotMatch | stale date; a page's page-XML date where it differs from the hierarchy's (including pages in a protected section); a section group's or notebook's date that moved after the read | `StaleWrite` | `dates-*`, `protected-*-soft-delete-page` (`-032127`), `soft-delete-local-section-group` (`-033107`), `nb-soft-delete-local` (`-034716`) |
| `0x80042014` | hrObjectDoesNotExist | create under a section or a page; update, soft or permanent delete, or `Publish` of a permanently deleted section; `Publish` of a section group (`.one`, `.onepkg`); create under, or `Publish` of, a locked protected section; any `DeleteHierarchy` of a notebook; `Publish` of a notebook as `.one` | `ObjectNotFound` | `create-*-under-*`, `hr-local-*-gone*`, `backup-*-section-group-try-1`, `-try-3`, `protected-*-create-under-locked`, `protected-*-publish-locked` (`-032127`), `nb-*-delete*`, `nb-*-try-1` (`-034716`) |
| `0x80042017` | hrInvalidName | rename to a sibling's name | `Internal` | `update-*-rename-existing-name` |
| `0x8004201b` | hrSectionEncryptedAndLocked | page soft delete in a locked protected section | `SectionLocked` | `protected-*-soft-delete-page-locked` (`-032127`) |
| `0x8004201a` | hrFileAlreadyExists | `Publish` to an existing file; `Publish` of a section group or a notebook with no extension | `FileExists` | `backup-*-existing-file`, `backup-*-section-group-try-2`, `nb-*-try-2` (`-034716`) |
| `0x80042030` | hrAppInModalUI | `UpdateHierarchy` with File > Options or the password dialog open | `ModalDialogOpen` | `modal-options-update-hierarchy`, `modal-password-update-hierarchy` (`-032127`) |
| none | (blocked call) | "OneNote is syncing changes" dialog; MCP Test or MCP Test Cloud in a sync jam; `Publish` into a missing folder ("We couldn't find that file.") | `Timeout` | `*-call-timeout` (see Failure modes) |

`0xE0000644 9zu13` is the sync error OneNote showed in its UI (observed by Phill); no COM call returned it.

## Failure modes

- **"OneNote is syncing changes" blocks calls** (run 1, `-033107`). After Phill showed two pages in `probe dates s`, the next section-level date row blocked about 3 minutes until Phill pressed Cancel; the next raised it again and froze OneNote's UI (`previous-run-note`, in `-152447` and `-164036`). The delete itself took 104 ms (`dates-local-section-stale`, 03:39:51), so the block was in the setup before it. The probes now ask Phill to show a page outside the probe area before each hierarchy write (`*-shown-check` rows), and time every call out at 60 s. No block recurred with nothing shown.
- **The local `..` jam.** Run `-033107`'s `..\` create made a `..` section group in MCP Test (`create-local-relative-name-up`). MCP Test then showed `0xE0000644 9zu13`. Runs `-150859` and `-151215` both blocked on their first call, the run group's create at MCP Test's root (`run-group-call-timeout`, 60 s each). Phill deleted the `..` group and synced, and the error cleared (observed by Phill); run `-152447` then went through. The `..\` row is skipped in later runs.
- **`Publish` into a missing folder** (`-152447`). OneNote showed a modal "We couldn't find that file." (observed by Phill) and the call blocked past 60 s (`backup-call-timeout`). Skipped in later runs (`backup-*-missing-folder`, `notProbed`).
- **The cloud jam** (`-165147`).
  - MCP Test Cloud stuck "syncing", showing `0xE0000644 9zu13` stamped 11:53 local time, a minute before the run's first create (16:54:07 UTC) (observed by Phill).
  - The section groups reached OneDrive; the sections didn't (observed by Phill).
  - The logged creates took 4.6, 5.7, 1.4 (existing name), 3.5 and 6.6 s. The update step's setup creates then took about 8, 17 and 8 s (gaps between `update-shown-check` rows), and the next timed out at 60 s (`update-call-timeout`).
  - The retry run `-165825` blocked 180 s on its first create (`run-group-call-timeout`).
  - Recovery (observed by Phill): Cancel, then close the notebook (a hung close needed Cancel and a retry; OneNote moved the unsynced sections to Misplaced Sections), then reopen. The notebook's ID changed on reopen, as spike 2.2 constraint 9 found for any reopen.
- **The cloud-jam repro: not reproduced.** One variable per run, with a Sync Now check by Phill after each (`*-check-sync` pauses). Every one synced (observed by Phill):

  | Repro | What | Log |
  |---|---|---|
  | R0 | A section made in OneNote's UI | none (Phill); it shows in `-191703`'s first `run-group-shown-check` as `probe r0 manual` |
  | R1a | COM section with a page | `r1-with-page` (`-191703`, again in `-221810`), 6.5 s and 5.5 s |
  | R1b | COM section, empty | `r1-empty` (`-192800`), 5.6 s |
  | R2 | Same name twice, 2 s apart | `r2-same-name` (`-193209`): 5.7 s then 2.3 s, same ID |
  | R3 | No `.one` | `r3-no-extension` (`-193545`): `0x80042004`, nothing created. Its sync pause went unanswered in the log; the result is Phill's |
  | R4 | Run `-165147`'s whole burst, back to back | `r4-burst` (`-211303`): 10 creates ok (s3 12.8 s), 3 pages |
  | R5 | A create that times out at a 2 s limit | `r5-timeout-call-timeout` (`-221624`, 2.1 s). It landed about 3 s later, and the next create (`-221810`) went through (observed by Phill) |

  Conclusion: the cause is unproven. The likeliest is the notebook's old local cache, cleared by the close and reopen. Live sittings keep a Sync Now check after every cloud step, and report `0xE0000644` at once.

## Not probed

- **Create under a deleted parent** (review finding 7): OneNote might fall back to a location outside the test notebooks.
- **A section group's permanent delete**: its backup failed, so AD-7 refused it (constraint 6).
- **`..\` and `*?` creates on cloud**: not repeated after the local jam.
- **HRESULT and modal probes on cloud**: those steps run on MCP Test only.
- **Other modal dialogs**: only File > Options and the section password prompt.
- **A page soft delete in an unlocked protected section with the hierarchy date** (constraint 16).
- **Reordering section groups among themselves**, and a valid full reorder (sections first, then groups).
- **A displayed page deleted with its hierarchy date.**

## Evidence gaps

- **Restore from a backup.** Opening the backup gave an ID but no pages in a pages-scope read. Whether OneNote loads the pages later, or a different read shows them, wasn't tried; Phill saw only hidden Open Sections and closed nothing (the `*-close-backup` pauses were skipped). Runs 3 and 4 gave the same empty page lists, for protected sections and for unpacked notebook packages.
- **Does a permanent delete leave an OneDrive copy?** Run 2's three unprotected sections never reached OneDrive's recycle bin; run 3's protected section, permanently deleted from OneNote's recycle bin the same way as `probe del b`, did (45.7 KB; observed by Phill). Protected versus not is the visible difference, but one run each can't say why. Whether that copy is still encrypted is unverified.
- **When does a package backup land?** Straight after `Publish` returned ok, the `.onepkg` wasn't on disk (`nb-*-publish`: `absent`), yet it unpacked a second later.
- **A clean local notebook soft delete** never ran: its date was stale (constraint 18).
- **A stale date on a permanent delete.** Spike 1 row 21 found `DeleteHierarchy(id, staleDate, true)` deleted a hidden page anyway. Every stale row here was a soft delete, and each was refused. The two haven't been compared on the same page; the permanent-delete story should probe it before relying on the date for permanent deletes.
- **The protected backups grew after they were opened.** At `Publish` they were 19,232 and 19,184 bytes (`protected-*-backup-publish`); the team lead's later byte check found 21,440 and 21,392. Opening a backup with `OpenHierarchy` lets OneNote rewrite it, so a backup opened to check or restore it is no longer the file that was published.
- **Does `Publish` of an unlocked protected section write plaintext?** Pending: publish one and check its bytes without opening it (constraint 17).
- **`Publish` under a dialog.** `0x80042006` came under both File > Options and the password dialog. It may come from the dialog or from the target section, emptied by the two deletes just before it (`modal-*-delete-hierarchy-*`). No `Publish` of an empty section ran without a dialog.
- **Showing a page moved its page-XML date** in two runs: `dates-local-page-shown-*` (`-033107`: 03:35:18 and 03:36:16, hierarchy still 03:31:23) and `dates-cloud-page-shown-*` (`-021319`). It didn't in `-164036` (both dates stayed 16:40:39). Phill was asked not to type. If simply viewing a page moves its date, page writes (AD-17) get spurious `StaleWrite`s; the cause wasn't isolated.
- **Local files after a delete.** Read once, straight after the call. Whether OneNote later removes a permanently deleted section's `.one`, or moves a soft-deleted one, wasn't checked.
- **Parent dates.** Most "unchanged" rows fall in the same second as the write, so they don't show that the date didn't move.
- **OneDrive results** rest on Phill's checks in OneNote for the web and the OneDrive recycle bin: the group rename, the reorder, the move, the recycle bin contents. The run's `onedrive-check-cloud` row records `phillChecked: false` because the pause was skipped; the answers came through the team lead. OneNote for the web also added an "Untitled Page" to the empty s3 when Phill opened it (not ours).
- **The cloud jam's cause** (Failure modes).

### Where the logs and the observed list differ

- **"Run 1 passed, 83 rows" (`-164036`).** That run ran only the dates, backup, HRESULTs and modal steps. Run 1's create, hierarchy-write and delete rows come from `-152447`.
- **The `*`/`?` rename.** The observed list puts it in run 2. The only such row is local (`hr-local-update-invalid-name`, `-164036`); the cloud scenario has no HRESULT step.
- **"Creates slowed 5, 8, 16, 60 s"** (run `-165147`). The logged create times are 4.6, 5.7, 1.4, 3.5 and 6.6 s; the slower ones (about 8, 17, 8 s) are setup creates inferred from the gaps between rows, then the 60 s timeout.
- **R3 synced.** Its log has no answer: the sync pause timed out after 1800 s (`-193545`). The result is Phill's.

## The spine's Deferred rows

| Deferred row | Answer |
|---|---|
| `DeleteHierarchy` on a whole notebook; permanent delete of a section group | Notebook: COM can't, soft or permanent, local or cloud (`0x80042014`); refuse it (constraint 18). Section group: can't be backed up, so AD-7 refuses it as written; entry 2 decides between per-section backups and a refusal (constraint 6) |
| `DeleteHierarchy(deletePermanently=false)` on a page in a protected section | **Not answered by run 3.** Its only attempt sent the page-XML date and was refused `0x80042010`; locked, `0x8004201b`. Needs one probe with the hierarchy date (constraint 16) |
| `Publish` to OneNote format as the backup route | Works for pages and sections, fails for section groups; restoring from it is unproven; a protected section's backup is plaintext once opened, and whether `Publish` alone writes plaintext is pending (constraints 6, 7, 17). Notebooks: package (`.onepkg`) only (constraint 18) |
| Which `lastModifiedTime` `DeleteHierarchy` accepts | The hierarchy's, for pages and sections; the reverse of `UpdatePageContent`, so AD-17 names the date per method (constraint 1) |
| A protected section deleted whole | Soft: recycle bin, same ID, `encrypted` kept locked and unlocked, locked straight after the delete. Permanent (after a backup): gone. Its IDs had already changed when it was protected (constraints 15, 16) |

## Deviations from the plan

- **Fixes after run `-033107`**, all in `tests/` (plan Implementation Notes): section dates first, a shown-check before every hierarchy write, a 60 s call limit, and a `previous-run-note` row.
- **Skipped after the jams:** `..\` and `*?` creates (local jam), and `Publish` into a missing folder (modal). Their later rows say `notProbed`.
- **A Sync Now check after each cloud step** (`*-check-sync` pauses), added after the cloud jam at Phill's request, and the jam repro (`CloudJamRepro.cs`), which the plan didn't have.
- **Unanswered pauses:** `dates-local-show-stale` (`-152447`), `soft-delete-check-sync` and `dates-cloud-make-differ` (`-222514`), and `r3-check-sync` (`-193545`), each after 1800 s. Run 1's dates were re-run in `-164036` and run 2's in `-021319`.
- **Run 3 took three tries.** Run `-023431` couldn't find either protected section after the protect, because its ID had changed (constraint 15); the probe was fixed to find it by name. Run `-024414` stopped after `protected-local-locked-state`: the PC ran low on memory and Claude Code's process was reaped, not a probe failure. Run `-032127` ran both passes to the end.
- **The `onedrive-check` pause** in `-222514` was skipped. Phill's OneDrive answers came through the team lead and are marked "observed by Phill" here.
