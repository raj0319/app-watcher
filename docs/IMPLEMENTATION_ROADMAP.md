# Implementation roadmap (after the audit of 2026-11-20)

The plan (twelve phases in its first version, fourteen in the second, which adds a technology comparison and splits
capture from observation) is not one project. Phases 2, 4, 6 and 8 build on what App Watcher has; phases 3, 9 and 10 are
new products; 5, 7, 11 and 12 are work on all of them. The order below keeps the rule that made every release so far
measurable: each step has a test on evidence and a pass mark on the next driven run, and nothing claims equivalence it
cannot show.

## Decisions the roadmap rests on

1. **App Watcher stays the application and the only collector.** The Python analyzer is not merged: it has no licence, its
   collector is a weaker duplicate, and its App Watcher importer reads 0 fields from a real export. Two ideas from it are
   kept as ideas, reimplemented in C# with tests: a rule store with verification status and regression cases, and a safe
   arithmetic rule format for a generated app.
2. **The collector never clicks.** Anything that drives the original program (exploration, experiments, the original's side
   of differential tests) is a separate tool that runs only on a test environment, and the collector records what it causes.
   The test PC's driver scripts are the seed of that tool.
3. **Evidence first.** Every new file in the blueprint carries evidence references or says `unknown`; no model output is
   written as a fact.

## Milestones

### M1. Blueprint in the plan's layout (done 2026-11-20: `BlueprintWriter`, `blueprint/` in every export, tests on the three fixtures)
A writer from `LearnResult` to `blueprint/` (`screens.json`, `fields.json`, `navigation_graph.json`, `workflows.json`,
`calculations.json`, `business_rules.json`, `database_model.json`, `coverage_report.json`, `AI_REBUILD_INSTRUCTIONS.md`,
plus `evidence/` and `screenshots/` as copies of what the export already holds). `permissions.json`, `integrations.json`
and `reports.json` are written with `status: unknown` and what discovery did find (backends, services, drop folders).
Rule records get an id, `source` (tree/ocr/data/sql), `verification: observed|inferred|unknown`, and the observations as
regression cases. Test: the writer on the three anonymised fixtures; every record references an evidence file.
Pass mark on the test PC: the v0.10 scorecard rows plus "blueprint written, N rules with evidence".

### M2. One local-model path with schema validation (small, 1 to 2 days, needs Ollama on a PC)
Keep `LocalLlmClient`; add a JSON schema per prompt (summary, inferred features with confidence and the memory line they
cite, questions), prompt versioning, retries, and a check that every cited evidence line exists. The reply is written to
`memory/architecture-reviewed.md` as it is today, marked as model output. Test: a recorded reply fixture through the
validator; a live check on a PC with a model.

### M3. The driven workload as a verification engine for the watcher (done 2026-11-20: `Scenario`, `AppWatcher.Driver`, `ScenarioScorer`, `--verify`, `scripts/scenario-run.ps1`)
Turn the test PC's `drive-*.ps1` plus `score9.py` into a repo tool: scenario files (open document, type, press, expect
message), a driver that runs them on the original with UIA (test environment only, typed confirmation), and a scorer that
compares the collector's events with the scenario. This is what has been done by hand for four releases; as a tool it gives
the pass/fail/blocked/untested dashboard the plan asks for, for the watcher itself. It is also the original's side of M6.

### M4. Rule experiments and required fields (medium, 1 week, test environment only)
With M3's driver: for a calculation the learner holds as `inferred` or with exceptions, generate one-variable-at-a-time
experiments (zero, negative, rounding, a third distinct value), run them on the original, record the outcome as evidence,
and move the rule to `verified` or `contradicted`. Required fields and defaults come from the same driver (leave a field
empty, press Save, record the message). Approval stays with the person who runs it; nothing is saved in the original
(every scenario ends with Cancel/Exit and the "changes will be lost" prompt answered the way the watcher already sees).

### M5. Exploration (medium to large, 2 to 3 weeks, test environment only)
Extend M3's driver into a crawler: the navigation candidates are the buttons and menu items the collector recorded (not a
name regex), a persistent graph of visited structures with coverage per window, a block-list of actions by effect (a press
that produced a save-type message or an effect is never repeated automatically), resume from the graph, loop prevention by
structure hash. Evaluate Microsoft UFO here as an alternative driver, with a written comparison before adopting it.

### M6. Replacement and differential verification (large, months)
Only after M1 to M5 give a blueprint whose rules are verified. The generator produces a desktop app from `blueprint/`
(typed tables from `database_model.json` where seen, forms from `screens.json`, verified calculations and validations,
workflows from `workflows.json`); the differential engine runs M3 scenarios against both and compares outputs, messages and
persisted rows. Equivalence is reported per scenario as passed/failed/blocked/untested with the coverage M1 computes, never
as a single percentage. Permissions and integrations enter here only with test accounts and documented endpoints.

### M7. One application and packaging (continuous)
Each milestone adds its panel to the Settings window or a new window: blueprint export (M1), model panel (M2), scenario
runner and dashboard (M3), experiments and rule viewer (M4), explorer and coverage (M5), generator and verification (M6).
An MSI or a signed installer replaces `install.ps1` when the first client needs it.

## What is deliberately not on the roadmap
- Merging the Python code: no licence, duplicate collector, importer that does not read the export.
- OpenAdapt: hooks input, which the collector's rules forbid.
- Claims of recovering the original database schema, server-side logic or every rule from the UI alone: the plan itself
  forbids them, and the health page keeps saying what was not seen.

## Immediate next step
M3's first scorecard on the sample app (13 of 16 steps passed) found two collector gaps: a press on the MDI container's own
button that closes the container is not recorded (the container has no recorded tree to hit-test when it is destroyed), and a
click on a menu drop-down item that lies over the toolbar is credited to the toolbar button under it (the recorded tree does
not know the popup). Fix both, then run `scenarios/` on the test PC's ERP with the driver instead of the hand scripts, so the
v0.10 column of the scorecard comes from `--verify`. M2 (local model) and M4 (rule experiments, built on the driver) follow.
