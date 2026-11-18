# App Watcher - instructions for Claude Code

This repository holds App Watcher, a small Windows tray program that watches ONE chosen desktop
app and learns the logic behind it (fields, tables, rules, flows). Read this file once, then follow it.
Keep replies short and in plain words; the user is usually not a developer.

## What is where

```
src/AppWatcher.Core/     the engine: config, event store, snapshots and diff, data readers, learner, export, API client
src/AppWatcher/          the tray app: WinForms tray icon, settings window, UI Automation collector
tests/AppWatcher.Tests/  xunit tests with synthetic evidence (dotnet test)
scripts/                 publish.ps1 (build one exe), install.ps1 (start at login), uninstall.ps1
prompts/analyst-prompt.md  the architect prompt shared by /analyze and the optional API analysis
.claude/commands/analyze.md  the /analyze command
docs/                    notes for people
```

Build: `dotnet build` (needs the .NET 8 SDK). Tests: `dotnet test`. One-file exe: `scripts\publish.ps1`.
The exe also has headless commands: `AppWatcher.exe --learn`, `--export`, `--payload`, `--status`
(add `--portable` when the data lives in `AppWatcherData\` next to the exe).

## The data folder you analyze

On a client PC the watcher writes to `%ProgramData%\AppWatcher\data\<app-name>\`:

```
events/<yyyy-MM-dd>.jsonl   one line per user action in the watched app (values typed; passwords masked)
screens/<title>.md          the control tree of each window, once, in the App Twin Kit screen format
effects/<timestamp>.json    what changed in the data right after a save-type action (rows added/removed/changed per table, files changed)
snapshots/before.json       the latest read-only snapshot of the app's data (tables, rows)
memory/                     the living architecture written by the learner:
  data-model.md  screens.md  rules.md  flows.md  architecture.md  changelog.md
  architecture-reviewed.md  written by /analyze or by the optional API analysis
export/<date>/              bundles in the App Twin Kit format, zipped
```

Every line in `memory/*.md` ends with a confidence (`seen N×` = observed that many times, `inferred` =
guessed from structure) and the evidence file to open. The memory files never contain raw values typed by users.

## /analyze (the deeper reasoning step)

The user opens either this repo or a data folder in Claude Code and types `/analyze [path]`. The command
file `.claude/commands/analyze.md` has the steps. In short: read `memory/`, open the evidence behind every
`inferred` and `seen 1×` finding that matters, challenge it, and write `memory/architecture-reviewed.md`
with: what the app is, modules, schema proposal, rules, challenged findings, open questions, gaps in the
evidence. Use the prompt in `prompts/analyst-prompt.md` as your brief.

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
  program is in front. Keep it that way.
- Memory files must never contain raw typed values; use shapes (`Masking.Shape`) and patterns.
- Keep idle memory under 60 MB and CPU near zero when the watched app is not in front. No polling loops
  faster than a few seconds; everything is event driven.
- Add a test in `tests/` for every new heuristic, with synthetic data.
