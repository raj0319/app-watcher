# App Watcher - instructions for Claude Code

This repository holds App Watcher, a small Windows tray program that watches ONE chosen desktop
app and learns the logic behind it (fields, tables, rules, flows). Read this file once, then follow it.
Keep replies short and in plain words; the user is usually not a developer.

## What is where

```
src/AppWatcher.Core/     the engine: config, event store, snapshots and diff, data readers (SQLite, Access, SQL Server, ODBC, export drop folder),
                         SQL log readers, discovery (toolkit, backends, profiles), learner (incremental), export (twin kit + XES), merge, retention, API clients
src/AppWatcher/          the tray app: WinForms tray icon, three-step first-run wizard, settings window (Simple/Advanced), UI Automation collector (with MSAA, menu and press-inference fallbacks),
                         window capture (one picture per screen), ETW file-access trace
src/AppWatcher.Driver/   the scenario driver: runs scenarios/*.json on the original program with real input (test environments only, typed
                         confirmation, acts only while the target is in front); the one program here that clicks. Writes run.jsonl for --verify
scenarios/               scenario files (sample-erp.json drives tests/SampleApp); the format is src/AppWatcher.Core/Verification/Scenario.cs
profiles/                app profiles (JSON, embedded): generic, winforms, wpf, infragistics, devexpress, component-suite, electron, classic, java, custom-drawn, office, ddi-inform
tests/AppWatcher.Tests/  xunit tests with synthetic evidence (dotnet test); Fixtures/inform, inform-v0.8, inform-v0.9 and inform-v0.10 hold anonymised copies of four real sessions (see their READMEs; inform-v0.9/clicks.json holds every truth click with its point)
tests/SampleApp/         a tiny WinForms "ERP" (login, tabs, menu, dialog, message windows) for the live test
scripts/                 publish.ps1, install.ps1, uninstall.ps1, appwatcher.cmd (CLI wrapper), live-test.ps1, scenario-run.ps1 (driver + scorecard on the sample app), check_skills.py, pm4py_example.py, sql/*.sql (DBA scripts)
skills/                  portable Agent Skills: app-watcher-analyze (the architect review), app-watcher-ask (questions from memory)
openclaw/                how to use the skills from OpenClaw
prompts/analyst-prompt.md  the architect prompt shared by the analyze skill and the optional API/local analysis
.claude/commands/analyze.md  thin wrapper that invokes skills/app-watcher-analyze
docs/                    notes for people (data-folder.md: file formats; demo-test.md: 30-minute test; improvements.md: what changed after each real run and why; test-v0.8-inform.md: the anonymised scorecard of the second measured run;
                         CURRENT_ARCHITECTURE.md, FEATURE_GAP_ANALYSIS.md, CAPTURE_TECHNOLOGY_COMPARISON.md, IMPLEMENTATION_ROADMAP.md: the audit against the "Ultimate Edition" plan and its milestones)
```

Build: `dotnet build` (needs the .NET 8 SDK). Tests: `dotnet test`. One-file exe: `scripts\publish.ps1`.
Live test against real windows: `dotnet build tests\SampleApp` then `scripts\live-test.ps1` (starts the
watcher in portable mode on the sample app, drives it by UI Automation patterns, learns, prints the result;
kills only the two processes it started).

The exe also has headless commands: `AppWatcher.exe --learn` (incremental), `--rebuild` (from day one),
`--export`, `--payload`, `--status`, `--expect`, `--detect <exe>`, `--setup <exe> <password> [profile]`,
`--merge <folder or zip>`, `--verify <scenario.json> <run.jsonl>` (scorecard of a driven run into `verification/`),
`--trace-files <pid> <seconds> <out.json>` (admin), and the hidden `--render-ui <dir>`
(pictures of the wizard and Settings for the docs, with a made-up program, never this PC's). Add `--portable` when the
data lives in `AppWatcherData\` next to the exe. The exe is a windowed program, so from a shell use
`scripts\appwatcher.cmd <command>` (start /wait) to get its output and exit code.

## The data folder you analyze

On a client PC the watcher writes to `%ProgramData%\AppWatcher\data\<app-name>\`:

```
events/<yyyy-MM-dd>.jsonl   one line per user action in the watched app (values typed; passwords masked; "how" says how a press was seen)
screens/<title>.md          the control tree of each window or tab document, once, in the App Twin Kit screen format (kind: screen | tab | session | dialog)
screens/shots/<screen>/     one picture per state of that screen (the app's window only; off in Settings, Simple)
screens/index.json          structure hash -> screen file, kind, every title seen for that screen
effects/<timestamp>.json    what changed in the data right after a save-type action (rows added/removed/changed per table, files changed)
snapshots/before.json       the latest read-only snapshot of the app's data (tables, rows)
collector-status.json       what the collector managed: dumps ok/failed, presses by path, dialogs, tab screens
expectations.md             what can be learned from this app, in plain words
memory/                     the living architecture written by the learner:
  data-model.md  screens.md  rules.md  flows.md  architecture.md  health.md  logic.md  logic.json  changelog.md
  learn-state.json          the learner's accumulated counters and watermark (incremental learning)
  architecture-reviewed.md  written by the analyze skill or by the optional API/local analysis
export/<date>/              bundles in the App Twin Kit format plus events.xes and blueprint/ (ids, statuses, evidence), zipped
```

Every line in `memory/*.md` ends with a confidence (`seen N×` = observed that many times, `inferred` =
guessed from structure) and the evidence file to open. The memory files never contain raw values typed by users.

## /analyze (the deeper reasoning step)

The user opens either this repo or a data folder in Claude Code and types `/analyze [path]`. The command
is a thin wrapper around `skills/app-watcher-analyze/SKILL.md`, which has the steps. In short: read
`memory/`, open the evidence behind every `inferred`, `candidate` and `seen 1×` finding that matters,
challenge it, and write `memory/architecture-reviewed.md` with: what the app is, modules, schema proposal,
rules, challenged findings, open questions, gaps in the evidence. The same skill runs unchanged in OpenCode
and OpenClaw; `skills/app-watcher-ask` answers plain questions from the memory files.

## Rules you always follow

- **Read only towards the watched app.** Never open the watched app's database or files for writing, never
  run SQL that changes data, never touch the app's install folder. `snapshots/before.json` is your copy of the data.
- **Stay inside the data folder** of the app being analyzed (plus this repo). Do not read other folders on
  the client's PC.
- **No network from the analysis step.** The optional Claude API call is the tray app's job and is off by default.
- **Keep confidence honest.** Copy the `seen N×` / `inferred` tags onto every claim you make; add
  `reviewed: confirmed / doubted / rejected` for the ones you checked.
- **Use subagents for big reads** (large `effects/*.json`, long `events/*.jsonl`, `snapshots/before.json`)
  with a narrow brief, so only the conclusion enters your context.
- **Short answers.** When done, say in at most five lines where the file is and what the client should do next.

## Changing the code

- The export must stay compatible with App Twin Kit: `docs/raw/screens/*.md`, `tasks/*.md`,
  `probes/*.md`, `data-model.md`, `memory/*.md`. The screen table columns are
  `| Depth | Type | Name | AutomationId | Position |`, same as the kit's `scripts/ui_tree.py`.
  The diff text format is the one printed by the kit's `scripts/snapshot_diff.py diff`.
- The collector never installs a keyboard or mouse hook, never records while another program is in front, and never
  invokes a control of the watched app. Hover tips are read once when the program shows one (the control under the
  pointer at that moment), and only for buttons, menu items and fields, never grid or list tips, which show data. Keep it that way. Pictures: one per distinct screen, of the app's
  window only (PrintWindow), never the desktop, never a login or excluded window; the setting is in Settings, Simple.
- The app never shows a file dialog or a message box on its own: the three-step first-run wizard, the tray
  menu, the password-protected settings window (whose program picker and data-source pickers the user opens
  on purpose) and short balloon notices are the only UI. Notices say in one sentence what happened and what
  to do next, never a path alone. Setup decides for the user: every default is the fullest safe, local
  choice (`SetupDefaults`), and the same defaults serve the wizard, Settings and `--setup`.
  Validation messages go to the settings window's status line. Export writes to a fixed path.
- Dialogs are decided by structure (`DialogDetector`), never by a title pattern alone; a profile's `dialogWindows`
  pattern only helps when the structure allows it (`CouldBeNamedDialog`).
- Click and press decisions live in `ClickTracker` (Core, pure): the collector feeds it one `InputSample` per event and per
  50 ms read of the button (its own thread, only while the program is in front), and the fixture replay feeds it the live order
  of a recorded run. A click is the button seen down (bit 15, or bit 0 for a very fast click) and then up at a sample; its point
  is where the pointer was when the button was first seen down. Without a click nothing is pressed (no hovered button, no
  resting pointer, no control under the pointer when a window changes); the focused button is credited only for Enter or Space
  pressed while it already had the focus. A tree hit and a live read that agree on a button are a press at once (Add Line
  Item changes nothing to compare). Any click re-reads the window under it. Trees are kept per window handle; a document with
  no tree of its own is never resolved on its top window's tree; the newest on-screen read is the click surface, the tree
  recorded at open stays the label source. Pointer-move WinEvents are not used (they arrive late when the watcher is busy).
- Typed or filled: a changed field is the user's only when a character key went down after the keyboard focus arrived in it
  (arrival from the sampled GUI-thread focus, `GetGUIThreadInfo`), or a click landed on a drop-down or tick box
  (`FieldReads.UserChanged`); a re-read that replaces what the field's own events reported is the program's
  (`FieldReads.IsReplacement`); in a record load (three or more fields filled at once) text longer than the keys pressed is
  the program's (`LongerThanKeys`). Which key went down is never kept, only that one did and how many.
- Presses on toolkits whose buttons announce nothing come from the control under the pointer, read with
  ElementFromPoint at once when the left button went down (`GetAsyncKeyState` sampled at each event the collector
  already receives: a read, never a hook) or when a PRESSED state or mouse capture says so (a state change on hover
  remembers nothing), or from the focused button; counted when a window, document, tab or message changes or the
  program fills a value within 1.5 s (`PressDecider`, pure and tested). When the button is gone by the time the click is
  heard of (Exit, OK, Cancel: the next event is the destroy), the window's last recorded tree is the hit-test surface
  (`TreeHitTest`, positions in screen pixels); the trees are tried in the order `ClickSurfaces.Resolve` gives (a window
  going away first, then the window really under the point, by WindowFromPoint), and a hit on a tab item is a page selection. A click while a menu is open or just closed belongs to the menu (its item is counted when
  the menu closes), never to the control under the popup. Nothing is remembered from a state change (Infragistics raises
  it on hover). Documents of an MDI workspace are the child
  windows of the MDICLIENT pane (`MdiWorkspace`): the one in front by z-order is the screen, read from its own window;
  a `Tab` control inside a document is never the workspace strip; where a strip is exposed it is read after events
  (every 2 s at most), never through the selection pattern on WinForms. Inner tab pages are read with the document after
  clicks and focus moves. Values the program fills in come from the field's own change event or from one re-read of the
  document 300 ms after a typed value, press, Enter, Tab or focus move (`FieldReads.Diff`: the cell that had the focus
  is the typed value). Keep all of that read-only.
- When the program is too busy to answer WM_GETTEXT for the text box the keys went into (it builds a lookup list), that box's
  own picture is copied (BitBlt of the box alone, `BoxPicture`, never saved, never when another window lies over it, never a
  password box) and read with the Windows OCR; the text is kept only when the keys pressed could have written it
  (`FieldReads.PixelTextFits`), and look-alike characters (O/0, I/1) are settled by the value the program shows later
  (`FieldReads.ReconcilePixelText`). It goes to events as a typed value; memory still holds only its shape.
- Text read from pictures reaches memory only as a field or column name the screen record knows (`OcrLogic.KnownFields`);
  personal fields the program fills (phone, e-mail, address, name) are shapes in the events; the profile's `titleMasks`
  hide the company name in the main title. A window the program opens by itself (no click in the 2 s before) is not a
  flow visit.
- Evidence folders copied from a client PC (`app-watcher-v*-evidence-*/`, `evidence/`) are ignored by git and must
  stay out of it; anything that goes into `tests/` is anonymised first (`scripts\check-fixtures.ps1 -Forbidden <list kept outside the repo>`).
- When testing the collector against any app: start your own instance (tests/SampleApp is there for that),
  drive only that window by handle, never press Save or close with unsaved text (no "save changes?" prompt
  on the user's desktop), and end the test by killing the process you started. Driving a WinForms
  TabControl: UIA property conditions evaluated on it can flip or hide tab items; walk the tree with a true
  condition and filter yourself, or switch tabs through a menu.
- Memory files must never contain raw typed values; use shapes (`Masking.Shape`) and patterns.
- `src/AppWatcher.Driver` is the only program in the repository that clicks and types, and it never does so in the watcher's
  name: it refuses to run without `--test-environment`, asks for the word DRIVE unless `--yes` is given, acts only while a
  window of the target process is in front, and blocks a press on Save, Post, Delete, Send and the like unless the step says
  `confirm: true`. The scorer (`ScenarioScorer`) is pure and tested on the anonymised rerun log of 2026-11-19, so a change
  to the scoring rules must keep that run's numbers (open 4/4, Exit 1/4, prompts 0/2, sub-tabs 1/17, 5 unexplained presses).
- The learner is incremental: every per-session heuristic accumulates into `LearnState` (additive
  counters); snapshot-based findings are recomputed each learn. A learn run twice must give identical memory,
  and an incremental run must equal a full rebuild (tests cover both). Keep new heuristics additive.
- Keep idle memory under 60 MB and CPU near zero when the watched app is not in front. No polling loops
  faster than a few seconds, with one exception: while the watched program is in front, the button read every 50 ms
  (one `GetAsyncKeyState` call; every fourth read also Enter, Tab and whether a character key went down). It stops when
  the program leaves the front. Everything else is event driven; a document read that took over 2 s waits 15 s before the
  next one unless a click or key asks.
- Add a test in `tests/` for every new heuristic, with synthetic data; run `scripts\live-test.ps1` after
  touching the collector.
