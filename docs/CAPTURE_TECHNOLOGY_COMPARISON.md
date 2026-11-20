# Capture and analysis technologies: what App Watcher has, what each candidate would add (2026-11-20)

The plan lists nineteen technologies. For each: whether App Watcher already covers the need, what the candidate would
add, its cost (dependency, runtime, licence, the rules it would break), and the verdict. "Measured" means it was run on
the real target (DDI Inform) or in this repository's tests; "not here" means nothing in either codebase uses it.

## Windows accessibility and UI inspection

| # | Technology | In App Watcher today | What it would add | Cost | Verdict |
|---|---|---|---|---|---|
| 1 | FlaUI (C#, UIA3) | the collector's base: event subscriptions scoped to one process, cached tree walks, patterns; measured on the target | nothing more to add | MIT, already a dependency | keep as the base |
| 2 | Microsoft UI Automation | what FlaUI wraps; plus WinEvents (foreground, menus, capture, create/show/destroy, focus, state, selection) hooked directly through `SetWinEventHook` | nothing | built into Windows | keep |
| 3 | MSAA / IAccessible | naming of the object behind a WinEvent, pressed states, a tree walk when UIA shows fewer than ten controls | the Infragistics tab strip through PAGETAB roles was listed for v0.9 and turned out unnecessary (documents come from the MDI client) | built into Windows | keep as the fallback it is |
| 4 | pywinauto (Python) | not used; the analyzer's whole collector is a pywinauto polling dump | a second UIA client in another language; it reads nothing FlaUI cannot. Its `win32` backend equals the MSAA fallback | Python 3.11 runtime on every client PC, BSD-3; a second collector violates "no duplicated collectors" | do not add to the watcher; acceptable only inside a separate test-environment driver (see 10) |
| 5 | Microsoft Inspect.exe / Accessibility Insights | not used in code; the probes of 2026-11-19 (`tabstrip-probe2`, `hover-probe`) did the same with FlaUI scripts | a person's diagnostic tool for the test PC | free, interactive only | recommended for the person on the test PC; nothing to integrate |
| 6 | Windows Graphics Capture | pictures are taken with `PrintWindow` of the app's window only (one per state, deduplicated, capped) | capture of GPU-rendered windows that `PrintWindow` leaves black; a frame stream | Windows 10 1903+, WinRT interop in a WinForms app | add only if a target shows black pictures; none did so far |
| 7 | Windows OCR / Tesseract | Windows.Media.Ocr on every state picture; the text layers feed `OcrLogic` (label → number pairs, formulas from pictures) | Tesseract: other languages and a tunable engine; no need shown yet (the target is English) | Tesseract: Apache-2, native binaries, 30+ MB | keep Windows OCR; Tesseract only on a demonstrated OCR failure |
| 8 | OpenAdapt Capture | not used | video plus raw mouse and keyboard events | installs input hooks, which the collector's rules forbid on a user's PC; records keystrokes (passwords) | reject for the watcher; at most a reference for a test-environment recorder |

## Autonomous exploration

| # | Technology | In App Watcher today | What it would add | Cost | Verdict |
|---|---|---|---|---|---|
| 9 | Microsoft UFO | not used anywhere (the analyzer's README says "evaluate separately") | a model-driven agent that plans and executes actions on Windows apps | Python, a hosted or local vision model, its own safety model; it clicks, so it can only run on a test environment; licence MIT | evaluate in milestone M5 with a written comparison against the FlaUI driver; not before the driver exists |
| 10 | pywinauto / FlaUI automation (driving the original) | the test PC's driver scripts (`drive-*.ps1`, UIA through .NET) drove four measured runs; the live test drives the sample app with real mouse input | a repo tool for scenarios on a test environment (milestone M3) | none new (FlaUI is here); the rule "the collector never clicks" stays, the driver is a separate tool | build the driver in C# on FlaUI; pywinauto adds nothing |
| 11 | Playwright | not used | browser automation for web parts of a target | Node or .NET package, browsers | out of scope until a target has a web interface |

## AI and reasoning

| # | Technology | In App Watcher today | What it would add | Cost | Verdict |
|---|---|---|---|---|---|
| 12 | Ollama (local models) | `LocalLlmClient` (OpenAI-compatible chat endpoint, off by default, sends memory files only) | schema-validated, versioned prompts (milestone M2) | a model on the client PC (several GB), CPU or GPU | keep, harden in M2 |
| 13 | Vision models on screenshots | not used; the pictures are read by OCR instead | descriptions of custom-drawn screens | the pictures hold customer data; sending them to a model, even local, needs the privacy setting and a redaction step | after M2, opt-in, local only, on the OCR-failed screens |
| 14 | JSON Schema for model output | not used; the analyzer asks for `format: json` and saves whatever comes back | rejecting replies that do not fit the schema or cite no evidence | a validator (System.Text.Json schema support or a small package) | part of M2 |

## Workflow and business analysis

| # | Technology | In App Watcher today | What it would add | Cost | Verdict |
|---|---|---|---|---|---|
| 15 | PM4Py | `events.xes` export; `scripts/pm4py_example.py` draws the directly-follows graph | discovery algorithms (inductive miner, conformance) over the same log | Python with pm4py on the analyst's PC, not the client's | keep as is: the log is the interface |
| 16 | SymPy | not used; `LogicLearner.Formulas` enumerates sums, products, ratios, percentages, discount and tax forms over the screen's numbers and `FormulaFitter` does the same over data columns | symbolic simplification of candidates, linear solves for constants | Python on the analyst's PC | not needed for the forms seen so far; revisit if a target shows rules the enumeration misses |
| 17 | Hypothesis (property-based tests) | not used | generated input cases for a verified formula's regression tests | Python | belongs to the differential engine (M6), where the replacement is tested |
| 18 | SQLite for evidence | evidence is files (`events/*.jsonl`, `screens/*.md`, `memory/*.json`), readable without a tool; SQLite is a data *source* reader, and the generated analyzer app's store | queries over evidence | a schema to maintain, migration of three fixtures' worth of formats | not now: the files are the schema, and every memory line already cites a file and line |
| 19 | Graph analysis | navigation graph (directly-follows with counts and medians), flows, back-steps, Mermaid output | centrality, unreached nodes once an explorer exists | none | add with M5 |

## Summary
Nothing in the list replaces what the collector does today; three items are worth building in order: schema-validated local-model output (12, 14), a FlaUI driver for scenarios on a test environment (10) which is also the base for exploration (9) and differential tests (17), and Windows Graphics Capture only on evidence of black pictures (6). Four are rejected for the watcher itself: pywinauto as a second collector (4), OpenAdapt (8, hooks input), Tesseract without a shown need (7), SQLite as an evidence store (18).
