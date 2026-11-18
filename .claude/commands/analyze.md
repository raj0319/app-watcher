---
description: Read the App Watcher memory and evidence for one watched app, challenge weak findings, write memory/architecture-reviewed.md
argument-hint: [path to data/<app-name>, optional]
---

Act as the hidden architect for the watched app. Follow CLAUDE.md in this repository exactly.

1. Find the app data folder:
   - If `$ARGUMENTS` is a path, use it.
   - Else if the current folder contains `memory/` and `events/`, use the current folder.
   - Else look in `%ProgramData%\AppWatcher\data\` (or `AppWatcherData\data\` next to the exe for portable installs) and, if there is exactly one app folder, use it; if there are several, list them and ask which one.
2. If `memory/` is empty or older than the newest file in `events/`, run `AppWatcher.exe --learn` first (it is in `%LOCALAPPDATA%\AppWatcher\` after install, or in `publish\`). If the exe is not found, say so and continue with what is in `memory/`.
3. Read, in this order: `memory/architecture.md`, `memory/data-model.md`, `memory/screens.md`, `memory/rules.md`, `memory/flows.md`, `memory/changelog.md`.
4. For every line tagged `inferred` or `seen 1×` that matters for the schema or the rules, open the evidence it points to (`effects/*.json`, `events/*.jsonl#L<n>`, `screens/*.md`, `snapshots/before.json`) and check it. Use subagents for big files; you only need the conclusion. Never open or change anything outside this data folder, and never write to the watched app's data files or database.
5. Write `memory/architecture-reviewed.md` using the analyst prompt in `prompts/analyst-prompt.md` (the same prompt the optional API analysis uses) with these sections: What this application is; Modules; Schema proposal; Rules; Challenged findings; Open questions; Gaps in the evidence. Keep every confidence tag you rely on, and add "reviewed: confirmed / doubted / rejected" next to each finding you checked.
6. Reply to the user in at most five plain lines: where the file is, how many findings you confirmed, doubted and rejected, and the single most useful thing the client could do next (for example "watch the app for another week" or "add the SQL Server connection in Settings > Data").
