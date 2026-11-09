# Bug 1.22: findings to choose from

The build passes:
- 0 warnings; 1256 unit tests pass; integration tests 87 passed and 7 skipped, as before.
- The stress harness on MCP Test shows 0 mismatches, and the protected section is still withheld.
- 19 of the new tests fail on the pre-fix code. The rest cover behaviour that was already right.

Three reviews ran. I checked every finding against the source:
- **Fable** (the AD-6 spine edit): 5 findings, all hold.
- **The code review lens:** 8 findings. 5 hold and 3 are dropped.
- **The security review:** it found **no remaining path to protected data** through `read_result`, in any mode, from any tool, under any grant lifetime. It raised 2 issues.

**Dropped:**
- "Every new test fails on main": the plan's wording. Tests for the still-valid cases can't fail there.
- "The no-index test duplicates the absent-section rule": true, but the test still pins the behaviour the ticket asks for.
- "Fable review pending": it has run now.

## Spine and docs

- **F1 (medium).** AD-6's Limits never say the plugin guard is best effort, but the code comment and the README both cite AD-6 Limits for it. Fix: add one sentence to Limits. The guard is best effort and works in Claude Code only: a disguised shell command gets past it, and nothing stops another program editing `settings.json`.
- **Q1 (medium).** AD-9's `ReadNotGranted` row still says a lapsed `read_result` refusal is retryable, and AD-20's re-check bullet lacks the new fail-closed rule. Fix: amend both rows to match the code, including the per-exception retryable override.
- **Q2 (medium).** The guide skill's `ReadNotGranted` row still tells the model to "let them retry" for a lapsed `read_result`. Fix: for `read_result`, say to repeat the original call once the user grants again.
- **F2 (low).** AD-19's Uninstall rule doesn't say that only the user runs the purge, that the plugin denies it, or that it asks the user to type `purge` (or takes `--yes`). Fix: add one sentence.
- **F3 (low).** The memlog entry says only "purge"; the edit also names the grant-command block. Add a "Fable review applied" event, as in 1.14.
- **F4 (low).** Make the spine say "and every grant in it", as the other docs do.
- **F5 (low).** README:45, the "never …" sentence, should also name the grant and uninstall commands, as the skill does.
- **Q4 (low).** README agent step 6 says "the plugin blocks it", but manual and source installs have no hook. It also points at the uninstall command for the plugin's path only. Fix: "the plugin blocks it", plus "use the exe path you installed" for the other routes. The build agent also rewrote step 6, which was outside the plan: it used to tell agents to run the purge with `--yes` themselves.

## Code

- **S1 (low).** A lapsed refusal is marked `retryable: false` for every lifetime. After an "until revoked" grant is revoked and then granted again, the same `read_result` does succeed. Fix: false only for `hours` grants, plus a test for the re-grant case.
- **Q3 (low).** When the check can't place the section, the refusal message still says "this part is in protected section X". Fix: reword it to say the part may hold that section's pages.

:::question-multi
**Which of these should I fix in 1.22?**
- F1. AD-6 Limits: the guard is best effort
- Q1. AD-9 ReadNotGranted row and AD-20 re-check bullet
- Q2. Skill ReadNotGranted row: repeat the original call
- F2. AD-19: only the user purges; the confirm and --yes rule
- F3. Memlog wording, plus a review-applied event
- F4. "and every grant in it"
- F5. README:45, the never sentence
- Q4. README step 6 wording for non-plugin installs
- S1. retryable false only for hours grants, plus a test
- Q3. Refusal message wording when the section can't be placed

Recommended: all of them
:::

## One older weakness, outside this bug

**S2. The model can give itself a grant.**
1. It writes `%APPDATA%\OneNoteMcp\g.json` with the Write tool. That's allowed, because the file isn't named `settings.json`.
2. It runs `cd "$APPDATA/OneNoteMcp" && mv g.json settings.json`. That's allowed too, because the command never writes the folder and the file name as one path.
3. The next read finds a stored grant.

This has been true since story 1.20. Section paths are visible in any broad `get_hierarchy`, so the model has what it needs. AD-6 calls the guard best effort against *disguised* commands, and this one isn't disguised.

A fix would block the whole OneNoteMcp app-data folder, not only `settings.json`:
- deny any Write or Edit to a file under it;
- deny any shell command that names that folder in any form, including `$APPDATA/OneNoteMcp` and `%APPDATA%\OneNoteMcp`.

That is about 15 lines and its tests.

:::question
**S2: what should happen with the model granting itself through a renamed file?**
- (a) Fix it in 1.22: block the OneNoteMcp app-data folder, not only settings.json
- (b) A separate bug ticket after rc.2
- (c) Record it in deferred-work as a known limit of a best-effort guard

Recommended: (a) Fix it in 1.22: block the OneNoteMcp app-data folder, not only settings.json
:::
