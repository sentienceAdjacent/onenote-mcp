# Read-only probes, 2026-11-03

These are evidence for the architecture update of 2026-11-03 (AD-4 Deferred, AD-17). Both probes make read-only COM calls against the user's running OneNote: `GetHierarchy` and `GetPageContent` at `piBasic` level. They print names and timestamps only, never page content. They reuse the hand-declared interop from `src/OneNoteMcp.Com/Interop`.

Machine: Windows 11, Microsoft 365 OneNote desktop (type library `{0EA692EE-BB50-4E3C-AEF0-356D91732725}` 1.1), with 205 live pages across 4 cloud notebooks.

## 1. `xs2013` vs `xs2026` (schema probe)

The type library defines `XMLSchema`: `xs2007=0 xs2010=1 xs2013=2 xs2026=3 xsCurrent=3`. Microsoft Learn doesn't document `xs2026`.

The probe compared the full hierarchy, plus 25 pages sampled evenly, read in both schemas:

```
== hierarchy pages: ns2013=http://schemas.microsoft.com/office/onenote/2013/onenote ns2026=http://schemas.microsoft.com/office/onenote/2026/onenote len 56870 vs 56870
  elements: only2013=[] only2026=[]
  attributes: only2013=[] only2026=[]
  identical apart from namespace: True
== pages: sampled 25, failed 0, ns2026=http://schemas.microsoft.com/office/onenote/2026/onenote, identical apart from namespace: 0
  only2013=[OE@author, OE@authorInitials, OE@authorResolutionID, OE@lastModifiedBy, OE@lastModifiedByInitials, OE@lastModifiedByResolutionID, Outline@author, Outline@authorInitials, Outline@lastModifiedBy, Outline@lastModifiedByInitials, Cell@lastModifiedByInitials]
  only2026=[AuthorDef, AuthorDef@index, AuthorDef@name, AuthorDef@initials, AuthorDef@resolutionID, OE@authorIndex, OE@lastModifiedByIndex, Outline@authorIndex, Outline@lastModifiedByIndex, Cell@colSpan, Cell@rowSpan]
```

The schema probe's source was not kept; it was overwritten by probe 2. It compared the sets of element and attribute local names, and the documents with the namespace replaced.

## 2. Page vs hierarchy `lastModifiedTime` (`LastModifiedProbe.cs`)

The probe read 12 pages sampled evenly. For each, it compared the hierarchy page's `lastModifiedTime` with the root `lastModifiedTime` from `GetPageContent`:

```
hier=2026-10-17 11:13:12Z page=2026-10-17 11:13:12Z -> same
hier=2022-11-11 15:41:35Z page=2022-11-11 15:41:35Z -> same
hier=2024-08-07 19:08:35Z page=2024-08-30 20:10:26Z -> other
hier=2024-08-30 19:53:33Z page=2025-07-27 18:05:31Z -> other
hier=2025-02-28 14:25:13Z page=2025-02-28 14:25:13Z -> same
hier=2025-03-28 17:51:34Z page=2025-03-28 17:51:34Z -> same
hier=2024-08-20 18:55:27Z page=2024-08-20 18:55:27Z -> same
hier=2021-10-11 16:35:37Z page=2021-10-11 16:35:37Z -> same
hier=2023-03-28 21:17:37Z page=2023-03-28 21:17:37Z -> same
hier=2019-05-06 18:14:00Z page=2019-05-06 18:14:00Z -> same
hier=2020-10-30 15:49:04Z page=2020-10-30 15:49:04Z -> same
hier=2019-03-19 13:06:49Z page=2019-03-19 13:06:49Z -> same
same=10 readTime=0 other=2 of 205 pages
```

The two values can differ. The page value was never the read time, so OneMore's reported defect (PowerShell 5, on another build) didn't reproduce. Which value is correct when they differ is not established here; see the AD-17 Deferred row.

Run: `dotnet run --project spikes/probes-2026-11-03` with OneNote running.
