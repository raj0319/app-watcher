---
name: app-watcher-ask
description: Answers plain-language questions about an application watched by App Watcher, from its memory files only (what users did today, which screen is slowest, which fields are never used, how a flow goes, what changed since last week). Use when someone asks about the watched app's screens, flows, fields, tables, rules or recent changes, including over chat tools such as WhatsApp, Telegram or Slack.
license: MIT
compatibility: Works in Claude Code, OpenCode and OpenClaw. Needs read access to the App Watcher data folder (%ProgramData%\AppWatcher\data\<app-name>). No network, no write access needed.
metadata:
  author: app-watcher
  version: "1.0"
---

# App Watcher: ask the observer

Answer questions about the watched application using only what App Watcher has already learned.

## What you may read

- `memory/*.md` in the app's data folder: `architecture.md`, `data-model.md`, `screens.md`, `rules.md`,
  `flows.md`, `logic.md` (what fills what, calculations, what buttons lead to, open questions), `health.md`, `changelog.md`, and `architecture-reviewed.md` when it exists.
- The newest `export/<date>/docs/raw/` folder (screens, tasks, probes, data-model) when a question needs
  per-screen detail the memory files do not have.

Never open `events/`, `effects/` or `snapshots/`: they hold raw user activity and data. If a question can
only be answered from those, say that the observer's memory does not hold it and suggest running a learn
(`AppWatcher.exe --learn`) or the `app-watcher-analyze` skill.

## How to answer

- Plain words, short. Keep the confidence tag of every fact you use (`seen 12×`, `candidate`, `inferred`).
- Name the file the fact came from, once, at the end ("from memory/flows.md").
- Numbers only when they change the answer (counts, durations, percentages).
- If the memory is older than a day, say when it was last learned (the "Last learn:" line).
- Never print values typed by users; the memory files do not contain them, and you must not look for them.

## Finding the data folder

Use the path configured for the workspace or given in the question. Otherwise look under
`%ProgramData%\AppWatcher\data\` (or `AppWatcherData\data\` next to `AppWatcher.exe`). If several app
folders exist and the question does not name one, ask which app.

## Example questions and answer shapes

1. **"What did users do in the app today?"**
   Read `flows.md` (user flows table) and `changelog.md`. Answer: the three most frequent flows with their
   counts and median durations, and one line on what changed in the memory since the last learn.
   Example shape: "Mostly New Order (14×, about 2 min each) and Customer lookup (9×). Since yesterday the
   learner added one status transition: open → cancelled (seen 2×). From memory/flows.md, changelog.md."

2. **"Which screen is slowest?"**
   Read the user flows table in `flows.md`. Answer: the flow with the highest median duration, its count,
   and the step users most often go back from, as a hint why.
   Example shape: "Invoice # (median 6.5 min over 11 flows). Users most often go back from Pick Item
   (7×), so that step is where time goes. seen 11×, from memory/flows.md."

3. **"Which fields are never used?"**
   Read `screens.md`: fields listed per screen with "(not mapped yet)" and no typed values are the
   candidates; cross-check the probes file of that screen in the latest export when present.
   Answer: per screen, the fields with no observed input, and the caveat that "never used" means
   "never seen used in N sessions".
   Example shape: "On Customer: Fax, Region, Credit limit had no input in 23 sessions (inferred). From
   memory/screens.md."

4. **"Show me the invoice flow."**
   Read the flow section in `flows.md` whose name matches, and the status flow of the matching table.
   Answer: the common path as an arrow line, the controls used on each step, and the status states in
   order with the transitions seen.
   Example shape: "Main → Invoice # → Pick Item → Invoice # (seen 11×). Steps: Add line (31), Save (11).
   Status: draft → posted (seen 9×), posted → paid (seen 4×); draft → paid never seen. From memory/flows.md."

5. **"What changed since last week?"**
   Read `changelog.md` entries newer than 7 days. Answer: new tables, mappings, rules or transitions
   added, and anything removed, grouped by file, with counts.
   Example shape: "3 learns. New: field Email → customers.email (seen 4×), rule 'Email: mixed text →
   Please enter a valid e-mail'. Removed: nothing. From memory/changelog.md."

6. **"Which tables does the Order screen write?"**
   Read the screen's section in `screens.md` ("Tables written"). Answer: the tables with always / sometimes
   and the save counts, plus mapped fields.
   Example shape: "Order writes orders (always, 14 of 14 saves) and order_lines (always), audit_log
   (sometimes, 3 of 14). 6 of 9 fields mapped. From memory/screens.md."

If the question is not about the watched app, say that this skill only knows the App Watcher memory.
