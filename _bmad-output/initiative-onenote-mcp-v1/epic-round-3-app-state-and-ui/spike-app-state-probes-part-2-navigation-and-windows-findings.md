---
title: 'Findings: app-state probes, part 2: navigation and windows'
type: 'spike-findings'
findings_for: '12'
status: 'draft'
created: '2026-11-21'
machine: 'Windows 11 Pro 10.0.26300, OneNote desktop (M365), not elevated; the OneNote build was not recorded by these runs'
evidence: '%LOCALAPPDATA%\OneNoteMcp-Test\probes\app-state-nav-20261121-025922.jsonl, -031737.jsonl, -032533.jsonl and -034016.jsonl (outside the repo; -034016 is the complete link run); Phill''s reports and screenshots per pause, in the coordinator''s sitting notes and the 4.12 Inbox thread'
---

# Findings: app-state probes, part 2: navigation and windows

**Draft: link rows done, window rows still to run.** The sitting ran on 2026-11-21 (UTC), in four runs of the same stamp (`1121-025922`). Runs 1 and 2 stopped at the first link (Security Notice, below); run 3 was ended by Claude Code's low-memory reaper, not by OneNote; run 4 covered every link row and the cleanup. Every window read failed on a probe bug (below, fixed in 92f38d4), so W1–W6 wait for a second, shorter sitting. K2's window column is unaffected: it reads `CurrentWindow`, which never hit the bug.

**Top finding: `NavigateToUrl` on a local `onenote:` link can raise a Security Notice and block until someone answers it.** The notice reads "Microsoft Office has identified a potential security concern. This location may be unsafe." and names the `.one` file's path, with Yes and No (screenshot, run 1). The call doesn't return while it is open: Phill waited about 10 s before Yes and the call took 13.2 s (N1 paragraph); No returned `0x80042014` after 6.3 s (N1 notebook). When the notice opened behind other windows, nobody saw it and the call hit the 60 s probe limit twice (runs 1 and 2, `Timeout`). So a `navigate_to_url` tool can hold the STA thread, and every other tool call, on a prompt that may not even be visible. The notice didn't show on every local link: Phill saw fewer later in the sitting (R1 L) but didn't count them, so what suppresses it isn't known. Cloud links (`onenote:https://d.docs.live.net/…`) never raised it.

**Second finding: a link can open things entry 2 must keep closed (Q6).** A link to a page of a notebook closed the way entry 1 closes reopened that notebook, under a new ID, and showed the page (N4). An `onenote:///` link to a `.one` that isn't in any notebook opened it as a section under Open Sections (N5). Both returned ok.

**Short answer:**

1. **Local links** (`GetHyperlinkToObject` on MCP Test) land where they point once the notice is answered Yes: a page, a section's last-viewed page, a notebook's last-viewed section, and a paragraph, which OneNote selects (N1 L, Phill). Calls took 0.9–13 s, almost all of it waiting for Phill's click.
2. **Cloud links** return in 39–69 ms (989 ms for the notebook link) with no notice (N1 C, Phill). They don't bring OneNote to the front: the foreground stayed on the Inbox window in all four rows. With OneNote in the background, **its window didn't repaint**: COM already reported the new page, but the screen kept the old one until Phill pointed at or clicked OneNote (N1 C notebook, about 2 min; N1 C paragraph, on hover).
3. **A web link** (`GetWebHyperlinkToObject`, `https://onedrive.live.com/…`) isn't followed: OneNote shows "We can't open files from other programs. Please open a OneNote file." with only OK, and the call returns `0x80042014` once OK is pressed (9.9 s). No browser opened (N2).
4. **A link to a missing page returns ok** and shows the section's last-viewed page, locally and on the cloud, with no message (N3). So ok doesn't mean the target was reached.
5. **The repeat window is real only for cloud links, and short.** A cloud `NavigateToUrl` sent straight after another returned `0x80042014` in 19 ms; 1 s later it went through. Cloud `NavigateTo` by ID was never refused, and nothing was refused locally at 0–5 s (R1). OneMore's figure was about 2 s; here it is under 1 s (one pair each).
6. **The landing is visible to COM at once.** In every row the hierarchy's `isCurrentlyViewed` and `CurrentWindow`'s IDs already showed the target at the first read after the call returned (0–1 ms) and didn't change over the 1.5 s series (K1).
7. **The landing can be checked, but only by comparing IDs** (K2): the current page, section and notebook IDs match the target's in every row that landed, and don't in the rows that didn't (N1 L notebook after No, N2). For a missing page (N3) the page comparison has nothing to compare (`null`), and `GetHyperlinkToObject` on the current page doesn't match the link's `page-id`: that mismatch is the only sign the link missed.
8. **A link to a page in the locked Vault returns ok** and lands on the locked section with no page, and no password prompt (N6, Phill).
9. **Error codes:** only `0x80042014` hrObjectDoesNotExist, for three different causes (a declined notice, a web link, a cloud repeat), plus the probe's own `Timeout` (H).

## Results, one row per Matrix question

| Id | Result |
|---|---|
| N1 L | Page, section, notebook, paragraph links (`onenote:` + local path, `page-id` and `section-id` present): ok after a Security Notice answered Yes. Page 0.9 s (run 4) and 1.5 s (run 3), landed Probes › Untitled page. Section 1.3–1.4 s, landed Seed (run 3's landing is confounded: OneNote was already on Seed). Notebook, answered No on purpose in run 4: `0x80042014` after 6.3 s, view unchanged on MCP Test Cloud; run 3, answered Yes: ok 1.2 s, landed MCP Test › Seed (last-viewed section). Paragraph: ok 13.2 s with a 10 s wait before Yes, landed Probes › Untitled page with the paragraph selected. Foreground became `ONENOTE` in each, after Phill clicked its title bar. |
| N1 C | Page 69 ms, section 39 ms, notebook 989 ms, paragraph 45 ms, all ok, no notice. Landed probe r0 manual › Untitled page, Probes › Text Page, Probes (last-viewed section), probe r0 manual with the paragraph selected (Phill, after hover). Foreground unchanged (`other`) in all four; the screen didn't repaint until Phill pointed at or clicked OneNote. |
| N2 | `GetWebHyperlinkToObject` link (`https://onedrive.live.com`): dialog "Microsoft OneNote — We can't open files from other programs. Please open a OneNote file." [OK]; `0x80042014` after 9.9 s; no browser; view unchanged. |
| N3 | Missing page (N1 page link with a synthetic `page-id`): ok, local 1.1 s and cloud 1.0 s; landed on the section's last-viewed page; no message (Phill). The local one raised the notice; the cloud one didn't. |
| N4 | `n1` made (`OpenHierarchy`, 36 ms), linked (`GetHyperlinkToObject`, 249 ms), closed (`CloseNotebook`, 217 ms). Its link, followed: notice, Yes, ok in 1.9 s; `n1` back in the notebook list with a new ID (`{8B1F…}` → `{3867…}`), its page shown (Phill confirmed). |
| N5 | `onenote:///` to `probe nav 1121-025922.one` in the probe publish folder (published by `Publish(pfOneNote)`, 312 ms, never opened): notice, Yes, ok in 1.5 s; opened as section "probe nav 1121-025922" under Open Sections, shown. The hierarchy's viewed notebook, section and page all read `null` with only an Open Sections section in view. Phill closed it by hand. |
| N6 | Vault page link read while unlocked; Phill locked it and moved to Seed; link followed: notice, Yes, ok in 6.5 s; landed on Ballast Vault with no page (`page: null`); no password prompt (Phill). |
| R1 L | `NavigateTo` second call at 0, 1, 2, 3, 5 s: all ok, 25–87 ms. `NavigateToUrl`: all ok, 1.0–4.9 s (each included a notice where one showed). No `0x80042014`. |
| R1 C | `NavigateTo`: all ok, 12–19 ms. `NavigateToUrl`: 0 s `0x80042014` in 19 ms; 1, 2, 3, 5 s ok in 20–23 ms. No notice (Phill). |
| K1 | Every landing series read the target at its first point (0–1 ms after return) and held it to 1.5 s, local and cloud. |
| K2 | Window IDs and hierarchy `isCurrentlyViewed` agree in every row. `GetHyperlinkToObject` on the current page matched the link's `page-id` where a page was the target (N1 page and paragraph, N4, R1 links); it didn't for N3 (missing page). |
| W1–W6 | Not measured: every `Windows` read failed on the probe bug below. The one fact it left: `Windows.Item(i)` is 0-based, and `Item(Count)` fails with `E_INVALIDARG` (raised in .NET as `ArgumentException`, seen in the 2026-11-21 read-only check). |
| H | `0x80042014` hrObjectDoesNotExist: N1 L notebook (notice declined), N2 (web link), R1 C 0 s (repeat). `Timeout` (probe limit, 60 s): runs 1 and 2, notice behind other windows. |

## The window-read bug (probe, not OneNote)

`WindowsSnapshotAsync` reads `Item(i)` for i = 0 … `Count` to record the past-the-end result. OneNote answers `Item(Count)` with `E_INVALIDARG`, which the .NET runtime raises as `ArgumentException`, not `COMException`. The per-item catch took only `COMException`, so the exception left the STA delegate and the gateway reported the whole snapshot as failed (`Windows: 0x80070057`). A read-only check on 2026-11-21 (a temporary test, deleted) showed `app.Windows`, the cast, `Count` (1), `Item(0)` and `CurrentWindow` all working. Fixed in 92f38d4: the window and navigation catches now take the runtime's mapped exceptions too (`ProbeValue.IsComFailure`), with a unit test. The same bug would have broken `Window.NavigateTo`'s handle search (W4).

## Implications for entry 2

1. **`navigate_to_url` can block on a Security Notice for local links.** Entry 2 either refuses local `onenote:` links and navigates by ID instead (`NavigateTo` never raised it here), or treats a blocked call as `Timeout` with the outcome unknown and says a prompt may be waiting in OneNote, possibly hidden.
2. **Q6 needs a check before the call:** a link whose target notebook isn't open (N4) or whose path isn't a notebook section (N5) must be refused, because OneNote opens either.
3. **Don't offer web links to `navigate_to_url`** (N2): they always fail, with a dialog.
4. **ok doesn't prove a landing.** After the call, compare the current page ID with the target (K2); for a missing page that comparison has nothing to compare, and the `page-id` check is the only signal (N3).
5. **No settle wait is needed for COM to report the landing** (K1), but on the cloud the screen may not show it until OneNote is activated: a tool that says "OneNote now shows X" should say "OneNote's current page is X".
6. **The repeat window affects only cloud links, under 1 s here.** One retry after 1 s would cover it, but AD-17 says no automatic retries; report `0x80042014` from `NavigateToUrl` as possibly a repeat.
7. **`0x80042014` from `NavigateToUrl` is ambiguous** (declined notice, web link, repeat), so its AD-9 mapping can't say "object does not exist" for this method.
8. **A link to a locked page doesn't prompt for the password** and lands on the section (N6), so navigating doesn't unlock anything.

## Not probed

- W1–W6 (window reads, a quick-note window, two windows, no window): second sitting.
- What makes the Security Notice show on some local links and not others (not counted in R1 L).
- `NavigateToUrl` with `fNewWindow=true`, `Window.NavigateToUrl` and the window setters (plan, Never).

## Evidence gaps

- Run 3's section landing is confounded (OneNote was already on Seed); run 4's isn't.
- The cloud repaint delay rests on two of Phill's observations (N1 C notebook and paragraph); the probe can't see the screen.
- The repeat window is one pair per delay.
- The window-read front case (W2) in runs 1 and 2 was measured against a OneNote shown through Remote Desktop on another computer, not this one; those rows don't count.

## Relied on, not re-probed

- `NavigateTo` takes focus (spike 1 row 25).
- A close and reopen changes every ID and keeps the path (spike 2.2 constraint 9; 4.1).
- A published `.one` opened by `OpenHierarchy` lands as a hidden Open Section (5.1 constraints 7 and 17).
- OneMore research rows 5, 10, 14, 24 and 25 (link page GUIDs survive moves; release window wrappers; cloud repeat window about 2 s; `NavigateToUrl` reports success for a missing page).

## Left behind

- Nothing: `n1` closed by cleanup, the run folder and the published `.one` deleted, the loose section closed by Phill; his screenshot after cleanup shows the notebook list as before the sitting.
