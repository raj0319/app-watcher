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

## Scorecard by round (r1 = first v0.12 build; "b" = the same run re-learned with a newer learner; r15 = final build)

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

## Still not perfect, and why

- **Tree reads fail about 25–30%** of the time on Inform (UI Automation timeouts on its large grids). The watcher now backs off
  after a slow read, and clicks no longer depend on it, but some document reads still come late.
- **The partial code "100" typed into the second order line and its lookup list** are caught in some runs and not in others
  (0 to 2 per run): the list window is untitled and short-lived, and its tree read often times out on the busy program.
- **Price Change** is counted as one visit but with a duration of about 0.1 s: Inform shows its tab at once, but its document
  window only becomes visible to the watcher when it closes.
- **One program fill or fragment per run** still slips through now and then (a grid cell read while Inform is still answering).
- **Hover tips** depend on how long the driver hovers (2–4 per run; all correct).

## Files

- Code: branch `best-of-v0.12` (commits from `000d42a` on); 187 tests; `scripts\live-test.ps1` passes.
- Scorecards and driver logs of every round: `evidence/harness/runs/r1` … `r15` (git-ignored: they hold customer names).
- Evidence zip: `evidence/app-watcher-v0.12-inform-evidence-2026-11-20.zip` (for the user's own mail only).
