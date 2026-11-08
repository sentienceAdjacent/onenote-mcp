# Decisions waiting: 1.20 follow-up and the 1.13 plan

1.20 is built: PR #14, commits 23333e0 and 1711ad1. 1.13's plan is a draft (file attached). It waits on the three 1.13 questions below.

## 1.20: deleted pages from a protected section

On MCP Test, the recycle bin's "Deleted Pages" section is **not** marked `encrypted`. If a page deleted from the Ballast Vault lands there, AD-21 treats it as unprotected:
- a broad `get_hierarchy` would show its name;
- `get_page_content` would return it.

Checking this needs a hands-on delete in the unlocked Vault (MCP Test only).

:::question
**What should happen to recycle-bin pages until the hands-on check is done?**
Interim choice only. Whatever the check finds goes into an AD-21 amendment, which gets the Fable review.
- (a) Leave as is. Run the hands-on check next (you unlock the Vault, delete one test page, and I read where it lands and how it's marked), then amend AD-21 if it leaks.
- (b) Fail closed now: in a notebook that has any protected section, treat recycle-bin pages as protected. They are withheld from broad reads and need a grant to read. Some recycle-bin reads are lost until the check is done.
- (c) Ship 1.20 as is, and track the question in deferred work only.

Recommended: (a). The check takes five minutes with you at the machine, and (b) costs recycle-bin reads in every notebook that has a password-protected section.
:::

## 1.13: open questions in the plan

:::question
**Confirmation for `uninstall --purge`** (it deletes your read grants)
- (a) When run from a terminal, it asks you to type `purge`. Otherwise it needs `--yes`, so a script can run it. The plugin hook doesn't block it, because you may ask Claude to uninstall.
- (b) No confirmation.
- (c) Terminal only, like `grant`, and the hook blocks Claude from running it.

Recommended: (a)
:::

:::question
**Clean environment for the end-to-end install check**
- (a) A new local Windows user on this PC. OneNote is installed for all users; sign in to M365 in that account.
- (b) A VM or another PC.
- (c) Windows Sandbox. It has no OneNote, so it only proves the install up to "OneNote is not running".

Recommended: (a)
:::

:::question
**The release the clean run installs from** (it needs 1.20 and 1.13 merged, then a release)
- (a) After you merge, I push tag `v0.7.0-rc.2` and run the marketplace sync, on your go.
- (b) You cut the release yourself.

Recommended: (a)
:::
