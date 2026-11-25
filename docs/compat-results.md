# Compatibility runs on other programs (2026-11-25)

App Watcher was proven in depth on one real distribution ERP. To see how it does on programs it was never tuned for, it was
run on five other programs installed on the test PC, each built with a different technology. A test drive
(`scripts/compat/compat_test.py`) starts its own copy of the program, clicks and types at human speed only while that copy is
in front (it refuses any click outside it, types one key at a time and pauses when another program takes the front), never
saves, runs or sends anything in a user's program, closes only the copy it started, and scores what the watcher recorded
against what was done.

| Program | Kind | First run | After the fixes (final build) |
|---|---|---|---|
| Character Map | classic Win32 dialog | 6 of 7 | 7 of 7 in both final rounds |
| Advanced IP Scanner | Qt 5 (never scans) | 6 of 10 | 10 of 10 in both final rounds |
| PowerShell ISE | WPF (never runs a script) | 4 of 9 | 7 of 7 in three runs in a row (one final round had missed a keyboard-opened menu; fixed) |
| Microsoft Excel | Office, spreadsheet grid | 8 of 25 | 25 of 25 in both final rounds (and three runs before them) |
| Microsoft Access | Office, forms over a database | 10 of 13 | 15 of 15 in three runs |

(The drive's steps differ a little from the first run: steps that only opened a menu, and keys typed while another program
had taken the front, are not scored; a formula cell is scored as "a typed value in that cell", since it shows its result.)

What the first runs found, and what changed (each with a test in `tests/AppWatcher.Tests/CompatRulesTests.cs`):

- **Menus**: a Qt menu item announces nothing and a WPF item announces itself after the menu closed, so the choice is now the
  item under the pointer when the button went down; a greyed-out item or the menu-bar item itself never counts.
- **Button names**: Qt puts the shortcut into the name ("Settings Alt+S"); a button that renames itself when pressed
  (Search becomes Reset) was counted twice. One click is one press, under the name clicked.
- **Typed or filled**: a field with no automation id (Qt) looked like the field the focus had just moved into; text a button
  added to a field counted as typed; values kept a trailing line break.
- **Key reading**: the "key went down" bit is shared by every program and Excel takes it first; a key now counts when it is
  seen going from up to down, and the system's last-input time (a read, never a hook) backs it up for grid cells.
- **Excel**: cells commit on Tab or Enter and report afterwards, often late; the ribbon's tabs were taken for documents; a read
  of the Excel window takes seconds and held Excel back. Excel has its own profile now: typing never asks for a window read or
  a picture, and a cell that reports nothing is read once by its row and column.
- **Access**: fields have no window of their own and a new record clears the form, so a field is read once when the keys
  pause; Access's "Detail Section, " prefix is dropped from field names; side-pane links are not fields.

Known limits that remain: a code editor's text is read only while it has the focus; Access databases with macros need the
user's Trust Center to allow them (the test database has none); Citrix and remote desktop sessions are not covered.
