# Feature gap analysis against the "App Watcher Ultimate Edition" plan (2026-11-20)

Each phase of the plan, what exists today (in code, verified), what is partial, and what does not exist. "Exists" means
there is code with a test or a measured run behind it; "claimed" means a document says so and the code does not bear it out.

## Phase 1: audit
Done: this document, `CURRENT_ARCHITECTURE.md`, `IMPLEMENTATION_ROADMAP.md`.

## Phase 2: unified Windows observation engine

| Requirement | App Watcher | Analyzer | Gap |
|---|---|---|---|
| Automatic target detection | setup from the exe (toolkit, backends, profile), watches one process by pid | title substring | none worth closing |
| UIA hierarchy extraction | yes, cached walks with four fallbacks, MDI documents from their own window | yes, flat polling dump | none |
| FlaUI and MSAA fallback | yes | win32 backend | none |
| OCR for inaccessible controls | yes, per state picture, used by the learner | no | none (analyzer side only) |
| Screen and window identification | structure hash, title patterns, kinds (screen/tab/dialog/session/menu) | signature hash of names and types | none |
| Field names, types, values, properties | labels, types, read-only, password, values on re-reads, typed values | names and types only (values deliberately not read) | none |
| Button, menu, tab relationships | presses (five paths), menus with items, inner tab pages, leads ("Exit opens Close?") | buttons listed; no presses | none |
| Navigation transitions | flows, navigation graph, XES | consecutive signature changes ("trigger unknown") | none |
| Continuous background observation | tray app, event driven, idle CPU near zero | foreground console loop, 1.5 s polling | none |
| Incremental evidence, dedup, session recovery | incremental learner (state file, watermark), screens recorded once, pictures deduplicated | pages deduplicated by signature | session recovery of a crashed learn is not tested on either side |
| Sensitive-data redaction | passwords, personal fields, title masks, excluded windows, anonymisation check for fixtures | reads no values; screenshots unredacted | none on App Watcher's side |
| No global keyboard monitoring | rule, enforced by review | no hooks either | none |

Verdict: Phase 2 is App Watcher as it stands. The analyzer adds nothing here. What is still open is on the target itself:
the next driven run of v0.10 on the test PC (sub-tab clicks without a recorded rectangle, the live read at the next event).

## Phase 3: autonomous exploration

| Requirement | Exists | Gap |
|---|---|---|
| Discover reachable screens | App Watcher: only what people open; analyzer: one allow-listed invoke per run | no crawler anywhere |
| Identify navigation controls | analyzer `plan`: regex on names (never clicks); App Watcher: buttons and menus with what they led to when pressed | no model of "unexplored" |
| Persistent navigation graph | App Watcher: `flows.md` graph from use, `events.xes`; analyzer: `screen_graph.json` | one graph, not two |
| Unexplored paths, exploration plans, resume, coverage, loop prevention | analyzer: `attempted` set in memory, `max-actions`, stops on repeat signature; no persistence, no coverage, no resume | all missing |
| Dialog and error detection | App Watcher: dialogs by shape with their text | none in the analyzer | none |
| Approval for data-changing actions, isolated test environments | analyzer: typed word + block-list regex on button names | a name regex is not a safety boundary; no environment detection |

Design conflict to decide first: App Watcher's rule "never invoke a control of the watched app" is what makes it safe on a
user's PC. An explorer that clicks must be a separate tool that runs only on a test environment, reusing the collector to
record what it causes. Microsoft UFO (an agent that drives Windows apps with a model) could be that tool; it is not integrated
anywhere today, and evaluating it is a task of its own.

## Phase 4: business-rule discovery

| Requirement | App Watcher | Analyzer |
|---|---|---|
| Field dependencies | "typing X fills Y" with seen/of counts | placeholders (`dependent_on: []`, `status: not_verified`) |
| Required fields, conditional visibility | not learned (no evidence source yet) | placeholders |
| Validation constraints | message after a typed value, with the value's pattern | placeholders |
| Default values | not learned | no |
| Arithmetic, tax, discount, rounding | formulas over screen numbers and over data diffs, evidence rules (three distinct values, no alternative for cells and pictures) | candidates from ≥ 3 hand-typed observations |
| Status transitions, numbering | from data snapshots (`StatusFlowLearner`, `SequenceLearner`) | no |
| Save and update behaviour | effects (rows added/changed) around save-type presses, SQL capture | no |
| Cross-screen dependencies | leads and flows | no |
| Controlled input/output experiments | no (App Watcher never types) | manual `probe` entry |
| Rule record with id, inputs, outputs, evidence, confidence, contradictions, verification status, regression cases | `logic.json` has screen, field, formula, fits/tested, alternatives, evidence files, `source` (tree/ocr); no rule ids, no verification status, no regression cases | `formula_hypotheses.json` has candidates and observations; everything is `unverified` by construction |

Gap: a rule store with identifiers, verification status and regression cases does not exist on either side; App Watcher's
`logic.json` is the closest and would need those fields. Experiments (typing values to test a hypothesis) are an explorer
feature (Phase 3), not a collector feature.

## Phase 5: Ollama
Both projects can call a local model; neither validates the reply against a schema, versions prompts, manages context beyond
truncation, or reports confidence. App Watcher's `LocalLlmClient` already restricts input to `memory/*.md` and never sends
events or pictures. Gap: schema validation, prompt versioning, retries, "evidence-grounded" checks (every claim must cite a
memory line), and a test with a model installed. None of this was exercised here: no Ollama on this PC.

## Phase 6: database and relationship reconstruction
App Watcher reads real tables (SQLite, Access, SQL Server, ODBC, drop folders) and learns mappings, relations, status flows,
sequences and formulas from data diffs, with seen/candidate/inferred confidence; the analyzer has a "conceptual model" that
is one entity per screen with `data_type: unknown`. Gap: on the DDI target the data server is UniVerse (no reader yet; needs
the vendor's read-only account and the U2 ODBC driver or U2 Toolkit); CRUD behaviour and transaction boundaries are not
modelled as such; "observed vs hypothesis" is already the seen/candidate/inferred split.

## Phase 7: permissions and integrations
Nothing on either side captures roles or allowed/denied operations (App Watcher sees "Protected Area" messages as messages
after a button; the analyzer stores hand-written CSV rows). Integrations: App Watcher discovers backends, services from log
files, export drop folders and data files; the analyzer stores hand-written rows. Enforcement in a generated app: nothing.

## Phase 8: AI-ready blueprint
App Watcher's export is already a blueprint with evidence references (`docs/raw/screens/*.md`, `tasks`, `probes`,
`data-model.md`, `memory/*`, `logic.json`, `events.xes`, shots index). The plan's layout (`screens.json`, `fields.json`,
`navigation_graph.json`, `workflows.json`, `calculations.json`, `business_rules.json`, `database_model.json`,
`permissions.json`, `integrations.json`, `reports.json`, `coverage_report.json`, `AI_REBUILD_INSTRUCTIONS.md`) is a
different arrangement of the same facts plus three files that have no evidence source yet (permissions, integrations
beyond discovery, reports). Gap: a writer for that layout from `LearnResult`; it is a few hundred lines of C#, not a new engine.

## Phase 9: functional replacement generation
The analyzer's generator makes a generic record editor per screen (every record is one JSON blob; validations and
calculations are typed by a person into `rules.json`). It cannot read a real App Watcher export (0 fields, measured). Nothing
generates Windows desktop UI, typed tables, workflows, permissions, reports or adapters. This phase is a product of its own.

## Phase 10: differential verification engine
Does not exist on either side. The closest thing is the test PC's driven workload (`drive-*.ps1`, `score9.py` in the
evidence folders): scripts that drive the original and a scorer that compares the collector's events with the drivers' logs.
That is a verification of the watcher, not of a replacement. A replacement engine needs: scenario files, a driver for the
original (UIA, like the test PC scripts), a driver for the replacement, a comparer, and a dashboard.

## Phase 11: unified Windows application
App Watcher has the tray app, wizard, settings, health, memory files and export. Missing panels: workflow explorer, screen
inventory, rule viewer, calculation experiments, permission matrix, integration inventory, AI panel, generator, verification
dashboard. The analyzer has no application at all (CLI scripts and a Tkinter mock-up).

## Phase 12: testing and packaging
App Watcher: 126 unit tests, a live test, a single-file exe and `install.ps1`; no MSI. Analyzer: 2 tests that do not run
without pywinauto; a `.bat` that creates a venv. Missing: a synthetic accounting fixture richer than the sample ERP,
permission tests, import/export tests, security tests, an installer.

## Summary table

| Capability | Position today | Honest remaining work |
|---|---|---|
| Observation engine | App Watcher, measured on a real ERP | keep measuring on the target (v0.10 run) |
| Evidence import from App Watcher into the analyzer | does not work on real exports (0 fields) | drop the Python importer; the C# export is the blueprint |
| Calculation inference | App Watcher, with evidence rules | rule ids, verification status, regression cases |
| Ollama | two optional clients, neither tested with a model | one client, schema-validated, tested |
| Autonomous exploration | one guarded invoke in Python | a separate explorer for test environments, or UFO evaluated |
| Database discovery | App Watcher readers and learners | UniVerse reader once an account exists |
| Permissions, integrations | hand-written CSV only | needs test accounts and a capture design |
| Blueprint layout | App Twin Kit bundle | a writer for the plan's JSON layout |
| Replacement generator | generic Tkinter CRUD prototype | a product of its own |
| Differential verification | none | a product of its own |
| One Windows application | App Watcher tray and settings | the panels above |
