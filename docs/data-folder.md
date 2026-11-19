# The data folder, file by file

Everything App Watcher learns lives in `C:\ProgramData\AppWatcher\data\<app-name>\` (or in
`AppWatcherData\data\<app-name>\` next to the exe when started with `--portable`). This page is for a
person or an AI that wants to read the raw evidence, not only the memory files.

## events/<yyyy-MM-dd>.jsonl

One JSON object per line, one line per user action inside the watched app. Nothing is written while
another program is in front. After a merge from another PC the file is `<yyyy-MM-dd>.<machine>.jsonl`.

| Field | Meaning |
|---|---|
| `t` | local time of the event |
| `win` | title of the app window that was in front; for a tab document "Window › Tab" |
| `ct` | control type from UI Automation (Button, Edit, ComboBox, MenuItem, Document, Window, ...) |
| `name` | the control's label: its own Name, else the text left of or above it, else its cleaned AutomationId |
| `aid` | the control's AutomationId, when the app sets one (raw) |
| `act` | `focus`, `open`, `close`, `invoke` (button or menu pressed), `select`, `value` (a field changed), `foreground`, `dialog` (a message window appeared; `val` holds its text), `dropdown` (a suggestion list opened while typing into `name`; `val` is the text typed so far, `items` the suggestions), `result` (what a grid showed right after the action in `name`; `val` is the grid, `items` the header line then the first rows, cells joined by " \| "), `menu` (a menu opened: `name` is the menu, `items` what it offers), `hint` (a hover tip: `name` is the control under the pointer, `val` the tip's text), `start`, `stop` |
| `how` | for `invoke`: how the press was seen: `uia` (Invoke event), `msaa` (older accessibility event or pressed state), `menu` (a menu closed after the item was highlighted), `inferred` (focus on a button followed within half a second by a window change), `toggle` (check box) |
| `val` | the new value for `value` events (`***` for password-like fields, text cut at 500 characters), the message text for `dialog` events, `on`/`off` for toggles |
| `dt` | milliseconds since the previous event |
| `fx` | the effects file written for this event, when there is one |
| `items` | for `dropdown`, `result` and `menu` events: the suggestions, the grid's header and first rows, or the menu's items |
| `by` | for `value` events: `app` when the program filled the value in itself (the field did not have keyboard focus: a total recalculated, a name looked up); absent when the user typed it |

The `start` event's `val` holds hashes of the Windows user and machine names (`user:… machine:… version:…`),
never the names, so a merged memory can count users and PCs. Typing is coalesced: only the final value of
a field is written, when the user moves on.

## screens/<title>.md

The control tree of each distinct window or tab document, written once the first time it was seen (a
richer tree of the same screen replaces it). The layout is the App Twin Kit screen record: menu path,
purpose, reached from, **Kind** (`screen`, `tab`, `session`, `dialog`), title pattern, structure hash, tree
source (`uia-cached`, `uia-live`, `uia-raw`, `msaa`), a Fields table (label, type, AutomationId; toolkit
wrappers collapsed to one field), an Actions table with shortcuts, then the raw tree
`| Depth | Type | Name | AutomationId | Position |`. Grids keep their headers and first rows plus one
note row with the row count and column names.

`screens/shots/<screen>/` holds the pictures of that screen, page-wise: `01-first-seen.png`, then one per
distinct state, `02-after-press-search.png`, `03-after-type-customer-code.png`, `04-dropdown-city.png`,
`05-dialog-validation.png`, and, when OCR is on, `<picture>.png.ocr.json` and `<picture>.png.txt` next to each picture (the text the
built-in Windows OCR read, with each line's box), and an `index.md` listing each picture with the action that caused it, the time
and the event. Only the app's own window is pictured, never the desktop. The same state (judged by a coarse
hash of the picture, so a blinking caret does not count) is never stored twice; the picture before an action
is the previous row. Caps: 25 per screen and 500 MB per app by default (Settings, Advanced, Privacy: off / first / actions;
never for login or password windows or for excluded windows). The screen record's `Screenshot:` line names the first one.

`screens/index.json` maps the structure hash of each tree (types, names, automation ids; no values or
positions) to its file, its kind and every title the screen was seen under, so "Invoice 1023" and
"Invoice 1024" are one screen, "Invoice #". The learner also uses those title groups to fold varying words
("Customer - # "…"").

## effects/<timestamp>.json

What changed in the app's data right after a save-type action (or after a data file changed):

- `Reason`: `invoke` (a trigger button such as Save was pressed) or `file-change` (a watched file changed)
- `Win`, `TriggerName`, `TriggerType`, `TriggerAid`: the window and control that caused it
- `BeforeTaken`, `AfterTaken`: when the two snapshots were read
- `Diff.Tables[]`: per table, `Added` (key → row), `Removed` (key → row), `Changed` (key → column → old/new), `ChangedRows` (full new rows)
- `Diff.Files[]`: files `created`, `deleted` or `modified`

The text form of a diff (`DiffResult.ToMarkdown`) is identical to what App Twin Kit's
`scripts/snapshot_diff.py diff` prints.

## snapshots/before.json

The latest read-only copy of the configured data sources: for every database (SQLite, Access, SQL Server,
any ODBC source, an export drop folder of CSV/XLSX files), every table with its columns, primary key and
rows (values normalized to strings). This is the rolling "before" picture; it is replaced after every
effect capture and refreshed at idle moments when a source changed. `hot-tables.json` remembers which
tables changed in the last diffs, so very large databases are only partly read.

## collector-status.json and expectations.md

`collector-status.json` is the collector's own account, rewritten every two minutes: windows seen, tree
dumps that worked, failed or came back empty, how many screens came through MSAA, presses by path (UIA,
MSAA, menu, inferred), toggles, dialogs, values typed, excluded events. The learner copies its problems
into `memory/health.md` ("What the collector could not do"). `expectations.md` says in plain words what
can be learned from this app (toolkit, backends, data sources), the same text as the green box in step 1 of the first run.

## memory/

Written by the learner (tray: Update what I learned; nightly; one hour after the first start; or `AppWatcher.exe --learn`). Seven documents
(`data-model.md`, `screens.md`, `rules.md`, `flows.md`, `architecture.md`, `health.md`, `logic.md`), `logic.json`
(the same logic for a program or an AI: per screen the fields typed, what the program fills in and after what,
the calculations with their fit counts, what each button leads to, hover tips, rules, tables written, what nobody
used; plus the questions to ask), and `changelog.md`, every line with a confidence tag and an evidence pointer. `architecture-reviewed.md`
is added by the analyze skill or by the optional API/local analysis. No raw values typed by users
appear in this folder; window titles have their quoted parts masked.

`learn-state.json` holds the learner's accumulated counters and the watermark (time of the last closed
session processed). Learning is incremental: only sessions newer than the watermark are processed and
merged. Delete the file, or use "Recompute everything I learned from day one" (Settings, Advanced, Learning), to recompute from day one; the result is the same.
(When new titles make the title normalizer fold a screen differently, a rebuild lines the counts up again.)

## SQL statement logs (optional)

Not inside the data folder: the client chooses where they live (Settings, Advanced, page 2). Four forms are read:
Extended Events `.xel` files written by `scripts/sql/xevents-session.sql`, a CSV with a timestamp and a
statement column, the Windows ODBC trace `SQL.LOG` (only the watched program's lines are used), and any
text log with timestamped INSERT/UPDATE/DELETE lines (MySQL general log, PostgreSQL `log_statement`,
Oracle or UniVerse exports; DBA scripts in `scripts/sql/`). Statements are parsed into table, columns and
values and become exact screen-to-table and field-to-column evidence (`sql/<file>#<n>` pointers).

## export/<date>/

Bundles in the App Twin Kit `docs/raw` layout (screen records with the learned menu path and
predecessor filled in, the pictures under `screens/shots`, tasks, probes, data model, memory), plus
`events.xes` (process-mining event log) and a zip. See the README for how to hand them over.
