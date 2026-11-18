---
name: app-watcher-analyze
description: Reads an App Watcher data folder (memory/*.md plus the evidence behind weak findings), challenges low-confidence findings, and writes memory/architecture-reviewed.md with modules, a schema proposal, rules, open questions and evidence gaps. Use when asked to analyze, review or reverse-engineer an application watched by App Watcher, or when the user types /analyze.
license: MIT
compatibility: Works in Claude Code, OpenCode and OpenClaw. Needs read access to the App Watcher data folder (%ProgramData%\AppWatcher\data\<app-name>) and write access to its memory/ subfolder. No network needed.
metadata:
  author: app-watcher
  version: "1.0"
---

# App Watcher: architect review

You are the hidden architect behind a Windows desktop application that was observed, not decompiled.
App Watcher recorded what real users did and what changed in the app's data, and a rule-based learner
wrote that into `memory/*.md`. Every line there carries a confidence tag (`seen N×` = cause and effect
recorded together N times; `candidate` = fewer than 3 distinct observations; `inferred` = suggested by
structure, never proved) and names its evidence file.

## Steps

1. **Find the data folder.** Use the path the user gave. Otherwise, if the current folder has `memory/`
   and `events/`, use it. Otherwise look under `%ProgramData%\AppWatcher\data\` (or `AppWatcherData\data\`
   next to `AppWatcher.exe` for portable installs). One app folder: use it. Several: list them and ask.
2. **Refresh if stale.** If `memory/` is empty or older than the newest file in `events/`, run
   `AppWatcher.exe --learn` (it lives in `%LOCALAPPDATA%\AppWatcher\` after install, or in `publish\`).
   If the exe is not found, say so once and continue with what is there.
3. **Read, in this order:** `memory/architecture.md`, `memory/data-model.md`, `memory/screens.md`,
   `memory/rules.md`, `memory/flows.md`, `memory/health.md`, `memory/changelog.md`.
4. **Check the weak lines.** For every finding tagged `inferred`, `candidate` or `seen 1×` that matters
   for the schema or the rules, open the evidence it points to (`effects/*.json`, `events/*.jsonl#L<n>`,
   `screens/*.md`, `snapshots/before.json`) and decide: confirmed, doubted or rejected. Big files: read
   only the lines you need; delegate to a helper if your tool supports it and keep only the conclusion.
5. **Write `memory/architecture-reviewed.md`** with exactly these sections:
   1. What this application is (two or three plain sentences)
   2. Modules (screens and tables grouped by what they do for the user; one line on how they connect)
   3. Schema proposal (tables, columns, keys, relations; each marked seen / inferred / proposed)
   4. Rules (validation, calculated columns with formula, sequences, status flows with allowed transitions)
   5. Challenged findings (what you doubt, why, and the one observation that would settle it)
   6. Open questions (what a developer must ask the users before building a twin)
   7. Gaps in the evidence (what the watcher cannot see and what the client could do about it)
   Copy the confidence tag onto every claim you keep and add `reviewed: confirmed | doubted | rejected`
   next to each finding you checked.
6. **Reply in at most five plain lines:** where the file is, how many findings you confirmed, doubted
   and rejected, and the single most useful next step for the client (for example "turn on SQL capture"
   or "watch for another week").

## Rules

- Read only. Never open the watched app's database or files for writing, never run SQL that changes
  data, never touch the app's install folder. `snapshots/before.json` is your copy of the data.
- Stay inside the App Watcher data folder (and this skill's folder). Do not read other folders.
- Do not print or request raw values typed by users. Names, shapes and counts are enough.
- Work only from the evidence. You may propose tables or rules that are not there, marked "proposed".
- Keep each section under about 60 lines; prefer short tables and bullets.

## Reference

The full analyst prompt the App Watcher app uses for its optional API analysis is in
`references/analyst-prompt.md`; it says the same thing as the steps above and may be pasted into any AI.
