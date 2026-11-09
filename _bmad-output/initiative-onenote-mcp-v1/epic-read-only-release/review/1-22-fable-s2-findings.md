# Bug 1.22: Fable review of the S2 spine edits

This short Fable review covers the spine text added after the first one. I checked all 6 findings against the code, and all 6 hold.

- **1 (medium).** The guard matches only `OneNoteMcp` or `OneNoteMcp.exe` followed by `grant` or `uninstall`. On a machine with the source, these get past it: `dotnet run --project src/OneNoteMcp.Server -- uninstall --purge --yes`, and `dotnet …\OneNoteMcp.dll uninstall …`. `--yes` lets purge run without asking. The grant command still refuses to run without a terminal, but purge has no such second line of defence. **This affects developer machines only:** plugin users have only the exe. Fix: widen both patterns to cover `.dll` and the `dotnet run … --` form, with tests.
- **2 (low).** AD-6 names only the settings file's own folder, but the code also blocks the default `%APPDATA%\OneNoteMcp` when the settings path is overridden. Fix: name both.
- **3 (low).** AD-6 Limits says "the README says so", but the README never says the guard works in Claude Code only. Fix: add that to the README's security bullet.
- **4 (low).** AD-19 says purge "takes `--yes` where it can't ask", but `--yes` also skips the question in a terminal. Fix: use the README's wording.
- **5 (low).** Memlog entry 154 reads as if it closes the Fable review, but it records the build's review fixes. Fix: reword 154, and add an entry for this review.
- **6 (low).** Wording: AD-6 says "editing `settings.json`", but the hook also denies creating, moving and deleting, so "changing" fits. AD-20 should say "isn't in the result".

:::question-multi
**Which of these should I fix?**
- 1. Guard: cover .dll and dotnet run forms, with tests
- 2. AD-6: name both blocked folders
- 3. README: say the guard is Claude Code only
- 4. AD-19: the README's --yes wording
- 5. Memlog: reword 154 and add this review's entry
- 6. Wording: "changing" and "isn't in the result"

Recommended: all of them
:::
