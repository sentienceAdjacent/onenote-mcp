# 1.14 refactor sweep: your answers, then the plan

The plan is the second file in this review. Two read-only inventories went through `deferred-work.md`, all 20 epic 1 plans and the spine's Deferred table, and I checked their claims against the code.

**What the sweep does regardless of your answers:**
- Adds a test that logging never reaches stdout (stdout carries MCP JSON-RPC only).
- Adds a test that runs real `tools/call` requests through the MCP SDK, so argument binding is covered.
- Stops attaching raw `XmlException`s to errors.
- Closes the items that are already fixed (4 in `deferred-work.md`, plus 2 more from the plans).
- Moves 2 epic 2 notes into `deferred-work.md`.
- Updates 3 spine Deferred rows to what epic 1 found. That means a Fable review before the PR.

**Size:** the plan is about 1,700 tokens, a little over the 1,600 guideline. Your answers replace the Open Questions section, which brings it back under.

---

:::question
**Q1. Two code-shape items from story 1.3, deferred to this sweep**
`ToolRunner.RunAsync` takes the use case and its call separately, so a tool could pair the wrong ones. Separately, `?? string.Empty` is repeated in tools, use cases and the gateway.
- (a) Close both as won't-fix: each tool has exactly one use case, and the extra null guard is harmless
- (b) Fix both: a typed RunAsync and one coalesce point, touching all 12 tools

Recommended: (a) Close both as won't-fix: each tool has exactly one use case, and the extra null guard is harmless
:::

:::question
**Q2. A cancelled call is reported as `Internal`**
When the client cancels, MCP says the client stops waiting and ignores any reply, so the code is rarely seen.
- (a) Keep Internal and close the item
- (b) Add a Cancelled error code (a contract change; bumps contractVersion)

Recommended: (a) Keep Internal and close the item
:::

:::question
**Q3. `0x80080005` always maps to `IntegrityMismatch`**
The server now checks integrity levels *before* it calls COM, so if this code still comes back, the levels matched. The current message then tells you to fix elevation, which isn't the problem.
- (a) Map it to ComUnavailable (retryable) when the check matched, and amend AD-9's row
- (b) Keep it as is and close the item

Recommended: (a) Map it to ComUnavailable (retryable) when the check matched, and amend AD-9's row
:::

:::question
**Q4. Three items about the release repo and GitHub, not this code**
The validator version pin in the plugins repo can drift; branch protection on that repo would break `sync.yml`; a release the catalog pins could be deleted.
- (a) Close the branch-protection item (AD-15 already states it); add a backlog entry for the plugins repo's pin; you add a GitHub tag-protection rule for v* tags
- (b) Close all three as accepted limits

Recommended: (a) Close the branch-protection item (AD-15 already states it); add a backlog entry for the plugins repo's pin; you add a GitHub tag-protection rule for v* tags
:::

:::question
**Q5. Unknowns that need you at OneNote, plus one cache limit**
Unknowns: symbols in search queries, how reliable FindMeta is, and the guide skill's claims about lists and image alt text (taken from the schema, not our fixtures). The cache limit: the search cache churns when a search has more candidate pages than the cache holds.
- (a) Move the unknowns to a probe entry for epic 2, and add one README line about the cache limit
- (b) Accept them as unknowns and close

Recommended: (a) Move the unknowns to a probe entry for epic 2, and add one README line about the cache limit
:::

:::question
**The plan**
- Approve and continue: build it in this session
- Approve and stop: leave it ready-for-dev
- Review plan: changes needed (add notes)

Recommended: Approve and continue: build it in this session
:::
