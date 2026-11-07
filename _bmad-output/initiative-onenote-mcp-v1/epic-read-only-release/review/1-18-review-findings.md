# Story 1.18 review findings

The quick review found 7 problems. I checked each one against the code. Six are real. The seventh is false: it says acceptance criteria 1 and 2 have no evidence, but both ran. The stress harness reported 0 mismatches and a largest text block of exactly 100,000 characters. A headless Claude Code session reached the page's last text in 6 tool calls. I'll record that evidence in the plan.

Untick any you don't want fixed. All six are small changes for the implementation agent.

:::question
**Which findings should be fixed before 1.18 stops at built?**

- [x] **F1 – The nine read tools' descriptions are out of date (medium).** They still say a successful result always has `xml` and full `elementHashes`. Now any of them can return an index (`meta.result` + `index`) instead, and the text block is a header line plus the raw body. A model that hasn't loaded the guide skill learns the wrong shape. *Fix:* add one sentence on the oversized index and `read_result` to each read tool's description.
- [x] **F2 – An oversized result that can't be indexed is lost (medium).** If the span scanner fails on the XML, the whole result becomes an `InvalidXml` error. The same body under the limit would have come back whole. That breaks AD-20's no-loss rule. *Fix:* fall back to storing it as plain text, so offset reads of `/` still reach every character, with a warning.
- [x] **F3 – `maxStoreMB` undercounts memory (medium).** Only the body and the piBasic XML are counted. Each entry's index tree (nodes, labels, ref map) isn't, and on a page of many small elements that can double the real memory or more. *Fix:* count an estimate of the tree too: so much per node plus the label characters.
- [x] **F4 – A run index's `chars` doesn't match its `ref` (medium).** `index.ref` is the parent but `chars` is the run's own length. The guide tells the model to offset-read against `ref` up to `chars`, so on a run index it reads the wrong range. *Fix:* the guide and description say offset reads take a single element's ref; a run's members are read by their own refs. Keep `run` on the index so it's clear which kind it is.
- [x] **F5 – "text" is the wrong word for offsets (low).** The descriptions say offsets count the element's "text", but they count its raw span, tags included. *Fix:* say "span (markup included)".
- [x] **F6 – Settings are read from disk several times per call (medium).** A size check can then use a different `inlineMaxChars` than the response was sized with. It also adds file reads to every call. *Fix:* read `results{}` once per call and pass that value along.

Recommended: fix all six.
:::
