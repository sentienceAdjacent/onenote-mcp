# Steps that need you: 1.20 wrap-up, then 1.13

Work top to bottom. Answer the questions at the end and approve once you're done (or leave notes where something went wrong).

## 1. Recycle-bin check (about 5 minutes)

Does a page deleted from a password-protected section land in the recycle bin unprotected?

1. In OneNote, open **MCP Test > Ballast Vault**. Unlock it if it's locked (password `1234`).
2. Add a new page titled `zz delete test` with one word of text.
3. Delete that page: right-click it in the page list, then **Delete**.
4. Leave the Vault unlocked.

Then answer question A below. I'll read MCP Test's hierarchy and report where the page went and whether its section is marked protected. If it leaks, I'll draft an AD-21 amendment for the Fable review.

## 2. Live check of the new 5-minute grant (about 5 minutes)

Start a fresh Claude Code session against the rebuilt server:

```
claude --mcp-config "C:\Users\phill\AppData\Local\Temp\claude\2e954d91aa84c703\29be74cd-4c1d-4173-ac4d-1d869f5ff611\scratchpad\headless\mcp120.json" --strict-mcp-config
```

**Prompt 1**
> Using the on120 OneNote tools, read the first page of the Ballast Vault section in MCP Test. Tell me only its title.

Expected:
- **One** dialog, not two.
- The option reads **"This section, for 5 minutes"**.
- The **Section name field is empty**: no pre-fill.

Type `Ballast Vault`, pick that option and Accept. The title comes back.

**Prompt 2**, within 5 minutes:
> Read the second Ballast Vault page's title, then call list_grants and show me what it returns.

Expected: no dialog. `list_grants` shows a `once` grant with `status: active` and an `expiresAt` about 5 minutes after you accepted.

**Prompt 3**, after 5 minutes have passed:
> Read the first Ballast Vault page's title again.

Expected: the dialog appears again, and the name field is **empty** again. That's the pre-fill fix working. **Decline**: you get `ReadNotGranted`.

Answer question B below.

## 3. Approve the 1.13 plan

The plan is the second file in this review. Your earlier answers are written into it: purge asks you to type `purge` (or takes `--yes`), the clean run uses a VMware Windows 11 VM with a snapshot, and I cut `v0.7.0-rc.2` on your go. Answer question C.

## 4. Merge PR #14

https://github.com/sentienceAdjacent/onenote-mcp/pull/14

Answer question D. "Merge" lets me run the merge; then you mark 1.20 done.

## Not needed from you now

The VM for 1.13's clean install. You'll need it only after 1.13 is built and released.

---

:::question
**A. Recycle-bin check: did you add and delete `zz delete test` in the Ballast Vault?**
- Done: read it now
- Couldn't do it (add a note)
:::

:::question
**B. The 5-minute grant live check**
- All as expected: one dialog, empty name field, no dialog on the second page, `list_grants` showed the grant, and asked again after 5 minutes
- Something differed (add a note)
:::

:::question
**C. The 1.13 plan**
- Approve and continue: start the build after 1.20 merges
- Changes needed (note them on the plan)
:::

:::question
**D. PR #14 (1.20)**
- Merge
- Not yet (add a note)

Recommended: Merge, if A and B came out clean
:::
