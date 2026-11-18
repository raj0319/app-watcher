# The data folder, file by file

Everything App Watcher learns lives in `C:\ProgramData\AppWatcher\data\<app-name>\` (or in
`AppWatcherData\data\<app-name>\` next to the exe when started with `--portable`). This page is for a
person or an AI that wants to read the raw evidence, not only the memory files.

## events/<yyyy-MM-dd>.jsonl

One JSON object per line, one line per user action inside the watched app. Nothing is written while
another program is in front.

| Field | Meaning |
|---|---|
| `t` | local time of the event |
| `win` | title of the app window that was in front |
| `ct` | control type from UI Automation (Button, Edit, ComboBox, MenuItem, Document, Window, ...) |
| `name` | the control's name (its label or caption) |
| `aid` | the control's AutomationId, when the app sets one |
| `act` | `focus`, `open`, `close`, `invoke` (button or menu pressed), `select`, `value` (a field changed), `foreground`, `dialog` (a message window appeared; `val` holds its text), `start`, `stop` |
| `val` | the new value for `value` events (`***` for password-like fields, text cut at 500 characters), the message text for `dialog` events |
| `dt` | milliseconds since the previous event |
| `fx` | the effects file written for this event, when there is one |

Typing is coalesced: only the final value of a field is written, when the user moves on.

## screens/<title>.md

The control tree of each window, written once the first time the window was seen. The layout is the
App Twin Kit screen record: menu path, purpose, a Fields table, an Actions table, then the raw tree
`| Depth | Type | Name | AutomationId | Position |`.

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

The latest read-only copy of the configured data sources: for every database, every table with its
columns, primary key and rows (values normalized to strings). This is the rolling "before" picture;
it is replaced after every effect capture and refreshed at idle moments when a source changed.
`hot-tables.json` remembers which tables changed in the last diffs, so very large databases are only
partly read.

`screens/index.json` maps the structure hash of each tree (types, names, automation ids; no values or
positions) to its file and lists every title the screen was seen under, so "Invoice 1023" and
"Invoice 1024" are one screen, "Invoice #".

## memory/

Written by the learner (tray: Learn now; nightly; or `AppWatcher.exe --learn`). Six documents
(`data-model.md`, `screens.md`, `rules.md`, `flows.md`, `architecture.md`, `health.md`) plus
`changelog.md`, every line with a confidence tag and an evidence pointer. `architecture-reviewed.md`
is added by the analyze skill or by the optional API/local analysis. No raw values typed by users
appear in this folder.

`learn-state.json` holds the learner's accumulated counters and the watermark (time of the last closed
session processed). Learning is incremental: only sessions newer than the watermark are processed and
merged. Delete the file, or use "Rebuild memory", to recompute from day one; the result is the same.

## SQL statement logs (optional)

Not inside the data folder: the client chooses where they live (Settings, tab 2). Three forms are read:
Extended Events `.xel` files written by `scripts/sql/xevents-session.sql`, a CSV with a timestamp and a
statement column, or the Windows ODBC trace `SQL.LOG` (only the watched program's lines are used).
Statements are parsed into table, columns and values and become exact screen-to-table and
field-to-column evidence (`sql/<file>#<n>` pointers in the memory files).

## export/<date>/

Bundles in the App Twin Kit `docs/raw` layout, plus `events.xes` (process-mining event log) and a zip.
See the README for how to hand them over.
