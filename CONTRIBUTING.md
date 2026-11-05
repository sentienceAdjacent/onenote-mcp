# Contributing

Thanks for your interest. Issues and pull requests are welcome. Every change goes through the maintainer, and nothing is merged without review.

## Before you start

- For anything bigger than a small fix, open an issue first so we can agree on the approach.
- Build and test with `dotnet build OneNoteMcp.slnx -warnaserror` and `dotnet test tests/OneNoteMcp.Tests`. CI runs the same.
- Tests that need a real OneNote are opt-in (`ONENOTE_MCP_INTEGRATION=1`). Run them only against a throwaway notebook, never your own notes.
- Never commit note content, notebook names, or anything else from your own OneNote.

## Contributor agreement

This project is licensed under the PolyForm Noncommercial License 1.0.0, with an additional permission for internal use (see [LICENSE.md](LICENSE.md)). The licensor also offers separate commercial licenses. To make that possible, contributions need to come with the rights below.

By submitting a contribution (a pull request, patch, or any other material) you agree that:

1. **It's yours to give.** You wrote it, or you have the right to submit it under these terms. If your employer has rights to your work, you have their permission.
2. **License to the licensor.** You grant the licensor (Phill, sentienceAdjacent) and anyone who receives the software from the licensor a perpetual, worldwide, non-exclusive, royalty-free, irrevocable license to use, copy, modify, distribute, sublicense and relicense your contribution, under any terms, including commercial terms.
3. **Patents.** You grant the same parties a perpetual, worldwide, royalty-free, irrevocable license under any of your patents that your contribution infringes, to make, use, sell and distribute it as part of this project.
4. **You keep your copyright.** This agreement doesn't transfer ownership. You can use your own contribution however you like.
5. **No warranty.** Your contribution is provided as is.

Say so in the pull request by including this line in its description:

> I have read CONTRIBUTING.md and agree to the contributor agreement.

A pull request without that line can't be merged.

## Contact

- Bugs and features: GitHub issues.
- Commercial licensing: license@sentienceadjacent.com.
