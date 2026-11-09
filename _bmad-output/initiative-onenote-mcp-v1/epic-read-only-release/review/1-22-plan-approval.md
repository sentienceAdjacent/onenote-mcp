# Bug 1.22: approve the plan

The plan is the second file. It's the "full" route: about 35 lines of source and 150 of tests, built by an implementation agent.

Two things to know:
- **Fail-closed side effect:** once a grant lapses, a stored search result that names a granted section with no hits in it refuses *every* part, not only that section's parts. The ticket's criterion 1 asks for exactly that, and it costs only re-running the search.
- **Spine:** AD-6's guard sentence gains the purge. That edit gets the Fable review before the PR, and you pick which findings to fix, as usual.

:::question
**The plan**
- Approve and continue: build it in this session
- Approve and stop: leave it ready-for-dev
- Review plan: changes needed (add notes)

Recommended: Approve and continue: build it in this session
:::
