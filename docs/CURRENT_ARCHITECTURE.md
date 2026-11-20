# Current architecture: App Watcher and the Windows App Analyzer (audit, 2026-11-20)

This is the code-level audit asked for as Phase 1 of the "App Watcher Ultimate Edition" plan. Everything below was
checked in the code and by running it, not taken from either project's documentation. The analyzer was unpacked from
`windows_app_analyzer_merged_edition.zip` into `external/` (ignored by git: it carries no licence file).

## 1. App Watcher (this repository)

| | |
|---|---|
| Language, runtime | C# 12, .NET 8 (`net8.0-windows`), WinForms tray app |
| Size | 93 C# files: `AppWatcher.Core` 9,833 lines, `AppWatcher` (tray, collector) 4,808 lines |
| Tests | 126 xunit tests (`dotnet test`), all green; a live test (`scripts\live-test.ps1`) that drives a sample WinForms ERP with real mouse input |
| Measured on a real target | four driven runs against DDI Inform (Infragistics on WinForms, MDI workspace, UniVerse backend); scorecards in `docs/test-v0.8-inform.md` and `docs/improvements.md`; anonymised copies of three runs under `tests/AppWatcher.Tests/Fixtures/inform*` |
| Licence | MIT |
| Releases | v0.7 to v0.10.0 on GitHub, single-file exe (258 MB, self-contained .NET) with `install.ps1` |

### Modules (what each one does, as implemented)

**Collector (`src/AppWatcher/Collector`, `Native`)**
- `UiaCollector` (1,500 lines): FlaUI UIA3 event subscriptions scoped to the watched process, WinEvents (foreground, menus, mouse capture, create/show/destroy, focus, state change, selection), MSAA naming and tree fallback, menu popups, dialogs by structure, MDI documents from the MDICLIENT pane, inner tab pages, re-reads of the document's fields (typed cells and the program's fills), lookups, hover tips, press inference from the key state plus the control under the pointer plus the recorded tree (`TreeHitTest`). No keyboard or mouse hook; never invokes a control.
- `WatchService`: event channel, value debouncing, screen records, pictures (PrintWindow of the app's window only), OCR of pictures (Windows.Media.Ocr, built in), effects around save-type presses, status file.
- `EffectCapture`, `FileAccessTrace` (ETW, admin only): data-side before/after snapshots.

**Engine (`src/AppWatcher.Core`)**
- `Screens`: `ScreenRecord` (the App Twin Kit screen table), `DialogDetector`, `TabDocuments`, `MdiWorkspace`, `FieldReads`, `PressDecider`, `TreeHitTest`, `Labels`, `Hints`, `ShotStore`.
- `Learning` (incremental, additive `LearnState`, "learn twice = same memory" is tested): flows and navigation (`FlowLearner`), validation and message rules, field-to-column mapping from data diffs and captured SQL, formulas over data (`FormulaFitter`), on-screen logic like a trainee (`LogicLearner`: fills, calculations with evidence rules, button leads, hover tips, not-used lists, questions), picture text (`OcrLogic`), status flows, sequences, relations, health (`PlainReport`), memory files (`MemoryWriter`, `LogicWriter`).
- `Sources` and `Snapshots`: SQLite, Access/OleDb, SQL Server, ODBC, CSV/Excel drop folder, plain files; read-only snapshots and diffs.
- `Sql`: SQL Server XEvent and ODBC trace log readers (exact field-to-column evidence).
- `Discovery`: toolkit from the exe's imports, backends from config files, DSNs and open connections, 12 profiles (JSON).
- `Export`: App Twin Kit bundle (`docs/raw/screens/*.md`, `tasks`, `probes`, `data-model.md`, `memory/*`) plus `events.xes` for process mining.
- `Analysis`: `ClaudeApiClient` and `LocalLlmClient` (OpenAI-compatible endpoint, Ollama by default). Both optional and off by default; they send only `memory/*.md`, never events or pictures.
- `Setup`: wizard defaults, profiles, plain-words health.

**Scripts and skills**: `publish.ps1`, `install.ps1`, `live-test.ps1`, `check-fixtures.ps1`, `pm4py_example.py` (XES → process model), `skills/app-watcher-analyze` (the architect review in Claude Code / OpenCode / OpenClaw), `skills/app-watcher-ask`.

### Dependencies (NuGet)
FlaUI.UIA3 4.0.0 (MIT), Microsoft.Data.Sqlite, Microsoft.Data.SqlClient, System.Data.Odbc, System.Data.OleDb, Microsoft.Diagnostics.Tracing.TraceEvent, Microsoft.SqlServer.XEvent.XELite, System.Security.Cryptography.ProtectedData; tests: xunit 2.9. All Microsoft packages are MIT or MS-permissive.

### Evidence model
Everything is a file in `%ProgramData%\AppWatcher\data\<app>\`: `events/*.jsonl`, `screens/*.md` + `index.json`, `screens/shots/` (+ `.ocr.json`), `effects/*.json`, `snapshots/before.json`, `collector-status.json`, `memory/*.md|json`, `export/<date>/`. Every memory line ends with `seen N×` or `inferred` and the evidence file. Raw typed values never reach memory (shapes only); personal fields the program fills are shapes in the events too.

## 2. Windows App Analyzer Merged Edition (the zip)

| | |
|---|---|
| Language, runtime | Python 3.10–3.12, Tkinter; `requirements.txt`: pywinauto, Pillow |
| Size | 10 Python files, 1,103 lines; 6 Markdown guides; 2 CSV templates; one `INSTALL_AND_RUN.bat` |
| Tests | one `unittest` file with 2 tests (ZIP merge idempotence, ZIP-slip rejection). On this PC it fails to import: pywinauto is not installed |
| Licence | none in the zip |
| Measured on a real target | none ("No Windows real-application end-to-end test was possible in this environment", its own README) |

### Modules (as implemented, read line by line)
- `analyzer.py` (200 lines): `list` windows, `inspect` one, `watch` = poll one top-level window every 1.5 s with pywinauto, dump every descendant's name/type/id/class/bounds (no values), write a snapshot when the structural signature changes, screenshot the window; `probe` = type a calculation observation by hand; `build` = pages/navigation/fields/calculation_evidence JSON and an `AI_REBUILD_INSTRUCTIONS.md`.
- `advanced.py` (160 lines): manual labels and workflows; `evaluate_formula`: with 3+ manual observations, brute-force `a`, `a±b`, `a×b`, `a÷b`, `a×b±c`, `a×b×(1−c/100)` over the typed inputs; `build-extra` writes placeholders (`permissions: unknown`, `data_model: unknown`, `reports: not_tested`), a coverage file and a handoff; `ai` posts the JSON to a local Ollama `/api/generate` and saves the reply unparsed-or-parsed.
- `autonomous.py` (170 lines): `discover` = after typing "DISCOVER", invokes at most N controls whose label matches an allow-list (`home|back|customers|…`) and not a block-list, one per screen, stops when the signature repeats; `build` = "conceptual data model" (one entity per screen with its editable controls), "calculation verification" (the same candidates, status `unverified_hypotheses`), a Tkinter mock-up with entries that save nothing.
- `complete.py` (150 lines): `plan` classifies controls by name regex into navigation/unknown/unsafe (never clicks), `explore` invokes one approved automation id after typing "NAVIGATE", `import-probes` from CSV, `enrich` writes a screen graph, "field inventory" (nearby labels), test plan and gap report, `build-all`.
- `enterprise_discovery.py` (110 lines): reads a CSV of hand-written rule / permission / integration observations, rejects rows containing secrets, writes them back as JSON with status `observed_once_not_generalized` and a regression plan.
- `import_appwatcher.py`, `merge_appwatcher.py` (160 lines): parse `docs/raw/screens/*.md` for lines of the form `Edit: Name` (or `- Edit | Name`), copy `memory/*.md`, parse `events.xes` transitions, merge into `pages.json` without duplicating pages.
- `replacement.py`, `replacement_runtime.py` (270 lines): from `pages.json` editable controls, generate a Tkinter CRUD app with one SQLite table `records(screen, data JSON)`, audit table, search, CSV export, JSON backup, a `rules.json` where a person types validations and arithmetic (`safe_formula`: AST-whitelisted `+ − × ÷ % **`), optional Ollama "suggestions" saved as `ollama_suggestions_UNVERIFIED.json`.

### What the analyzer does with a real App Watcher export (measured here)
`merge_appwatcher.merge()` was run on the zip App Watcher exported from the sample ERP session (`sample-erp-2026-11-20-1853.zip`, 9 screens, 43 XES events):

| | |
|---|---|
| pages imported | 9 (titles taken from the `# Screen:` line, including the "Screen: " prefix) |
| controls recognised | 0 |
| fields | 0 |
| XES | 1 trace, 43 events parsed |
| memory files copied | yes (architecture, data-model, flows, health, logic, probes) |

App Watcher writes control trees as `| Depth | Type | Name | AutomationId | Position |` tables; the importer only recognises
`Type: Name` lines, so it keeps the documents as "evidence" and extracts nothing. With 0 fields, `replacement.py` stops with
"No editable UI controls found". The merge edition's own README anticipates this ("App Watcher's actual exported screen syntax
may differ"). So the "App Watcher evidence import" row of the plan's table is not "done, test with real exports"; it does not work yet.

## 3. The tool claims, checked

| Tool | App Watcher | Analyzer | Verdict |
|---|---|---|---|
| Windows UI Automation | FlaUI UIA3, event-driven, scoped to one process | pywinauto polling, whole-tree dump every 1.5 s | both real; the analyzer's is a snapshotter, not an event collector |
| MSAA / win32 fallback | IAccessible naming and tree walk | pywinauto `--backend win32` | both real |
| OCR | Windows.Media.Ocr on every state picture, text layers used by the learner | none (README says "not implemented") | App Watcher only |
| Ollama | `LocalLlmClient`: OpenAI-compatible chat endpoint, off by default, memory files only | `advanced.ollama()`: `/api/generate` with `format: json`, model name validated, local endpoint only | both integrated, both optional, neither exercised in a test here (no Ollama on this PC) |
| Microsoft UFO | not used | not used ("evaluate separately") | not integrated anywhere |
| OpenAdapt | not used (it hooks input, which App Watcher's rules forbid) | not integrated ("separate tool") | not integrated anywhere |
| Autonomous exploration | none by design (never invokes a control) | one allow-listed invoke per run after a typed confirmation | a guarded single step exists; no crawler, no coverage, no resume |
| Replacement generation | none | generic CRUD per screen (one JSON blob per record), rules typed by hand | a prototype, not business-specific |
| Differential verification | the driven scorecard on the test PC (manual scripts, `score9.py`) | none (plans and placeholders) | no engine on either side |

## 4. Duplicated functionality
- Two collectors (C# event-driven vs Python polling). The Python one reads no values, no dialogs by shape, no menus, no documents of an MDI workspace, and credits nothing to presses.
- Two formula fitters: `LogicLearner.Formulas` (same operator set plus discount and tax forms, evidence rules, incremental) vs `advanced.evaluate_formula` (manual observations only, ≥ 3).
- Two blueprint exporters: App Watcher's App Twin Kit bundle plus `logic.json`, `events.xes` vs the analyzer's `pages.json`/`fields.json`/`navigation.json` family. Both are JSON and Markdown with evidence references; the layouts differ.
- Two Ollama clients.
- Two "handoff to an AI" documents (App Watcher: `skills/app-watcher-analyze`, `prompts/analyst-prompt.md`; analyzer: `AI_REBUILD_INSTRUCTIONS.md`, `MASTER_AI_HANDOFF.md`).

## 5. Security, privacy, licences, Windows compatibility
- App Watcher: no hooks, no network unless the optional analysis is switched on, pictures of the app's window only, password and personal values masked, excluded windows, evidence folders and fixtures anonymised with a check script. Runs on Windows 10/11 x64, no Python needed.
- Analyzer: reads no edit values (by design) but screenshots the whole target window unredacted; `discover`/`explore` invoke controls of the target (allowed only on a test environment after a typed word); the Ollama call sends the JSON evidence to localhost only. Needs Python 3.11 and pywinauto; its tests do not run without them.
- Licences: App Watcher MIT; FlaUI MIT; pywinauto BSD-3; Pillow MIT-CMU; the analyzer zip has **no licence**, so none of its code may be merged into this repository until its author adds one.
