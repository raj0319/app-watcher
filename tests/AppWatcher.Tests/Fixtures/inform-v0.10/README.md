# Anonymised copies of the v0.10 measured session (do not "fix" the names back)

These files are copies of what App Watcher 0.10.0 recorded during the fourth measured run against the same real ERP
(Infragistics on WinForms, MDI workspace, UniVerse backend) on 2026-11-20, 08:27 to 08:51 (two driven workloads: part A and
complex2). Every real name and identifier was replaced by the same fixed stand-ins as in `../inform-v0.9` (Sample Co, Writer
One, 0000001 "Test Customer", 00001 "ACME", 100EX "SAMPLE PRODUCT", phones "555-555-0100"); the structure, the counts, the
depths, the automation ids, the positions, the order of events, the timestamps and the `how` / `by` fields are unchanged.

- `events-2026-11-20.jsonl`, `collector-status.json` and the screen records (`*.md`) as the watcher wrote them.
- `drive-a.log` and `drive-complex2.log`: the driver's own logs, the truth of the run (what was clicked, typed and hovered,
  to the second, on the test PC's clock at UTC-5). The driver logs carry no pointer positions.
- `dialogs.json`: the layout of the five message boxes of the run (Close?, No Selection Specified, Protected Area, Attention,
  Advanced Demand Forecasting and Purchasing): window size and button rectangles in window pixels, taken from the captions in
  the boxes' pictures (a button is 84 x 26 around its caption). Message boxes are never screen records, so this is the only
  record of where their buttons were; no text of their bodies is kept.

`InformV11Tests` replays every click in the order the live run produced it, through `ClickTracker` (the class the collector
calls): the hovers before the click as samples with no click, the click heard at the event that really followed it (a
document's destroy, the foreground leaving a message box before it closes, the switch to the box an Exit opens), and the
change that confirms it. Points are the centres of the targets' recorded rectangles; message boxes are placed at a fixed origin.

What this fixture cannot hold: the layouts of Product Analysis and Customer Ledger. They share their titles with Product
Master and Customer Master, and v0.10 kept only the first layout per title (the bug v0.11 fixes). Their six sub-tab clicks
and their Exit buttons are covered by the replay only through the toolbar, which is the same in every document of this
program; a driven run of v0.11 must confirm them live.

No picture and no OCR text was copied. `scripts\check-fixtures.ps1 -Forbidden <file>` checks all of `tests\` with the plain
list kept outside the repository.
