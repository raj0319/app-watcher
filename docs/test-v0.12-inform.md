# v0.12 against DDI Inform: the best of every version, then a measured fix loop

Date: 2026-11-20, 14:20 to 22:00. Built and tested on the test PC itself (the .NET 8 SDK was installed there), branch
`best-of-v0.12`. Fifteen driven rounds (r1 to r15) of the same workload as the v0.7 to v0.11 tests, each scored by the same
automatic scorecard (`evidence/harness/scorecard.py`, kept out of git because its inputs hold customer names). Nothing was
saved in Inform: every entry screen was left through Exit → Close? → OK. Read only towards the program throughout.

## How the work was organised

A small "software factory" loop: **diagnose** (a helper reads the last scorecard and the watcher's log and names the code
path of each miss) → **build** (focused changes, each with a synthetic test; learner work done by separate workers in their
own git worktrees, merged after their tests passed) → **verify** (all unit tests, `scripts\live-test.ps1` on the sample app,
publish, install, drive Inform for 20 minutes) → **score** (every row against the best any earlier version reached).

## First: which version had the best mechanism for each row

| Row | Best before v0.12 | Mechanism kept or brought back |
|---|---|---|
| Toolbar opens | v0.9 (23/23) | a main-window toolbar press may take up to 8 s to open its document (v0.11 gave up at 1.5 s) |
| Sub-tab clicks | v0.10 (32/49) | try the document in front when the top window's tree has nothing at the point |
| Exit | v0.11 (18/18) | the closing window's recorded tree is hit-tested first |
| Message-box buttons | v0.7 (3/12) | the v0.7 idea (focused button + window closes) only with a real key press; real clicks now come from the button read |
| Wrong formulas | v0.10 (0) | a formula needs three distinct input sets |
| Flows | v0.10 (real durations) | one flow per document visit |
| Privacy | v0.10 (0 names) | kept; extended to tab names and screen file names |
| v0.1 to v0.6 | — | no mechanism better than a later one for any row (checked in the code) |

## Two findings that change how the earlier scorecards read

1. **The test driver answered message boxes through UI Automation, not the mouse.** The 0-of-N "message-box buttons" of
   v0.9 to v0.11 measured the driver as much as the watcher. The driver now clicks with the mouse, holds the button 90 ms
   like a hand, and never clicks a tab that another window covers (Inform's own notes window lies over some vendor tabs; a
   person cannot click there either).
2. **Price Change and Projected Purchase Order have no code field.** Earlier drivers typed a product and a vendor code into a
   date box and a grid cell there. Those two typings are no longer part of the workload.

## Scorecard by round, day 1 (r1 = first v0.12 build; "b" = the same run re-learned with a newer learner)

| Row | v0.11 | r1 | r2b | r4b | r7b | r10 | r12 | r13 | r14 | r15 |
|---|---|---|---|---|---|---|---|---|---|---|
| Toolbar opens | 13/17 | 16/16 | 16/16 | 16/16 | 17/17 | 18/18 | 18/18 | 18/18 | 18/18 | 18/18 |
| Menu items | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 |
| Exit presses | 18/18 | 16/17 | 17/17 | 17/17 | 18/18 | 19/19 | 19/19 | 19/19 | 19/19 | 19/19 |
| Message-box buttons | 0/6 | 6/6 | 6/6 | 6/6 | 6/6 | 7/7 | 7/7 | 7/7 | 7/7 | 7/7 |
| Add Line Item | 0/1 | 1/1 | 1/1 | 1/1 | 2/2 | 2/2 | 2/2 | 2/2 | 2/2 | 2/2 |
| Presses with a wrong name | 6 | 7 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Sub-tab clicks → select | 24/43 | 24/43 | 33/47 | 34/47 | 34/34 | 31/31 | 31/31 | 29/29 | 33/33 | 27/27 |
| Typed codes | 12/16 | 12/16 | 14/16 | 14/14 | 14/15 | 15/16 | 16/16 | 16/16 | 16/16 | 16/16 |
| Order-line entries (product, partial) | — | 3/3 | 2/3 | 3/3 | 5/6 | 5/6 | 6/6 | 5/6 | 5/6 | 4/6 |
| Program fills recorded as typed | 9 | 6 | 4 | 0 | 1 | 2 | 0 | 1 | 1 | 1 |
| Fragments | 2 | 2 | 0 | 0 | 0 | 0 | 1 | 0 | 0 | 1 |
| Same value twice | 1 | 0 | 0 | 0 | 0 | 2 | 1 | 2 | 0 | 0 |
| Price Extension = Quant Ordered × Net Price | no | no | 5/5 | 3/3 | 5/5 | 6/6 | 5/5 | 8/8 | 8/8 | 10/10 |
| "Typing Customer fills Ship To, …" | no | yes | no | yes | yes | yes | yes | yes | yes | yes |
| Wrong formulas | 1 | 0 | 0 | 0 | 1 | 0 | 0 | 0 | 0 | 0 |
| Lookups right + false | 1 + 2 | 0 + 0 | 0 + 0 | 0 + 0 | 0 + 0 | 1 + 1 | 2 + 0 | 1 + 0 | 1 + 0 | 0 + 0 |
| Flows counted wrong | all | 12 | 3 | 2 | 1 | 1 | 1 | 1 | 1 | 1 |
| Tree reads failed | 30% | 25% | 30% | 29% | 29% | 25% | 22% | 21% | 22% | 21% |
| Real names in memory | 0 | 2 | 0 | 0 | 0 | 0 | 0 | 0 | 1 | 0 |

From r5 on the workload enters two sales orders, not one. r7b's "wrong formula" is "Quant B/O is copied from Quant Ordered", an
observation the scorer later stopped counting. r10's "false" lookup was the same lookup seen at "10" while "100" was typed (the
scorer later accepted a prefix). r14's name in memory (a vendor's name used as a field's label) is fixed in the final build.

**Stable at 100% in each of the last five rounds:** toolbar opens, menu items, Exit, message-box buttons, Add Line Item, sub-tab
clicks, 0 wrong press names, 0 wrong formulas, the price formula (every observation), the customer cause (2 of 2 orders).

## What changed in the watcher (all read only; no hook; nothing is ever clicked or typed by the watcher)

- **Clicks** are heard from a read of the left button every 50 ms while Inform is in front (its own thread), placed where the
  button went down. A click with no event of its own (a message-box button, a plain tab strip) is no longer lost. A
  pointer-move signal was tried and removed: its events arrive late when the watcher is busy.
- **Presses** need a click resolved at its own point, or Enter/Space on a button that already had the focus. The fallback that
  credited whatever lay under the pointer when a window changed is gone (it caused "Rating", "Edit" and "Exit" wrong names).
  A tree hit and a live read that agree on a button are a press at once (Add Line Item).
- **Tabs**: any click re-reads the window under it (also Inform's notes window), compared with the tree recorded at open; a tab
  header part or strip under the point is resolved to its tab item.
- **Typed or filled**: a field changed by the user needs a character key after the focus arrived in it (arrival from the
  program's GUI-thread focus, which is never late); the text of the text box is read with WM_GETTEXT while keys go down, so
  the typed code is kept even when Inform replaces it with the record's name on Enter; the replacement is the program's.
- **Learner** (merged from three workers): whole grid rows for formulas, typed operands win ties, re-read cells are no new
  observations, a quotient that is a product read backwards is no formula, one flow per document visit, a code typed before
  the document takes its title is that document's cause, tab and action names masked like titles.
- **Privacy**: screen and picture file names use the masked title (the company name had been in them since v0.4).

## Day 2 (2026-11-23): the four open items, two independent reviews, 19 more rounds (r16 to r34)

The four items left on day 1 (the partial-code lookup, Price Change's visit time, stray values, failed tree reads) were
traced to their causes in the run data, fixed with tests, and verified on Inform. Two independent code reviews (fresh reviewers
reading only the code) found 13 further problems, all fixed: a document re-poll that could run in the background, the Enter read
in excluded windows, one thread per Enter, disabled buttons counted as pressed, short record names renaming labels, a stale
Escape cancelling menu choices, the box read blocking the button read, and others. Rounds disturbed from outside are not in the
table: r17 and r18 (an Adobe Acrobat popup on this PC took the front and left a modal report window open in Inform), r32 and r33
(Acrobat popups again). r33 also showed a real watcher bug, now fixed: a late "front changed" event left the watcher deaf for 70 s.

| Row | r15 | r19 | r20 | r23 | r25 | r28 | r31 | r34 |
|---|---|---|---|---|---|---|---|---|
| Toolbar opens | 18/18 | 17/17 | 17/17 | 17/17 | 17/17 | 17/17 | 17/17 | 17/17 |
| Menu items | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 |
| Exit presses | 19/19 | 18/18 | 18/18 | 18/18 | 18/18 | 18/18 | 18/18 | 18/18 |
| Message-box buttons | 7/7 | 7/7 | 7/7 | 7/7 | 7/7 | 7/7 | 7/7 | 7/7 |
| Add Line Item | 2/2 | 2/2 | 2/2 | 2/2 | 2/2 | 2/2 | 2/2 | 2/2 |
| Presses with a wrong name | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Sub-tab clicks → select | 27/27 | 39/40 | 39/39 | 39/39 | 39/39 | 39/39 | 36/36 | 38/38 |
| Typed codes | 16/16 | 15/15 | 15/15 | 15/15 | 15/15 | 15/15 | 15/15 | 15/15 |
| Order-line entries (product, partial) | 4/6 | 6/6 | 6/6 | 6/6 | 6/6 | 6/6 | 6/6 | 5/6 |
| Program fills recorded as typed | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 0 |
| Fragments | 1 | 0 | 0 | 0 | 0 | 0 | 1 | 0 |
| Same value twice | 0 | 1 | 1 | 0 | 0 | 0 | 0 | 0 |
| Price formula (seen) | 10/10 | 6/6 | 6/6 | 6/6 | 6/6 | 6/6 | 6/6 | 8/8 |
| "Typing Customer fills …" | yes | yes | yes | yes | yes | yes | yes | yes |
| Wrong formulas | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Lookups right + false | 0 + 0 | 2 + 0 | 2 + 0 | 2 + 0 | 2 + 0 | 2 + 0 | 2 + 0 | 1 + 0 |
| Flows counted wrong | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Tree reads failed | 21% | 0% | 0% | 0% | 0% | 0% | 0% | 0% |
| Real names in memory | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |

r15 is the last round of day 1, for comparison. Each later column is a separate install and a fresh 20-minute run with two sales
orders. r25 and r28 were clean on every row.

Also verified on day 2:
- `scripts\scenario-run.ps1` (the repository's own driver and scorer on the sample program): 14 passed, 0 failed, 2 untested by
  design; no press the steps do not explain. `scripts\live-test.ps1` after every collector change.
- CPU and memory: 0.5% of one core while Inform is in front (the 50 ms button read), 0.00% while it is not; about 61 MB private
  memory, tidied once a minute at most when the program leaves the front.
- 199 tests; the whole suite twice in a row with no failure.

## Day 3 (2026-11-24, v0.12.2): tools researched, the busy box read from its picture

Tools weighed against the open limits: pywinauto and Windows-MCP click controls, so they can only ever drive tests, never sit in
the watcher (our own driver already does that); deepdiff and hypothesis are Python, so their C# counterparts were used:
**CsCheck** property tests (18: learn twice = same memory, incremental = rebuild on random sessions, memory never holds a typed
value, no press without a click, debouncer and field-read invariants). They found one gap: letters without case (Chinese,
Japanese, Arabic) passed through `Masking.Shape`; fixed.

The busy grid box: measured on a test window whose program thread sleeps like Inform building a list. WM_GETTEXT timed out
(155 ms), PrintWindow waited 3.3 s for the program, a copy of the box's own pixels took 1-18 ms and the Windows OCR read the
typed code. The watcher now does that only when the text read times out (`BoxPicture`, rules in CLAUDE.md).

Two more fixes from round r36: a code typed fast and then Enter was read before the program drew its last keys ("100EX" read
as "100"): the box is read once more 150 ms later and at Enter alone; and a late "program in front" event while another program
(Chrome) was already in front no longer puts the watcher in front.

| Round | Result |
|---|---|
| r36 (first v0.12.2) | 23 of 26 rows at 100%; misses: one toolbar open and one code (Chrome took the front, the click went there), "100EX" (the read-too-early above) |
| r37 (fixes) | **26 of 26 rows at 100%**: 16/16 opens, 17/17 Exit, 31/31 sub-tabs, 14/14 codes, 3/3 order lines, lookup 1 right 0 false, 0 strays, 0 failed tree reads; Acrobat took the front twice and was handled |

The picture read did not fire in r36 or r37 (Inform answered every text read in time); it waits for the run where it does not.

## v0.13 (2026-11-24): beyond WinForms, office scale

| Test | Result |
|---|---|
| Inform r38 (after all v0.13 changes) | 26 of 26 rows at 100% (no regression) |
| Web page (tests/SampleWeb, WebView2) | presses 9, typed 15, tabs 7, `Line total = Quantity × Unit price` seen 12× of 12, `Typing Customer code fills Name` learned; in-page HTML dialogs are not dialogs (0) |
| WPF (tests/SampleWpf) | screens 4, presses 20, typed 13, dialogs 5; grid formula not learned (template cells with their own text boxes read as "(empty)") |
| v0.13.1, the same day | WPF 6 of 6 (screens 6, formula `Line total = Quantity × Unit price`, code fills Name); web 7 of 7 (3 in-page dialogs recorded, presses 13); Inform r39 and r40 26 of 26 |

v0.13.1 fixes: a WPF tab's page was cut out of the window as "the largest neighbour of the tab strip", which was the menu
(it holds items named like the tabs): now the tab item itself is the page, and Menu counts as window chrome. Grid dumps read
cell values (WPF template cells). Dialogs drawn inside a web page are found in the read after a click or key
(`DialogDetector.InPage`). A field's old focus arrival stops counting once the focus moved to another field (the second
code's looked-up name had been taken for typed). Tried and taken back: letting the read follow Windows' front window
(r39 filed Inform's lookup under the main window instead of New Sales Order; r40 after the revert is right again).
| Office scale (5 users × 30 days, 328,796 events) | full rebuild 9.3 s warm, one more day 3.4 s, peak 233 MB managed; incremental = rebuild; merge order-independent |
| Idle watcher on this PC | 51 MB private, 12 MB in RAM, 0% CPU with Inform in the background |

## v0.13.2 (2026-11-25): the window in front, handed over safely

In r39 a trial change let the screen read follow whatever window Windows reported in front. When that was Inform's untitled
lookup list, the lookup was filed under the bare main title instead of New Sales Order (every round from r20 to r40 except r39
filed it right). The trial was taken back in v0.13.1. v0.13.2 does the useful part safely: when the window in front closes or
hides before Windows names its successor, or a late event names an untitled drop-down, the next window is picked by
`FrontWindow.Pick`: visible, titled, of the program; a drop-down hands over to its owner and never becomes the screen. It is
decided on the UI thread, never inside a read. 7 new tests (262 in all); WinForms, WPF (6 of 6) and web (7 of 7) live tests
pass. Inform r41: every row 100%, both lookups under New Sales Order; the hand-over was not needed in that run.

## Still not perfect, and why

- **A partial code typed into a grid cell while Inform fills its lookup list** (the "100" of the second order line): before
  v0.12.2, in about one run in three these keystrokes were not seen, because Inform answers neither UI Automation nor WM_GETTEXT
  while it builds the list. v0.12.2 reads the box from its own picture in that moment; it has not yet been needed in a measured
  run, so its effect on Inform is still to be seen.
- **Other programs' popups** (Acrobat on this PC) can take the front in the middle of a run; the watcher now recovers at once,
  but a driver's action during such a popup goes elsewhere.

## Files

- Code: branch `best-of-v0.12`; 239 tests (v0.12.2); `scripts\live-test.ps1` passes.
- Scorecards and driver logs of every round: `evidence/harness/runs/r1` … `r37` (git-ignored: they hold customer names).
- Evidence zip: `evidence/app-watcher-v0.12-inform-evidence-2026-11-20.zip` (for the user's own mail only).
