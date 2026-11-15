---
title: 'Stop sending z in the Outline positions we write'
type: 'bugfix'
ticket: '2'
created: '2026-11-15'
status: 'draft'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `update_page_content` passes the caller's XML to `UpdatePageContent` unchanged. When it holds an Outline's `one:Position` with its `z`, even the value just read, OneNote re-stacks the page's Outlines: the sent one goes to `z="0"`, the others move up, and an Outline the caller didn't send changes hash, so an older basis for it later fails with `StaleWrite` (sitting C3; Z probe 57 at the 2026-11-15 sitting). Resent without `z`, nothing moved.

**Approach:** After every check has passed on the caller's XML, the write cuts the `z` attribute out of each `one:Position` and sends that text to COM. The cut works on the original string (AD-4: no re-serialising), so everything else is byte-identical.

## Boundaries & Constraints

**Always:** Every check (parse, schema, read grant, write grant, file paths, HTML, step 4, basis) runs on the caller's XML as today, so an invalid `z` is still `InvalidXml` before COM. Only an unprefixed `z` on a `one:Position` in the 2013 namespace (any prefix) is removed, with the whitespace before it; `x`, `y` and every other byte stay.

**Never:** No change to the hash or read-back rules (AD-17, `ElementHash`, `PageWriteCheck.CompareReadBack`), to what reads return, or to `restore_from_journal` (it rebuilds a whole page into an empty one, where the old `z` keeps the original stacking). No new error or warning code.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Resent Outline | Outline as read, `<one:Position x="36.0" y="200.0" z="1" />` | COM gets `<one:Position x="36.0" y="200.0" />`; all else identical | None |
| No `z` | Position without `z`, or no Position | COM gets the caller's XML unchanged | None |
| New Outline | Outline without `objectID`, Position with `z` | `z` removed | None |
| Odd spelling | `z='1'`, spaces around `=`, `z` on its own line, `z` first or last | Removed cleanly; tag still well-formed | None |
| Look-alikes | `z` text inside a `one:T` CDATA; `foo:z`; `Position` in another namespace | Untouched | None |
| Bad `z` | `z="-1"` | `InvalidXml`, nothing sent (as today) | Existing schema gate |

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/Writes/PageWrite.cs` -- `UpdatePageContentWrite`: `CallAsync` (l.640) sends `xml` to `Gateway.UpdatePageContentAsync`; `AfterCallAsync` (l.650) compares `xml` with the read-back. Every check before them reads the `xml` field; leave those alone.
- `src/OneNoteMcp.Xml/XmlPositions.cs` -- maps `XmlReader` line/column to string offsets; `Offset` is private, `StartTagEnd` skips quoted values. Reuse it for the attribute's offset.
- `src/OneNoteMcp.Xml/ElementSpans.cs`, `SearchResultFilter.cs` -- prior art for cutting exact substrings with `XmlPositions` and `XmlPositions.ReaderSettings`.
- `src/OneNoteMcp.Xml/PageWriteCheck.cs` -- `CompareAttributes` only looks at Outlines, OEs and elements with an `objectID`; `one:Position` is never compared, so read-back needs no change.
- `src/OneNoteMcp.Core/UseCases/RestoreFromJournalUseCase.cs` l.350/364 -- the other `UpdatePageContentAsync` caller; out of scope.
- `tests/OneNoteMcp.Tests/FakeGateway.cs` -- `UpdateCalls` records each sent XML.
- `tests/OneNoteMcp.Tests/UpdatePageContentToolTests.cs` -- l.103 and l.206 assert the sent XML equals the caller's; Outline 40 of `Fixtures/synthetic/update-page-Basic.xml` has `z="1"`, so both now expect the `z`-free text.
- `plugin/skills/guide/references/page-writes.md` l.16 -- names the re-stack as why to use the newest basis.
- `src/OneNoteMcp.Server/Tools/UpdatePageContentTool.cs` l.27-38 -- the description; nothing on `z` today. README says nothing on `z`.

## Tasks & Acceptance

**Execution:**
- [ ] `src/OneNoteMcp.Xml/SentPositions.cs` (new) -- `public static string WithoutZ(string xml)`: stream with `XmlReader`, find each `one:Position`'s `z`, cut it from the original string with its leading whitespace; return the same string when there is none -- the one place that knows the rule.
- [ ] `src/OneNoteMcp.Xml/XmlPositions.cs` -- an internal accessor for an attribute's start offset if `SentPositions` needs it -- reuse, not a second offset map.
- [ ] `src/OneNoteMcp.Core/Writes/PageWrite.cs` -- `UpdatePageContentWrite` computes the sent text once after `Validate`, sends it in `CallAsync` and compares it in `AfterCallAsync`; doc comment says why (Z probe 57) -- checks stay on the caller's XML.
- [ ] `tests/OneNoteMcp.Tests/SentPositionsTests.cs` (new) -- each matrix row, asserting the exact output string.
- [ ] `tests/OneNoteMcp.Tests/UpdatePageContentToolTests.cs` -- a test sending Outline 40 as read (`z="1"`) plus a new Outline whose Position has no `z`: the single `UpdateCalls` XML has no `z` on any Position and keeps `x`/`y`; update l.103 and l.206 to expect `SentPositions.WithoutZ(sent)`.
- [ ] Docs per the answer to Q2; `deferred-work.md` -- mark the l.379 and l.587 entries resolved, and add the spine entry if Q3 is (a).

**Acceptance Criteria:**
- Given any accepted `update_page_content` call, when it reaches COM, then no `one:Position` in the sent XML has a `z`, and the text is otherwise the caller's.
- Given a call refused before COM, then its code and message are as today.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

A text cut, not an `XDocument` round trip: `OneNoteXml.Parse` drops whitespace-only text, which would change a `one:T` holding only a space; precedent is `ElementSpans`/`SearchResultFilter`. Stripping only for COM leaves the basis alone (it hashes the page as read) and the read-back blind to Position as today.

Evidence on new Outlines: in the 2.12 README-only session, adding an Outline changed another Outline's hash (deferred-work l.562); whether that Outline was sent with `z` is not recorded. So the skill keeps "always use the newest basis"; only its reason changes.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings, 0 errors
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, new tests included

**Manual checks (a later sitting, Phill; not run by the build):**
- With the built server, on a throwaway page in MCP Test with two Outlines: read, resend one Outline as read (with its `z`); the other Outline's hash and both `z` values are unchanged. Then add a new Outline (once with `z`, once without) and record whether the other Outline's hash changes.

## Open Questions

1. **A caller who sends a changed `z`.** (a) Always strip, silently; the description says stacking order isn't written. (b) Always strip, plus a warning when a sent `z` differs from the read one (a new warning code: contract change). (c) Strip only a `z` equal to step 3's read value, so a changed `z` restacks on purpose (more code; needs a live check of what OneNote does with it).
2. **Docs.** (a) Skill l.16 reworded (re-sending no longer re-stacks; adding an Outline may still change others' hashes) plus one clause in the tool description. (b) Skill only. (c) (a) plus a README line.
3. **Spine.** The server now changes sent XML, which AD-4's "server quietly changing content" and AD-7 `PageWrite` don't mention. (a) A deferred-work entry for the next spine-sync story (as 2.13 Q3). (b) Amend AD-7 in this story (Fable final review needed).
4. **Which Positions.** (a) Every `one:Position` sent (Outlines and page-level images, ink, files: one stacking order). (b) Only those directly under `one:Outline`.
5. **The sitting check.** (a) Required before Phill marks done; the build stops at `built` without it. (b) Optional; unit tests suffice.
