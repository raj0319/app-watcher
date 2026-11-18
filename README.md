# App Watcher

A small Windows program that sits in the tray, watches one chosen desktop app while real people use
it, and slowly writes down how that app works: its screens, fields, tables, rules and flows. Think of it
as a quiet architect taking notes behind the user.

It does not watch other programs, it never shows a pop-up, and it never changes anything in the app it
watches. It keeps pictures of the app's own window, one per state of each screen (never the desktop, never
the same state twice), and that can be switched off.

## What you get

After a few days of normal work, the folder `C:\ProgramData\AppWatcher\data\<app-name>\memory\` holds
six living documents, rewritten every night:

| File | What it says |
|---|---|
| `data-model.md` | the tables and columns the app saves to, the type of each column, which screen field fills it, how tables link, the backends found (server, port, database), and the exact SQL statements seen when SQL capture is on |
| `screens.md` | every window and tab document seen, its fields (with how often each was typed into and what kind of value goes in), its buttons and shortcuts, where it is reached from, which menu item opens it, and which tables it writes |
| `rules.md` | validation messages (which field, which kind of value, which message), messages after a button or menu, calculated columns with the formula that fits and its exceptions, numbering sequences |
| `flows.md` | status flows (for example open → paid → shipped) with rare and not-allowed transitions, user flows with the presses and typed fields on each step, and a directly-follows navigation graph (Mermaid, renders on GitHub) |
| `architecture.md` | the toolkit and backends seen from the outside, modules (screens grouped by the tables they share) and an honest "seen vs inferred" count |
| `health.md` | events per day, presses seen, pictures and OCR pages, how many saves produced a data diff, how many fields are mapped, what the collector could not do, and what would improve accuracy |

Every line carries a confidence: `seen 12×` means it was observed twelve times; `candidate` means fewer
than three distinct observations; `inferred` means the structure suggests it but nobody proved it.
Every line also names the evidence file to open. `changelog.md` lists what changed since the last learn.

The memory files never contain values that users typed. They contain names, shapes and counts. Window
titles have their quoted parts masked ("Customer - 0002776 "Nate Test"" becomes "Customer - # "…"").

Next to the memory: `screens\<title>.md` with the control tree of every screen and `screens\shots\<screen>\`,
one picture per state of that screen with an index saying which action led to it, so a person or an AI can
compare the old app and the new one state by state.

## Try it first

[docs/demo-test.md](docs/demo-test.md) is a 30-minute test on one PC: install, use the app, learn,
export, send the folder for analysis, uninstall.

## Install (five minutes)

You need a Windows 10 or 11 PC where the app to watch is used.

1. Get `AppWatcher.exe`. Either download it from the releases of this repository, or build it yourself
   (install the .NET 8 SDK, then run `scripts\publish.ps1`; the exe appears in `publish\`).
2. Open PowerShell in this folder and run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

   This copies the program to your user folder, makes it start at login, and starts it now.
   Add `-ScheduledTask` if your IT prefers a scheduled task over the Run key.
3. The setup window opens:
   - **1. App to watch**: pick the program from the list of running programs, or press **Browse...**
     and choose any .exe on any drive (it does not need to be running yet; watching starts the moment
     it runs). Any Windows program can be watched. As soon as a program is picked, the box at the bottom
     says what App Watcher expects to learn from it: it looks at the program file (which toolkit it uses:
     WinForms, WPF, Infragistics, DevExpress, Delphi, Java, Electron, ...), its config files and ODBC data
     sources and open connections (where its data lives), and picks a matching profile.
   - **2. Where its data lives**: App Watcher scans the usual places for database and data files and shows
     the backends it found. Tick the ones that belong to the app, add a file, a folder, a read-only
     SQL Server or ODBC connection (any engine with a driver on the PC: UniVerse, Pervasive, Progress,
     MySQL, PostgreSQL, Oracle, ...), or an **export drop folder** where nightly CSV/Excel exports of the key
     tables land. **Detect by watching file access** asks for administrator rights once and records, for two
     minutes, which files the app really reads and writes. Nothing is written there, ever.
   - **4. Privacy**: mask quoted parts of titles, keep or skip the pictures, windows never to record
     (Payroll*, *HR*), or an allowlist.
   - **6. Settings password**: choose a password. It is needed to open settings or quit.
   Press **Start watching**. An eye icon appears in the tray. That is all.

Install is per Windows user: run it under the account that uses the app. Scripted installs:
`AppWatcher.exe --setup <exe> <password> [profile]` does the first run without the window.

## What is recorded

Only inside the chosen app, and only while it is the window in front:

- which window or tab document is open (its title) and the control tree of each distinct screen, once
  (the same screen under "Invoice 1023" and "Invoice 1024" is stored once, as "Invoice #"), with the
  labels a person would use (the text next to a field, or its cleaned internal name)
- pictures of the app's window, filed per screen: one for each distinct state (first seen, after each
  press, tab switch, typed value, dialog, suggestion list), never the same state twice, capped per screen
  (never for login or password windows, never for excluded windows; Privacy tab: off, first time only, or every state)
- the suggestion lists that open while typing (autocomplete, lookups): the field, the text typed so far,
  and the suggestions shown
- what a grid shows right after a search, filter or open: its column names and first rows
- the text on each picture, read by the built-in Windows OCR and stored next to the picture, so apps that
  draw their own screens (Qt, Java, old Delphi) still give their labels and column headers (Privacy tab to switch off)
- the keyboard shortcut of each button and menu item, so the twin keeps the power users' keys
- which buttons and menu items were pressed, seen four ways so that apps whose controls stay silent are
  covered too (UI Automation, the older accessibility events, a menu closing after an item was chosen, or a
  button focus followed by a window change), which fields got focus, what was selected or ticked
- the values typed into fields of the app, one event per typed value, not per keystroke
  (so a typed value can be matched to the column it lands in)
- the text of message windows (validation errors, confirmations)
- a read-only before/after copy of the app's data around each save-type action, and the difference
- optionally, the SQL statements the app sends to its database (see "SQL statement capture")

Password fields, and any field whose name contains "password", "pin" or "secret", are recorded as `***`.

## What is never recorded

- other programs, the desktop, the browser, e-mail, chat: nothing outside the chosen app
- anything while another program is in front, even if the app is still open behind it
- the clipboard (the tray can *write* our own folder path to it; it never reads it)
- pictures of the desktop or of other programs; only the watched app's own window, once per screen
- keystrokes outside the app (there is no keyboard hook at all)
- anything over the network: the watcher makes no network calls. The two optional exceptions are the
  AI analyses described below, both off by default; they send only the memory files.

Everything stays in `C:\ProgramData\AppWatcher\`. The tray item **Copy data folder path for AI** puts
that path on the clipboard.

## What App Watcher never does on your screen

- It never opens a file dialog or a message box on its own. Export writes to a fixed folder and tells you
  the path in a short tray notice; learning and snapshots happen silently.
- The only windows it opens are the tray menu, the password-protected settings window, and short tray notices.
- It never presses anything in the watched app: it only listens to events and reads. It cannot cause a
  "save changes?" prompt or any other dialog in the app.

## Profiles

A profile says what to expect from one program or one toolkit: which windows are splash and login
windows, which are message boxes, which buttons mean "save", whether tabs are documents, which
fallbacks to switch on. Twelve are built in (`profiles\*.json`: generic, winforms, wpf, infragistics,
devexpress, component-suite, electron, classic, java, custom-drawn, office, ddi-inform) and the setup
picks one from the program file. **Export these settings as a profile** on tab 3 writes your tuned settings
to `C:\ProgramData\AppWatcher\profiles\`, so the next site with the same program is a one-click setup; put
the file in `profiles\` of this repository to share it.

## How it learns

Once a night (default 02:00, changeable in Settings), one hour after the first start, and whenever you
choose **Learn now** from the tray, the learner reads the new evidence and updates the memory files. No AI,
no network, just rules:

- **Flows**: user actions are grouped into sessions and into flows by the sequence of windows, starting
  after the splash and login windows. For each flow: how often, median duration, the usual path, the
  presses and typed fields on each screen, and the step people most often go back from. The navigation
  graph is a directly-follows graph with frequencies and median durations.
- **Screen to table**: a value typed on a screen that shows up in a changed column becomes a mapping
  "field X fills column Y". After three distinct values it is "seen"; before that it is a "candidate".
  A captured SQL statement makes it "seen" at once and wins over value matching when they disagree.
- **Fields**: per field, how often it was typed into, the shapes of the values (never the values), the
  kind of value (date, money, integer, code, text), which fields are never used, which fields show a
  suggestion list while typing and how big it is, and what grid each action fills (columns, row counts).
- **Relations**: a value written to one table that exists as a key in another is a link (foreign key candidate).
- **Calculated fields**: tried in order until one explains 95% of the rows: a copy of another column, the
  sum of child rows, the product of two columns, a percentage of a column (rates seen in the data plus the
  common tax rates), then a linear fit over up to three columns. Rows that do not fit are listed as exceptions.
- **Rules**: a message window right after a typed value becomes a validation rule (field, kind of value,
  message); after a button or menu it becomes a message of that action. Message windows are not screens.
- **Status flows**: columns with up to twelve values are ordered into a state diagram from the transitions
  seen over time; transitions seen once are "rare", never-seen ones are "not allowed (inferred)".
- **Numbering**: columns that go up by one per new row are sequences.
- **Health**: events per day, presses by how they were seen, the share of saves with a data diff, the share
  of fields mapped, what the collector could not do (tree dumps that failed, no presses, no data source), advice.

Learning is incremental: only sessions newer than the last learn are processed and merged into the
stored counts, so a nightly learn takes seconds whatever the history. **Rebuild memory** in the tray
recomputes everything from day one; the result is the same. Raw events older than the retention window
(default 180 days, tab 3) are deleted once learned; the memory keeps the counts.

Resource use: the watcher is event driven. When the app is not in front it does nothing. Idle memory is
small (about 15 MB working set after start-up). Screen trees are read with one cached UI Automation
call. Before any data read, cheap change signals (file time and size, SQLite `data_version`, SQL Server
change tracking when the DBA enabled it with `scripts\sql\change-tracking-enable.sql`) decide which
sources and tables need re-reading; unchanged ones are copied from the last idle snapshot.

## SQL statement capture (optional, exact)

When the app uses a SQL database, the statements it sends are the most exact evidence there is: an
`INSERT` says precisely which screen writes which table and columns. Setup, once, by a DBA, with the
script for the engine in `scripts\sql\`:

- SQL Server (Express included): `xevents-session.sql` creates an Extended Events session that records only
  that application's completed statements to `.xel` files. Reads nothing from the app's tables, changes no data.
- MySQL/MariaDB: `mysql-general-log.sql`. PostgreSQL: `postgres-log-statement.sql`. Oracle: `oracle-trace.sql`.
  UniVerse/UniData: `universe-logging.md` (three routes: a read-only ODBC login, a nightly export drop folder,
  or server-side logging).

In App Watcher Settings, tab 2, tick **Use SQL statement log** and point it at the folder with the files
(`.xel`, CSV, or any text log with timestamped statements). To stop, the script's last block.

Apps that use ODBC: Settings, tab 2, **Turn on Windows ODBC tracing**. This is a Windows setting for the
current user account: it logs the SQL of every ODBC program under that account to `%TEMP%\SQL.LOG`, slows
them down a little, and the file grows. App Watcher keeps only the lines of the watched program. Turn it
off when done.

## The tray menu

Right-click the eye icon:

| Item | What it does |
|---|---|
| Pause / Resume | stop or continue recording (the icon turns grey while paused) |
| Learn now | update the memory files from the new evidence |
| Rebuild memory | recompute the memory from day one |
| Export for the twin kit | write the bundle (see below) and tell you the path in a tray notice |
| Copy data folder path for AI | puts `C:\ProgramData\AppWatcher\data\<app-name>` on the clipboard |
| Copy last export path | puts the last zip's path on the clipboard |
| Settings... | change the app, data sources, SQL capture, learning, privacy, AI analysis, password (asks the password) |
| Quit | stops App Watcher until the next login (asks the password) |

Running until Quit is the normal state. It starts again at every login.

## Changing the watched app

Open Settings, pick another program on the first tab, save. App Watcher starts a new folder
`data\<new-app-name>\` and leaves the old folder exactly as it is.

## Several PCs

Install App Watcher on each PC that uses the app, then fold the other folders into one with
`appwatcher --merge <folder or zip>` (events are kept per PC, screens are merged by structure, the memory
is rebuilt). health.md then counts users and PCs (names are hashed, never stored).

## Stopping and removing

- Pause from the tray to stop recording for a while.
- Quit from the tray to stop until the next login.
- To remove it completely:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1
```

  The learned data is kept; add `-RemoveData` to delete it too.

## Command line

`AppWatcher.exe` is a windowed program, so a shell does not wait for it. `scripts\appwatcher.cmd` (copied
next to the exe by the installer) does:

```
appwatcher --status | --learn | --rebuild | --export | --expect | --detect <exe> | --merge <folder or zip>
appwatcher --setup <exe> <password> [profile]
```

`--detect` prints the toolkit, the profile and the backends of any program file without setting anything up.

## Handing the result to the App Twin Kit, to a process-mining tool, or to any AI

Choose **Export for the twin kit** from the tray (or `appwatcher --export`). It writes
`data\<app-name>\export\<date>\` and a zip next to it:

- `docs\raw\screens\*.md`: one per distinct screen, with the control tree, the learned menu path and predecessor;
  `docs\raw\screens\shots\*.png`: the one picture per screen
- `docs\raw\tasks\*.md`: one per learned flow, with count, median duration and the actions on each step
- `docs\raw\probes\*.md`: per screen, the fields, the distinct values seen (personal-looking values masked) and the rules learned
- `docs\raw\data-model.md`: tables, columns, relations, backends, snapshot paths
- `docs\raw\memory\*.md`: the six living documents and the changelog
- `events.xes`: a process-mining event log (IEEE XES), one trace per user session, one event per window
  visit or save. Open it in PM4Py, ProM or Disco. `scripts\pm4py_example.py` draws the directly-follows
  graph from it (`pip install pm4py`).

Then: copy `docs\raw` into your `app-twin-kit` folder, open it in Claude Code and type `/mode spec`.
The kit continues from the SPEC step with everything the watcher learned.

Any other AI can read `memory\*.md` directly; they are plain text.

## Analyze with your own AI tool

The deeper reasoning is a portable skill in the open [Agent Skills](https://agentskills.io) format, so the
same files run in Claude Code, OpenCode and OpenClaw without changes:

- `skills\app-watcher-analyze`: the architect review. Reads the memory and the evidence behind weak
  findings, challenges them, and writes `memory\architecture-reviewed.md` (modules, schema proposal,
  rules, open questions, gaps). In this repository `/analyze` in Claude Code is a thin wrapper around it.
- `skills\app-watcher-ask`: answers plain questions from the memory files only ("what did users do
  today", "which screen is slowest", "which fields are never used").

`skills\README.md` has the install lines for each tool (Claude Code: copy into `.claude\skills`;
OpenClaw: `openclaw skills install .\skills\app-watcher-analyze`; OpenCode: its skills folder).
Point the tool at the data folder with **Copy data folder path for AI**. Neither skill opens raw events,
and both are read-only towards the watched app.

**OpenClaw support, as suggested by the client.** OpenClaw follows the Agent Skills specification, loads
skills from its workspace `skills\` folder or the shared `~\.openclaw\skills`, and installs a local skill
folder with `openclaw skills install <path>` (verified on docs.openclaw.ai). `openclaw\workspace-example.md`
explains how to point a workspace at the data folder read-only and install the two skills. If the client's
OpenClaw is connected to a chat tool, the ask skill answers from there too. We did not verify OpenClaw's
chat connectors or access-restriction settings ourselves; follow the OpenClaw documentation for those.

## Nightly analysis without clicking (optional, both off by default)

Settings, tab 5:

- **Claude API**: paste the client's own API key. After each nightly learn, only the `memory\*.md` files
  are sent with the analyst prompt; the answer is saved as `memory\architecture-reviewed.md`.
- **Local model**: an OpenAI-compatible endpoint on this PC or your network (Ollama by default,
  `http://localhost:11434/v1`, plus a model name). Same prompt, same files, nothing leaves the building.

**Show exactly what is sent** displays the full message before you decide. Raw events, typed values and
pictures are never sent by either.

## What we chose not to use

- **Screen recorders** (screenpipe, OpenAdapt): the watcher does not record the screen. It reads control
  trees and data, and keeps one still picture per screen of the watched app's own window, which can be turned off.
- **Binary reverse-engineering tools** (Ghidra, decompilers, "REA"-style analyzers): the observer never
  opens the app's program code. Everything comes from the outside: the accessibility tree, the data, the SQL.
  (The toolkit detection only reads the names of the libraries the program file references.)
- **Global keyloggers or keyboard hooks**: for the same privacy reason. Values are read from the field that
  changed inside the watched app, through UI Automation, and only while that app is in front.

## Tell your users

Something like this can go on the notice board or in an e-mail:

> We have installed a small program called App Watcher on this PC. It watches only **<app name>** while
> you work in it, and writes down which screens, buttons and fields are used and what the program saves,
> so that we can document and later rebuild it. It keeps one picture of each screen of that program. It
> does not record other programs, e-mails, the web, the clipboard or the desktop, and it does not send
> anything over the internet. Passwords are never recorded. You will see a small eye icon near the clock;
> it never shows pop-ups and does not need anything from you. Questions: ask <contact>.

## Honest limits

- Apps that draw their own screens (some Qt, Java without the Access Bridge, or custom programs) show few
  or no controls. The setup window says so before you start, and health.md after the first hour; the watcher
  still learns windows, timing, dialog text and the data side (SQL capture, drop folder).
- Encrypted or server-only data cannot be read unless you give a read-only connection, an export drop folder,
  or SQL capture.
- Presses seen through the fallbacks (menus, inference) are a lower bound, and an inferred press can be wrong
  now and then; health.md shows how many came which way.
- The learner finds simple formulas and plain rules. Anything cleverer is for the analyze skill and for people.
- A rule is only as sure as its count. One observation is a hint, not a fact.

## For developers

C# on .NET 8, WinForms for the tray and settings, FlaUI (UI Automation) for the collector with MSAA
(IAccessible) fallbacks, Microsoft.Data.Sqlite / OleDb / SqlClient / Odbc as read-only readers, XELite for
Extended Events files, TraceEvent for the one-time file access detection. `dotnet build`, `dotnet test`
(51 tests), `scripts\live-test.ps1` (drives `tests\SampleApp` by UI Automation and checks what the watcher
recorded), `scripts\publish.ps1`. See `CLAUDE.md` for the folder layout and the rules the code follows,
`docs\data-folder.md` for the file formats, and `docs\improvements.md` for what changed after the first real run.

## License

MIT.
