# Story 1.20: plan size

The 1.20 plan is about 2,500 tokens (guide: 1,600). AD-21 needs AD-6's grant machinery, and none of it exists yet:
- the protection check in all nine read tools and `read_result`;
- the settings writer, with its mutex and atomic replace;
- the elicitation dialog;
- the `grant` command-line tool;
- `list_grants`.

:::question
**Build 1.20 as one story, or split it?**

- **One story.** One PR delivers the whole AD-21 behaviour. The risk is a long implementer context, as in 1.18, which went fine.
- **Split.** 1.20a: the guards in every tool, the `once`/`hours`/`untilRevoked` dialog, and the settings writer (grants via the dialog only). 1.20b: the `grant` / `--revoke` command-line tool and `list_grants`. After 1.20a, protected sections are already safe, and the dialog alone can grant access.

Recommended: **One story**. The pieces share the access check and the settings writer, so splitting saves little, and the ticket's verify needs revoke, which 1.20b would hold.
:::
