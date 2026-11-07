# Story 1.19: how to make find_pages cheaper

**Why it's slow (measured, spike 1.15):** find_pages reads every candidate page whose name doesn't already hold all the search terms. That's one COM call per page, about 26 ms each, and they can't run in parallel because one thread owns every COM call. At about 190 pages a search takes 2–5 s, against about 50 ms for OneNote's raw search.

**The ticket's constraint:** the result set must stay exactly what it is today.

**The catch:** no option makes the *first* search faster without changing results. OneNote's fast indexed search lags new pages and matches by its own rules, so it can't replace our reads. Only a cache keeps results identical, and it only speeds up later searches.

:::question
**Which mechanism should 1.19 use?**

- **(a) Page-text cache keyed by page ID + `lastModifiedTime`.** Kept in memory, bounded, one per server process, never on disk. Later searches read only pages that changed since they were cached. It's fast for every search after the first.
  *Risk:* spike 1 measured that, for a page that isn't open in OneNote, `lastModifiedTime` doesn't advance after a COM edit. So if another add-in (such as OneMore) edits a page through COM, search could match it against old text until the page changes again. Edits you make in OneNote itself do update it.
- **(d) Cache (a) plus a notebook-level check.** Drop the cache when the notebook's own `lastModifiedTime` has moved. That costs one extra hierarchy call per search. It narrows (a)'s risk but doesn't remove it, since the notebook time comes from the same source.
- **(c) Time budget.** Stop reading after N seconds and return what's decided, marked possibly incomplete, with the unread pages listed. It's fast every time, but large notebooks would return fewer pages than today. That breaks the ticket's identical-results rule and your "no lost functionality" rule.
- **(b) Indexed-first.** It doesn't reduce reads while results must stay identical, so it gains nothing. It's listed only to rule it out.

Recommended: **(a)**. It's the only option that keeps results identical and actually cuts the work. The COM-edit staleness is narrow, so it gets documented in the README and the tool description, and the cache is easy to turn off with a setting (`search.cacheMaxMB: 0`).
:::
