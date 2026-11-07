# Story 1.19: Fable review of the spine amendment

The 1.19 spine amendment adds `search{cacheMaxMB}` to AD-10 and the page-text cache rule to AD-12. Fable found 10 problems. I checked every one against the spine and the code, and all 10 hold. The code review independently raised #2 and #4 too. #7, a test, is already done.

:::question
**Which findings should be applied to the spine and docs before 1.19 goes to built?**

- [x] **#1 (high) – The staleness gap is wider than "another add-in".** The server's own future write tools (epic 2), journal restore, and a second OneNote MCP session are also COM edits, so they leave this process's cache stale too. No AD tells the write pipeline to clear the cache. *Fix:* AD-12 names all COM edits, these included. AD-7 gets "a write drops the written page's cache entry before it returns". The README and tool description say "another program (another add-in, or a second OneNote MCP session)".
- [x] **#2 (medium) – "Same results as without the cache" overstates it.** (a) A cache hit can't detect a page deleted after OneNote's search, which a cold search would list as `notFound`. (b) `lastModifiedTime` is whole-second, so an edit in OneNote within the same second as a cached read is missed. The README says such edits are "always seen", which is too strong. *Fix:* AD-12 says results match a read for any page whose time changed with its content, and differ only through the known gap and `notFound`. Drop "always" from the README.
- [x] **#3 (medium) – Protected sections and the cache.** When 1.20 lands, protected candidates must be dropped before the cache lookup, and their pages should never be cached, so a one-time grant can't leave text behind. *Fix:* add exactly that to AD-12.
- [x] **#4 (medium) – AD-10's schemaVersion bullet doesn't list `search{}`.** *Fix:* "`results{}`, `search{}` and `protectedSections{}` included".
- [x] **#5 (medium) – "The find_pages text rule" is cited but never written down in AD-12.** It lives only in a code comment. *Fix:* add the rule as an AD-12 bullet (`PageText.Of` is its one implementation) and point AD-12's cache bullet and AD-20's label rule at it.
- [x] **#6 (medium) – A text bigger than the whole cap isn't cached.** AD-20's store does the opposite and keeps an oversized result alone. *Fix:* one clause in AD-12 saying so.
- [x] **#8 (low) – The gap states more than spike 1 measured.** The spike saw the time not advance within 8 s; whether it catches up later is open. *Fix:* reword to match.
- [x] **#9 (low) – AD-12 is silent on two things the code does.** Settings are read once per call and a lowered cap trims first, and the log carries hits, misses, evictions and entries. *Fix:* say both.
- [x] **#10 (low) – The cache bullet is one 120-word line.** *Fix:* split it into sub-bullets like AD-20's, and align the eviction wording.

Recommended: apply all nine. Then the spine stays final and 1.19 goes to built.
:::
