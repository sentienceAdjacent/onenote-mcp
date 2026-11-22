---
title: 'Findings: app-state probes, part 2: navigation and windows'
type: 'spike-findings'
findings_for: '12'
status: 'draft'
created: '2026-11-21'
machine: 'Windows 11 Pro 10.0.26300, OneNote desktop (M365), not elevated; the OneNote build was not recorded by these runs'
evidence: '%LOCALAPPDATA%\OneNoteMcp-Test\probes\app-state-nav-20261121-025922.jsonl, -031737.jsonl, -032533.jsonl and -034016.jsonl (first sitting, outside the repo; -034016 is the complete link run); %LOCALAPPDATA%\OneNoteMcp-Test\probes\app-state-nav-20261122-021935.jsonl (second sitting, window rows; its W2 front reads are void) and -023822.jsonl (windows-read rerun, the valid W2 front reads); Phill''s reports and screenshots per pause, in the coordinator''s sitting notes and the 4.12 Inbox thread'
---

# Findings: app-state probes, part 2: navigation and windows

**Draft.** Two sittings. The first ran on 2026-11-21 (UTC), in four runs of the same stamp (`1121-025922`). Runs 1 and 2 stopped at the first link (Security Notice, below); run 3 was ended by Claude Code's low-memory reaper, not by OneNote; run 4 covered every link row and the cleanup. Every window read in it failed on a probe bug (below, fixed in 92f38d4). The second sitting, on 2026-11-22 (UTC), ran the window rows with the fix (stamp `1121-211923`: steps `windows-read`, `vault`, `quick-note`, `two-windows`, `cleanup`, `no-window`). Its W2 front reads don't count (Phill brought OneNote forward too late, so the foreground was another app), so `windows-read` ran again on its own (stamp `1121-213820`); those front reads are the valid ones. Every window read in the second sitting returned its snapshot, so the fix holds against real OneNote.

**Top finding: `NavigateToUrl` on a local `onenote:` link can raise a Security Notice and block until someone answers it.** The notice reads "Microsoft Office has identified a potential security concern. This location may be unsafe." and names the `.one` file's path, with Yes and No (screenshot, run 1). The call doesn't return while it is open: Phill waited about 10 s before Yes and the call took 13.2 s (N1 paragraph); No returned `0x80042014` after 6.3 s (N1 notebook). When the notice opened behind other windows, nobody saw it and the call hit the 60 s probe limit twice (runs 1 and 2, `Timeout`). So a `navigate_to_url` tool can hold the STA thread, and every other tool call, on a prompt that may not even be visible. The notice didn't show on every local link: Phill saw fewer later in the sitting (R1 L) but didn't count them, so what suppresses it isn't known. Cloud links (`onenote:https://d.docs.live.net/…`) never raised it.

**Second finding: a link can open things entry 2 must keep closed (Q6).** A link to a page of a notebook closed the way entry 1 closes reopened that notebook, under a new ID, and showed the page (N4). An `onenote:///` link to a `.one` that isn't in any notebook opened it as a section under Open Sections (N5). Both returned ok.

**Window finding: reading `Windows` is harmless, but which window a navigation moves isn't obvious.** Reading the whole window list changed neither focus nor the view in six reads, background and front (W2). With two windows, app-level `NavigateTo` moved the first (original) window, not `CurrentWindow` (the second, newer one), and `Window.NavigateTo` moved only the window it was called on; neither took focus (W4). With OneNote's last window closed, `NavigateTo` returned ok in 0.6 s and opened a window in front, on the target page (W6). A quick note counts as a window and becomes `CurrentWindow` while it is open (W3).

**Short answer:**

1. **Local links** (`GetHyperlinkToObject` on MCP Test) land where they point once the notice is answered Yes: a page, a section's last-viewed page, a notebook's last-viewed section, and a paragraph, which OneNote selects (N1 L, Phill). Calls took 0.9–13 s, almost all of it waiting for Phill's click.
2. **Cloud links** return in 39–69 ms (989 ms for the notebook link) with no notice (N1 C, Phill). They don't bring OneNote to the front: the foreground stayed on the Inbox window in all four rows. With OneNote in the background, **its window didn't repaint**: COM already reported the new page, but the screen kept the old one until Phill pointed at or clicked OneNote (N1 C notebook, about 2 min; N1 C paragraph, on hover).
3. **A web link** (`GetWebHyperlinkToObject`, `https://onedrive.live.com/…`) isn't followed: OneNote shows "We can't open files from other programs. Please open a OneNote file." with only OK, and the call returns `0x80042014` once OK is pressed (9.9 s). No browser opened (N2).
4. **A link to a missing page returns ok** and shows the section's last-viewed page, locally and on the cloud, with no message (N3). So ok doesn't mean the target was reached.
5. **The repeat window is real only for cloud links, and short.** A cloud `NavigateToUrl` sent straight after another returned `0x80042014` in 19 ms; 1 s later it went through. Cloud `NavigateTo` by ID was never refused, and nothing was refused locally at 0–5 s (R1). OneMore's figure was about 2 s; here it is under 1 s (one pair each).
6. **The landing is visible to COM at once.** In every row the hierarchy's `isCurrentlyViewed` and `CurrentWindow`'s IDs already showed the target at the first read after the call returned (0–1 ms) and didn't change over the 1.5 s series (K1).
7. **The landing can be checked, but only by comparing IDs** (K2): the current page, section and notebook IDs match the target's in every row that landed, and don't in the rows that didn't (N1 L notebook after No, N2). For a missing page (N3) the page comparison has nothing to compare (`null`), and `GetHyperlinkToObject` on the current page doesn't match the link's `page-id`: that mismatch is the only sign the link missed.
8. **A link to a page in the locked Vault returns ok** and lands on the locked section with no page, and no password prompt (N6, Phill).
9. **Error codes:** only `0x80042014` hrObjectDoesNotExist, for three different causes (a declined notice, a web link, a cloud repeat), plus the probe's own `Timeout` and `Windows.Item(Count)`'s `E_INVALIDARG` (H).
10. **Window reads** are 0-based, take 9–33 ms, and change nothing on screen (W1, W2). Each window's `WindowHandle` is an inner pane, not a top-level window (W1).
11. **`CurrentWindow` is the most recently activated window**, the one with `Active` true: the quick note while one is open (W3), the Ctrl+M window right after it opens, and then whichever window a navigation moved (W4).
12. **App-level `NavigateTo` with two windows moves the first window**, not the current one, and makes it current; `Window.NavigateTo` moves only its own window. Neither took focus (W4).
13. **In a locked section, `CurrentPageId` is an empty string**, not an error, and `CurrentSectionId` still names the section (W5).
14. **With no window, `Count` is 0 and `CurrentWindow` is null (S_OK)**; `NavigateTo` then opens a window in front on the target (W6).

## Results, one row per Matrix question

| Id | Result |
|---|---|
| N1 L | Page, section, notebook, paragraph links (`onenote:` + local path, `page-id` and `section-id` present): ok after a Security Notice answered Yes. Page 0.9 s (run 4) and 1.5 s (run 3), landed Probes › Untitled page. Section 1.3–1.4 s, landed Seed (run 3's landing is confounded: OneNote was already on Seed). Notebook, answered No on purpose in run 4: `0x80042014` after 6.3 s, view unchanged on MCP Test Cloud; run 3, answered Yes: ok 1.2 s, landed MCP Test › Seed (last-viewed section). Paragraph: ok 13.2 s with a 10 s wait before Yes, landed Probes › Untitled page with the paragraph selected. Foreground became `ONENOTE` in each, after Phill clicked its title bar. |
| N1 C | Page 69 ms, section 39 ms, notebook 989 ms, paragraph 45 ms, all ok, no notice. Landed probe r0 manual › Untitled page, Probes › Text Page, Probes (last-viewed section), probe r0 manual with the paragraph selected (Phill, after hover). Foreground unchanged (`other`) in all four; the screen didn't repaint until Phill pointed at or clicked OneNote. |
| N2 | `GetWebHyperlinkToObject` link (`https://onedrive.live.com`): dialog "Microsoft OneNote — We can't open files from other programs. Please open a OneNote file." [OK]; `0x80042014` after 9.9 s; no browser; view unchanged. |
| N3 | Missing page (N1 page link with a synthetic `page-id`): ok, local 1.1 s and cloud 1.0 s; landed on the section's last-viewed page; no message (Phill). The local one raised the notice; the cloud one didn't. |
| N4 | `n1` made (`OpenHierarchy`, 36 ms), linked (`GetHyperlinkToObject`, 249 ms), closed (`CloseNotebook`, 217 ms). Its link, followed: notice, Yes, ok in 1.9 s; `n1` back in the notebook list with a new ID (`{8B1F…}` → `{3867…}`), its page shown (Phill confirmed). |
| N5 | `onenote:///` to `probe nav 1121-025922.one` in the probe publish folder (published by `Publish(pfOneNote)`, 312 ms, never opened): notice, Yes, ok in 1.5 s; opened as section "probe nav 1121-025922" under Open Sections, shown. The hierarchy's viewed notebook, section and page all read `null` with only an Open Sections section in view. Phill closed it by hand. |
| N6 | Vault page link read while unlocked; Phill locked it and moved to Seed; link followed: notice, Yes, ok in 6.5 s; landed on Ballast Vault with no page (`page: null`); no password prompt (Phill). Second sitting (`n6-locked-vault-link`, 1121-211923): the same, ok in 4.4 s. |
| R1 L | `NavigateTo` second call at 0, 1, 2, 3, 5 s: all ok, 25–87 ms. `NavigateToUrl`: all ok, 1.0–4.9 s (each included a notice where one showed). No `0x80042014`. |
| R1 C | `NavigateTo`: all ok, 12–19 ms. `NavigateToUrl`: 0 s `0x80042014` in 19 ms; 1, 2, 3, 5 s ok in 20–23 ms. No notice (Phill). |
| K1 | Every landing series read the target at its first point (0–1 ms after return) and held it to 1.5 s, local and cloud. The second sitting's four (`n6-locked-vault-link`, `w4-app-navigate`, `w4-window-navigate`, `w6-navigate`) did the same. |
| K2 | Window IDs and hierarchy `isCurrentlyViewed` agree in every row. `GetHyperlinkToObject` on the current page matched the link's `page-id` where a page was the target (N1 page and paragraph, N4, R1 links); it didn't for N3 (missing page). Window column, second sitting: `CurrentWindow`'s notebook, section and page matched the target after app-level `NavigateTo` with two windows (`w4-app-navigate`), after `Window.NavigateTo` on the second window (`w4-window-navigate`), and after `NavigateTo` with no window (`w6-navigate`), because the navigated window becomes current each time; the hierarchy agreed. For the locked-Vault link (`n6-locked-vault-link`) notebook and section matched and page didn't (empty), and the `GetHyperlinkToObject` check was refused by the guard (the locked pages aren't in the tree). |
| W1 | One window (background read, rerun 1121-213820, probe `w1`): `Count` 1; `Item(0)` is that window and `CurrentWindow`; `Item(1)` (= `Item(Count)`) fails with `E_INVALIDARG` `0x80070057`, so the index base is 0 and the end is `Count - 1`. Every getter answered without an error: `WindowHandle` (a live window, but not top-level: it is an inner pane whose root window is OneNote's frame), `CurrentPageId`, `CurrentSectionId`, `CurrentSectionGroupId` (empty for a section not in a group), `CurrentNotebookId`, `FullPageView` false, `Active` true, `DockedLocation` 0, `SideNote` false. A full read took 9–33 ms. The same shape held in every window read of both runs (`w1`, `w2-*`, `w3-*`, `w4-*`, `w5-*`, `w6-windows`). |
| W2 | Three full reads with OneNote in the background (`w2-background-1…3`, both runs) and three with it in front (`w2-front-1…3`, rerun 1121-213820, foreground `ONENOTE` before and after each): `focusChanged` and `viewChanged` false in all; Phill saw nothing change either time. In front, the window's root reads as the foreground window (`RootIsForeground` true); in the background it doesn't. The main run's front reads (1121-211923) had another app in front and don't count. |
| W3 | Quick note open (Win+Alt+N, `w3-quick-note-open`): `Count` 2; `Item(1)` is the quick note and is `CurrentWindow` (`Active` true, `SideNote` true, `FullPageView` true), on a page in the Quick Notes section of a notebook outside MCP Test; `Item(0)`, the main window, went `Active` false; `Item(2)` `E_INVALIDARG`. Closed by its X (`w3-quick-note-closed`): `Count` 1, the main window current again, and now reporting a page in the same Quick Notes section (not the one the quick note showed, by ID) instead of the locked Vault it showed before. Phill: OneNote asked nothing on close, and one empty untitled page was left in Quick Notes; Win+Alt+N reuses an existing empty untitled page and makes a new one only once the previous one has content. |
| W4 | Two windows on Seed's sentinel page (Ctrl+M, `w4-two-windows`): `Count` 2, `CurrentWindow` = `Item(1)`, the new window. App-level `NavigateTo` to a Probes page (`w4-app-navigate`, ok, 336 ms): `Item(0)`, the first (original) window, moved; `Item(1)` didn't; `CurrentWindow` became `Item(0)`. `Window.NavigateTo` on `Item(1)` (found by handle) to another Seed page (`w4-window-navigate`, ok, 366 ms): only `Item(1)` moved, and it became `CurrentWindow`. Foreground `other` before and after both calls; Phill confirmed which window moved and that neither took focus. |
| W5 | Vault unlocked, a Vault page shown (`w5-unlocked`, 376 ms): `CurrentSectionId` is the Vault and `CurrentPageId` a page in it. `pageIsVaultPage` false only because the probe compares with the Vault's first listed page (the one it linked for N6) and Phill showed a different one: the reported page is in the Vault section, but isn't that page. Locked, the same view (`w5-locked`): `CurrentPageId` is `""` (S_OK, empty, no error), `CurrentSectionId` still the Vault, and the hierarchy's viewed page `null`. |
| W6 | OneNote's last window closed by its X; OneNote stayed running with only its notification-area icon (Phill). `w6-windows`: `Count` 0, `CurrentWindow` null (S_OK), `Item(0)` `E_INVALIDARG`. App-level `NavigateTo` to Seed's sentinel page (`w6-navigate`): ok in 559 ms; a new window (new handle) opened on that page and took the foreground (`ONENOTE`), `Count` 1, landing at the first read. |
| H | `0x80042014` hrObjectDoesNotExist: N1 L notebook (notice declined), N2 (web link), R1 C 0 s (repeat). `Timeout` (probe limit, 60 s): runs 1 and 2, notice behind other windows. `0x80070057` E_INVALIDARG: `Windows.Item(Count)`, every window read. The second sitting had no other HRESULT. |

## The window-read bug (probe, not OneNote)

`WindowsSnapshotAsync` reads `Item(i)` for i = 0 … `Count` to record the past-the-end result. OneNote answers `Item(Count)` with `E_INVALIDARG`, which the .NET runtime raises as `ArgumentException`, not `COMException`. The per-item catch took only `COMException`, so the exception left the STA delegate and the gateway reported the whole snapshot as failed (`Windows: 0x80070057`). A read-only check on 2026-11-21 (a temporary test, deleted) showed `app.Windows`, the cast, `Count` (1), `Item(0)` and `CurrentWindow` all working. Fixed in 92f38d4: the window and navigation catches now take the runtime's mapped exceptions too (`ProbeValue.IsComFailure`), with a unit test. The same bug would have broken `Window.NavigateTo`'s handle search (W4). Proven against real OneNote in the second sitting: every window read returned its snapshot with `Item(Count)` recorded as `E_INVALIDARG`, and the W4 handle search found its window.

## Implications for entry 2

1. **`navigate_to_url` can block on a Security Notice for local links.** Entry 2 either refuses local `onenote:` links and navigates by ID instead (`NavigateTo` never raised it here), or treats a blocked call as `Timeout` with the outcome unknown and says a prompt may be waiting in OneNote, possibly hidden.
2. **Q6 needs a check before the call:** a link whose target notebook isn't open (N4) or whose path isn't a notebook section (N5) must be refused, because OneNote opens either.
3. **Don't offer web links to `navigate_to_url`** (N2): they always fail, with a dialog.
4. **ok doesn't prove a landing.** After the call, compare the current page ID with the target (K2); for a missing page that comparison has nothing to compare, and the `page-id` check is the only signal (N3).
5. **No settle wait is needed for COM to report the landing** (K1), but on the cloud the screen may not show it until OneNote is activated: a tool that says "OneNote now shows X" should say "OneNote's current page is X".
6. **The repeat window affects only cloud links, under 1 s here.** One retry after 1 s would cover it, but AD-17 says no automatic retries; report `0x80042014` from `NavigateToUrl` as possibly a repeat.
7. **`0x80042014` from `NavigateToUrl` is ambiguous** (declined notice, web link, repeat), so its AD-9 mapping can't say "object does not exist" for this method.
8. **A link to a locked page doesn't prompt for the password** and lands on the section (N6), so navigating doesn't unlock anything.
9. **`get_windows` can read freely** (W2): no focus or view change, 9–33 ms. Walk `Item(0)` … `Item(Count - 1)`, never past it (`E_INVALIDARG`), and release every wrapper. `WindowHandle` is an inner pane: a tool that reports a top-level window must walk up to its root (W1).
10. **"The current page" means the most recently active window's page**, which can be a quick note in a notebook the user never asked about (W3), or an empty string in a locked section (W5). A tool reading it must accept `""` as "no page", not as an ID.
11. **`navigate_to` without a window argument doesn't move `CurrentWindow` when there are two**: it moved the first window (W4). A tool that promises to move the window the user is looking at needs the window argument (`Window.NavigateTo` on the window's handle), and its result should say which window moved. One sample: whether OneNote picks the first-opened window or `Item(0)` isn't separable here.
12. **`navigate_to` with no window open opens one, in front** (W6). The tool's description (from story 1.21's review: "does nothing visible with no window") is wrong on this machine and must change.
13. **Focus after `NavigateTo` isn't reliable** (W4: none, with two windows; W6: yes, a new window), so a tool shouldn't promise OneNote comes to the front.

## Not probed

- What makes the Security Notice show on some local links and not others (not counted in R1 L).
- `NavigateToUrl` with `fNewWindow=true`, `Window.NavigateToUrl` and the window setters (plan, Never).

## Evidence gaps

- Run 3's section landing is confounded (OneNote was already on Seed); run 4's isn't.
- The cloud repaint delay rests on two of Phill's observations (N1 C notebook and paragraph); the probe can't see the screen.
- The repeat window is one pair per delay.
- The window-read front case (W2) in runs 1 and 2 was measured against a OneNote shown through Remote Desktop on another computer, not this one; those rows don't count. In the second sitting's main run (1121-211923) the front reads had another app in front; only the rerun's (1121-213820) count.
- W4 is one pair of navigations with two windows, both with OneNote in the background; which window app-level `NavigateTo` picks with OneNote in front, or with three windows, isn't known.
- W3's main window switching to a Quick Notes page on close rests on the COM read alone; Phill didn't report it and the probe can't see the screen.
- W6 is one call; the window opened in front although another app was in front before the call.

## Relied on, not re-probed

- `NavigateTo` takes focus (spike 1 row 25). Not so in W4 (two windows, OneNote in the background, foreground unchanged); so in W6 (no window). Not re-probed with one window by ID.
- A close and reopen changes every ID and keeps the path (spike 2.2 constraint 9; 4.1).
- A published `.one` opened by `OpenHierarchy` lands as a hidden Open Section (5.1 constraints 7 and 17).
- OneMore research rows 5, 10, 14, 24 and 25 (link page GUIDs survive moves; release window wrappers; cloud repeat window about 2 s; `NavigateToUrl` reports success for a missing page).

## Left behind

- First sitting: nothing. `n1` closed by cleanup, the run folder and the published `.one` deleted, the loose section closed by Phill; his screenshot after cleanup shows the notebook list as before the sitting.
- Second sitting: one empty untitled quick-note page in Quick Notes (W3; OneNote reuses it for the next quick note). Cleanup of both runs verified by `get_hierarchy`: the throwaways `b` and `c` open, MCP Test and MCP Test Cloud open, no notebook from 1121-211923 or 1121-213820 left (`cleanup-state`, `cleanup-test-notebooks`).
