# App Watcher - instructions for Claude Code

This repository holds App Watcher, a small Windows tray program that watches ONE chosen desktop
app and learns the logic behind it (fields, tables, rules, flows). Read this file once, then follow it.
Keep replies short and in plain words; the user is usually not a developer.

## What is where

```
src/AppWatcher.Core/     the engine: config, event store, snapshots and diff, data readers, SQL log readers, learner (incremental), export (twin kit + XES), API clients
src/AppWatcher/          the tray app: WinForms tray icon, settings window, UI Automation collector, ETW file-access trace
tests/AppWatcher.Tests/  xunit tests with synthetic evidence (dotnet test)
scripts/                 publish.ps1, install.ps1, uninstall.ps1, check_skills.py, pm4py_example.py, sql/*.sql (DBA scripts)
skills/                  portable Agent Skills: app-watcher-analyze (the architect review), app-watcher-ask (questions from memory)
openclaw/                how to use the skills from OpenClaw
prompts/analyst-prompt.md  the architect prompt shared by the analyze skill and the optional API/local analysis
.claude/commands/analyze.md  thin wrapper that invokes skills/app-watcher-analyze
docs/                    notes for people (data-folder.md: file formats)
```

Build: `dotnet build` (needs the .NET 8 SDK). Tests: `dotnet test`. One-file exe: `scripts\publish.ps1`.
The exe also has headless commands: `AppWatcher.exe --learn` (incremental), `--rebuild` (from day one),
`--export`, `--payload`, `--status`, `--trace-files <pid> <seconds> <out.json>` (admin)
(add `--portable` when the data lives in `AppWatcherData\` next to the exe).

## The data folder you analyze

On a client PC the watcher writes to `%ProgramData%\AppWatcher\data\<app-name>\`:

```
events/<yyyy-MM-dd>.jsonl   one line per user action in the watched app (values typed; passwords masked)
screens/<title>.md          the control tree of each window, once, in the App Twin Kit screen format
effects/<timestamp>.json    what changed in the data right after a save-type action (rows added/removed/changed per table, files changed)
snapshots/before.json       the latest read-only snapshot of the app's data (tables, rows)
memory/                     the living architecture written by the learner:
  data-model.md  screens.md  rules.md  flows.md  architecture.md  health.md  changelog.md
  learn-state.json          the learner's accumulated counters and watermark (incremental learning)
  architecture-reviewed.md  written by the analyze skill or by the optional API/local analysis
screens/index.json          structure hash -> screen file, with every title seen for that screen
export/<date>/              bundles in the App Twin Kit format plus events.xes, zipped
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
- The collector never installs a keyboard hook, never takes screenshots, never records while another
  program is in front, and never invokes a control of the watched app. Keep it that way.
- The app never shows a file dialog or a message box on its own: the tray menu, the password-protected
  settings window (whose program picker and data-source pickers the user opens on purpose with a
  Browse button) and short balloon notices are the only UI.
  Validation messages go to the settings window's status line. Export writes to a fixed path.
- When testing the collector against any app: start your own instance, drive only that window by handle,
  never press Save or close with unsaved text (no "save changes?" prompt on the user's desktop), and end
  the test by killing the process you started.
- Memory files must never contain raw typed values; use shapes (`Masking.Shape`) and patterns.
- The learner is incremental: every per-session heuristic accumulates into `LearnState` (additive
  counters); snapshot-based findings are recomputed each learn. A learn run twice must give identical memory,
  and an incremental run must equal a full rebuild (tests cover both). Keep new heuristics additive.
- Keep idle memory under 60 MB and CPU near zero when the watched app is not in front. No polling loops
  faster than a few seconds; everything is event driven.
- Add a test in `tests/` for every new heuristic, with synthetic data.
