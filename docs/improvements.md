# Improvement list and status

Written 2026-11-18 after the first real run against DDI inForm (265 events, 23 windows, one session), done
2026-11-19 in version 0.3. Each item says what was observed, what changed, and how it was tested.
Status: **done** (code + unit test or live test), **partly** (what is missing is said), **not done** (why).

## A. Auto-apply on app selection (app profiles)

| # | Status | What changed |
|---|---|---|
| A1 | done | `ToolkitDetector` reads the exe (and, for framework-dependent .NET apps, its `<name>.dll` and `runtimeconfig.json`) and the names of the DLLs next to it: WinForms, WPF, WinUI, Avalonia, Infragistics, DevExpress, Telerik, Syncfusion, Qt, Delphi/VCL, VB6, MFC, PowerBuilder, Electron/CEF, Java, Access. Picks the profile and tells the user what to expect. Test: `Toolkit_detector_reads_markers_from_exe_and_siblings`; live: the sample app is seen as WinForms. |
| A2 | done | `BackendDetector`: connection strings and URLs in config files next to the exe and under ProgramData/AppData, ODBC DSNs (user and system), and the TCP connections the running process holds (host and port, classified by port: SQL Server, MySQL, PostgreSQL, Oracle, Pervasive, UniVerse 31438, REST, ...). Passwords are stripped before anything is kept. Shown on tab 1 and tab 2, written to `architecture.md` and `data-model.md`. Test: `Backend_detector_classifies_connection_strings_without_keeping_secrets`. |
| A3 | done | Tab 1 shows "What App Watcher expects to learn from this program" as soon as a program is picked: screens/flows, fields/buttons (from the toolkit), tables/rules (from the data sources and the backends found) with the options when the data is on a server. Also `AppWatcher.exe --expect` and `<app>/expectations.md`. |
| A4 | done | `profiles/*.json` embedded in the exe plus `%ProgramData%\AppWatcher\profiles\` for the user's own. Matched by process name, product name, then toolkit. Shipped: generic, winforms, wpf, infragistics, devexpress, component-suite, electron, classic, java, custom-drawn, office, **ddi-inform**. "Export these settings as a profile" on tab 3. Test: `Profiles_match_exe_then_toolkit_and_apply_onto_config`. |
| A5 | done | Trigger words now cover update/accept/finalize/process/commit/confirm/release/approve/void/complete and their Spanish, German, French, Italian, Portuguese, Dutch, Polish, Danish, Swedish, Finnish, Russian, Chinese, Korean, Japanese forms; profiles add their own; `AcceleratorKey`/`AccessKey` of every button and menu item is recorded in the screen record and shown in screens.md. Learning triggers from presses that were followed by a data diff is not done (the diff capture already keys on any button once a profile names it). |
| A6 | done | A selected tab item becomes a screen of its own, "Window › Tab": the tab's subtree is dumped (or the window's on-screen controls for MDI-style tabs), events after the selection are attributed to the tab, and the learner treats "›" titles as kind `tab`. The collector checks that the tab item really is selected (WinForms raises a bogus selection event when a window is built). Live test: `Sample ERP › Orders` screen. |
| A7 | done | `Titles.Key` masks quoted parts ("EVERFLOW SUPPLIES INC" → "…") and collapses digits; `TitleNormalizer` folds titles that share one structure but differ in a word ("Customer - # "…"", "Order #"). Privacy tab switch "Mask quoted parts". Test: `Titles_mask_quoted_parts_and_normalizer_folds_varying_words`. |
| A8 | done | Session windows (password field, splash/login/register title, tiny first window, profile patterns) are tagged `session`; flows start after them, the navigation graph keeps them. Test: `Session_windows_are_found_by_password_title_or_size`, `Flow_steps_carry_actions_and_skip_session_windows`. |

## B. Collector

| # | Status | What changed |
|---|---|---|
| B1 | done | Four ways to see a press, first one wins per click: UIA Invoke; MSAA `EVENT_OBJECT_INVOKED` and a push button whose state gains PRESSED; a menu closing right after an item was highlighted (UIA MenuOpened/MenuClosed, WinEvents MENUPOPUPSTART/END, focus on menu items); inferred: focus on a button in window X followed within 600 ms by a window opening from X, X closing, or the foreground changing from X. Toggle changes are recorded too. `events/*.jsonl` carries `how`. Live test on the sample app: Log in (inferred), Save (MSAA state), menu items (menu). |
| B2 | done | Dump strategy: cached subtree with the control-view filter, cached without it, a live walk that survives bad nodes, a shallower raw-view walk, then MSAA; the first with ten controls wins, failures are counted in `collector-status.json`. Row cap 3000 (profile), depth 14-24. A window whose dump had fewer than ten controls is re-dumped at most once a minute when it comes to the front or its title changes; a richer tree replaces the file. |
| B3 | done | `DialogDetector`: class #32770, or ≤ 25 controls with 1-4 buttons from OK/Cancel/Yes/No/Close/Retry/... and a body that is a Text or a read-only Edit named like lblBody (its Value is read once). Profiles can name dialog windows. Dialogs are `dialog` events (text in `val`), not screens. Test: `Dialog_detector_knows_classic_and_toolkit_message_boxes`. |
| B4 | done | Labels: the control's Name, then `LabeledBy`, then the nearest Text to the left or just above (from the tree dump), then the AutomationId with wrapper suffixes and Hungarian prefixes removed and camel case split ("srcMasterSearch_EmbeddableTextBox" → "Master Search"). Trailing colons stripped. Tests: `Labels_clean_wrapper_suffixes_prefixes_and_camel_case`, `Labels_resolve_from_name_then_nearest_text_then_id`. |
| B5 | done | `ScreenRecord.LogicalFields()` collapses wrapper editors to one field (innermost editor wins, wrapper id kept as evidence); the Fields table shows the label and the AutomationId. Test: `Wrapper_controls_collapse_to_one_logical_field`. |
| B6 | partly | Grids: headers in full, the first 5 rows, then one note row "... N more rows (M in all); columns: a, b, c". Cell edit events are recorded as value events on the cell (the column is not yet named). |
| B7 | done | Shortcuts from `AcceleratorKey`/`AccessKey` in the Actions table of each screen record and in screens.md ("Save [F3]"). |
| B8 | done | MSAA tree walk (IAccessible) when UI Automation shows fewer than ten controls; roles mapped to the UIA control types; health.md says how many screens came that way. |
| B9 | done | The start event carries hashes of the Windows user name and machine name; health.md counts users and PCs. Flows per role are not split (counts only). |

## C. Data side

| # | Status | What changed |
|---|---|---|
| C1 | done | `OdbcSource`: any DSN or driver string, SELECT only, catalog from the driver, optional table list (ERP catalogs are huge), 400-table cap. Settings: "Add ODBC connection (read only)" with the DSNs of this PC. Not tested against a live UniVerse (none here); the SQL path is the shared `AdoDumper` used by SQL Server. |
| C2 | done | `DropFolderSource`: CSV/TSV/TXT and XLSX exports, one table per file, header row, first id-like column as key, change probe by newest time and size. Test: `Drop_folder_reads_csv_and_xlsx_exports_as_tables`. |
| C3 | done | Backends found by discovery are written to `data-model.md` and `architecture.md` (kind, host, port, database, where found; no credentials). |
| C4 | done | `scripts/sql/mysql-general-log.sql`, `postgres-log-statement.sql`, `oracle-trace.sql`, `universe-logging.md`; `SqlLogReaders.ReadTextLog` reads any text log with timestamped INSERT/UPDATE/DELETE lines. Test: `Text_sql_logs_of_other_engines_are_read`. |

## D. Learner and memory

| # | Status | What changed |
|---|---|---|
| D1 | done | Flow steps list the actions seen on each screen with counts: "press Save (3), type Customer (3), menu Orders (2), pick Status (1)". Test: `Flow_steps_carry_actions_and_skip_session_windows`. |
| D2 | done | Dialog text after a typed value is a validation rule; after a button or menu it is a "message after an action"; dialog windows are listed once in screens.md, not as screens. Test: `Message_boxes_become_rules_not_screens`. |
| D3 | done | "Reached from" = most common predecessor in the navigation graph; "Menu item before it opened" from the menu item selected within 5 s; the export fills the screen record header. |
| D4 | done | Per field: typed count, value shapes (never values), inferred type (date, money/decimal, integer, number, code, text), "never typed into" list per screen. |
| D5 | done | `AppWatcher.exe --merge <folder or zip>`: events as `<date>.<machine>.jsonl`, effects by name, screens through the structure index, then a full rebuild. Test: `Merge_folds_another_pc_and_health_counts_users_and_machines`. |
| D6 | done | Retention (default 180 days, 0 = forever) deletes learned event files older than that; disk use per folder in health.md. Test: `Retention_deletes_only_learned_old_event_files`. |

## E. Setup, privacy, operations

| # | Status | What changed |
|---|---|---|
| E1 | done | Privacy tab: mask quoted title parts, one picture per screen on/off, excluded windows, allowlist, "Show what we store" (newest screen record and the last events, in our own window). |
| E2 | done | After 60 minutes a learn runs and health.md gets "What the collector could not do" from `collector-status.json` (dumps failed/empty, presses by path, dialogs, tab screens); the tray notice is only the headline. |
| E3 | done | `scripts/appwatcher.cmd` (start /wait) so a shell gets the exit code and the output; install.ps1 copies it next to the exe. `--setup <exe> <password> [profile]` for scripted first runs. |
| E4 | partly | No MSI/winget package. `--setup` plus install.ps1 cover scripted and fleet installs; a central config is `config.json` under ProgramData. |
| E5 | not done | The settings window is English only; trigger words are multilingual (A5). |
| E6 | partly | `tests/SampleApp` (WinForms: login, tabbed workspace, menu, dialog, message windows, JSON "database") and `scripts/live-test.ps1` drive it by UI Automation and check events, screens, pictures, effects and memory. Not wired into CI; no WPF or Infragistics sample. |

## Version 0.11: the four red rows of the v0.10 run (2026-11-20)

The v0.10 run on the real ERP met five pass marks and moved Exit to 12 of 17 and sub-tabs to 32 of 49. The brief named six
causes, each checked in the events; all six are fixed, and the run is the new fixture `inform-v0.10`.

- **One place for the click decisions** (`ClickTracker` in Core): the collector calls it at every event, and the fixture
  replay calls the same class with the run's live order (hovers, the click heard at the event that followed it, the destroy).
- **A click needs a click.** `ClickSampler`: a down seen since the last sample (bit 0) and the button up at a sample; the
  point is the pointer read in the same handler. Without a click nothing is pressed: the control under a resting pointer and
  a focused default button are credited only right after a click (or Enter), and a PRESSED state only while the button is
  down. The "moved since the previous event" surface of the last commit is gone (it used a point from an earlier event).
- **Message boxes** keep their button rows in memory while they live; a click heard at the foreground switch that comes
  before the box closes is resolved on the box (`ClickSurfaces`: a window going away first, then the window really under the
  point by WindowFromPoint, never a tree found by title, never the top window's tree inside a document).
- **One tree per document window.** A document whose title was seen before (Product Analysis after Product Master) gets its
  own tree read for clicks, and a second layout under one title gets its own screen record (`name-2.md`). When the live element
  under the point disagrees with the recorded row, the press is cancelled (the tree was stale).
- **Clicks in a document are confirmed by its re-read** at 300 ms: a new row or a changed cell (Add Line Item).
- **The typed grid row**: Enter or Tab notes the cell that had the focus; its row is read again (by place when the focus moved to
  a new line) at 300 ms and 1.5 s; its cells carry a `row` field, and the learner keeps cell values per row, so a new empty line
  no longer spoils the line being priced. Field reads are kept per window handle, so a document that takes its title keeps its
  baseline. Windows that had the keyboard focus (GetGUIThreadInfo, a read) count as typed-in even when a focus event is missed.
- **Typed values**: a value stays pending while the program reacts (retitle, tab reads, lookups) and is written once with the
  final window; an empty typed value is never written; the grid editor's copy (named after the grid) waits 1.5 s and is dropped
  when the cell read names the column; the cell's own value shown when editing starts is not typed.
- **Learner**: a code typed in a window whose document takes its title within 5 s is the cause of the document's fills
  (the document's own title read in between is the program, not a choice).
- **Lookups**: only a list that was not in the previous read, starting within 40 px below or beside the field; a popup list
  shown again while the same one is open is not new.
- **Replay of the run** (`InformV11Tests`, 12 tests): Exit 17 of 17, message-box buttons 7 of 7, Add Line Item 2 of 2,
  wrong-name presses 0, typed values without fragments or empty values and each quantity once, the extension formula, the
  customer's header fills seen 2 of 2, lookups 2 right and 0 false. Sub-tabs: 42 of 49 resolve from the recorded trees and the
  live read; the other 7 are in the two layouts v0.10 never recorded and need a driven run of v0.11. 149 tests in all.

## Version 0.11: the scenario driver and the scorecard (milestone M3, 2026-11-20)

The hand-driven runs on the test PC (`drive-*.ps1` plus `score9.py`) became a tool in the repository.

- **Scenario files** (`scenarios/*.json`, `Scenario.cs`): steps `press`, `open`, `menu`, `nav`, `type`, `hover`, `prompt`,
  `wait`, each with a default expectation (a press of that name within 4 s, the opened screen within 6 s, the selected tab,
  the typed value, the dialog plus its button, a tip that is optional).
- **`AppWatcher.Driver`**: runs a scenario on the original with UI Automation to find and the real mouse and keyboard to act.
  Test environments only (`--test-environment` plus the word DRIVE), acts only while the target is in front, blocks
  data-changing presses unless the step says `confirm: true`, and writes `run.jsonl` with what it clicked where. Two things
  it learned on the sample app: a modal box and a menu drop-down are not always listed under the desktop by UI Automation, so
  it also walks the process's visible top-level windows with Win32; and a control is a target only when the element at its
  centre is itself (an MDI document behind another has visible buttons too).
- **`ScenarioScorer`** and **`--verify`**: per step passed | failed | blocked | untested, per kind, the presses no step
  explains, typed values credited to the program; Markdown and JSON under `verification/`. The anonymised text log of the
  2026-11-19 rerun scores through the same scorer (`LegacyDriverLog`) and reproduces the hand score exactly: opens 4/4,
  Exit 1/4, prompts 0/2, Add Line Item 0/1, sub-tabs 1/17, the five wrong-name presses (Exit, Cancel, Exit, Exit, Edit).
- **First scorecard on the sample app** (`scripts\scenario-run.ps1`): 13 of 16 steps passed, 2 untested (the password,
  a wait), 1 failed: the press on the workspace's own "Close workspace" button is not recorded (the MDI container has no
  recorded tree to hit-test when it is destroyed). One press no step explains: the click on Help > About, whose drop-down item
  lies over the toolbar, was credited to the "Workspace" button under it. Both are collector gaps for the next version; the
  tool exists to find exactly these.
- Tests: `ScenarioTests` (3: the rerun log, the scenario format and defaults, the run log). 135 in all.
- **Both gaps fixed the same day** (`ClickSurfaceTests`, 137 tests in all; the rules are unit-tested, and the diagnostic log of
  the last scenario run showed the two causes exactly, but the scenario and the live test have not been rerun on real windows
  yet: the desktop was taken by another tool's fixture windows at the time. Rerun `scripts\scenario-run.ps1` and
  `scripts\live-test.ps1` before the next release):
  - A click is now resolved on the recorded trees in this order (`TreeHitTest.Surfaces`, pure): the window being destroyed,
    the window in front, the window that was in front at the previous event when it changed since (the click went to it
    before the switch), and that window at the previous pointer position when the pointer moved; a document of an MDI
    workspace is followed by its container, whose own buttons are not in the document's tree. A hit in the container's
    tree belongs to the container (its plain title). A press is confirmed by its own window or by one of that window's
    documents ("Main" and "Main › Order" are the same for that purpose), so the press on "Close workspace" is counted
    when the workspace closes while a document is still in front.
  - A click heard of while a menu is open, within 300 ms after it closed, or within 1 s after a menu item was counted,
    belongs to the menu (`PressDecider.Clicked(..., overPopup: true)` keeps no target): the toolbar button lying under the
    popup in the recorded tree is not what was clicked. A window that opens within 1 s after a counted press
    (`PressDecider.PressedWithin`) is that press's doing, so the control under the pointer is not credited again.
  - The driver is per-monitor DPI aware (`app.manifest`), so the points in its run log are physical pixels, the same as the
    Position column of the screen records; and the watcher's log now says, per click, which trees were tried and what was hit,
    and per inference what was pending and whether it was confirmed (`watcher.log`, the lines starting "click at" and "infer").

## Version 0.10: one attempt, four jobs (after the v0.9 run, 2026-11-19)

The third measured run (v0.9 on the same ERP) left four jobs: clicks on buttons that are gone by the time the watcher hears
of the click, typed grid cells, memory quality and privacy, and three small ones. The anonymised run is
`tests/AppWatcher.Tests/Fixtures/inform-v0.9`, with `clicks.json` (every truth click with the point the hit-test resolves it at).

| Job | What the run showed | What changed | Test |
|---|---|---|---|
| 1 Clicks from the recorded tree | Exit 2 of 20, OK/No/Cancel 0 of 8, Add Line Item 0 of 2: the next event after such a click is the destroy of the dialog or document, the live read under the pointer finds the window behind, and the fallback (the control remembered from the last state change, which Infragistics raises on hover) gave 14 wrong names. 1 of 57 sub-tab clicks. | `TreeHitTest`: the recorded control tree (positions in screen pixels) is the hit-test surface. At every event the collector keeps the pointer and the window in front; when the left button went down, the target is the live control under the pointer when it still exists, else the button or tab item at that point in the recorded tree of the window the click went to (the one being destroyed, else the one in front, else the one in front at the previous event). The destroy of the window in front confirms the press before the close event. Hover memory is gone (a mouse capture still remembers). A hit on a tab item is a `select` of that page. A tick box flip is the user's only when the click landed on it. | `Every_truth_click_of_the_run_with_a_recorded_rectangle_resolves…`: Exit 20/20, prompts 8/8, Add Line Item 2/2, 37 of the 57 sub-tab clicks had a rectangle in the recorded tree and resolve; the other 20 were off screen when the document was recorded and need the live read; `A_click_on_a_button_that_the_next_event_destroys…`; live: the sample's Confirm? box and the document's Exit |
| 2 Typed grid cells | The typed customer and product went to the program's batch (the cell editor has no name); `0`, `00`, `002`, `00283` were four typed events (the popup flickered between windows); Net Price was read as 0 and never again. | A nameless cell editor takes its column from the recorded tree (`ColumnAt`); the focused cell is matched by its place (`FieldReads.AtRect`); rows are keyed by their line number; the debouncer keys by field, not window, with a second of quiet; a second re-read at 1.5 s after a typed value or key; one timestamp per batch; a fill of the same field after one action counts once; a typed cell is the cause of the fills within 2 s even when the collector credited them to the main window; a batch of three or more fills after one cause is a rule from the first time. | `A_nameless_cell_editor_takes_its_column_name…`, `Keystrokes_credited_to_two_windows…`, `The_typed_cells_and_the_late_price_give_the_extension_formula…` (Price Extension = Net Price × Quant Ordered seen 3× of 3: line 1 after the late price, qty 5, qty 20; "Typing Customer fills …" 1× of 1) |
| 3 Memory quality and privacy | 15 false "never recorded" documents (class names, first titles); tick boxes shape-masked; "Fax Include Area Code is copied from Fax", "Hits = Avail + On Hand", "Ln = Avail ÷ On Hand"; the vendor, customer, city and writer names in memory. | `MdiWorkspace` keeps one entry per renamed document, class-like names are never titles, never-recorded means none of its titles was recorded (the learner recognises a rename in the events too); `Masking.ForAppValue` shapes text controls only; a calculation from grid cells or pictures needs three distinct values, and any formula with an alternative that fits every observation is a question; tab names, steps, causes and screen names go through the title masks (`Vendor - * "*"` and friends in the profile); a control named by data (a phone, a city line, a record id) is renamed to its cleaned id or its shape; Writer, Customer, Sold To, Ordered By and Buyer are personal fields. | `Health_names_no_document…`, `Tick_boxes_keep_their_state…`, `Masks_reach_every_place…` (the memory rendered from the fixture holds none of the stand-ins) |
| 4 Small | "12" typed in a grid cell counted as a lookup; "Accounting" over the Sales Order button was a hint; "New Sales Order 8× · 0 ms". | A lookup needs rows that were not on screen before; a tip that is the name of a menu, button, tab or window is dropped; a flow runs from the document's open to its close (the lone close visit joins its run, the main window's flicker is no visit). | `A_lookup_grid_that_stayed_on_screen…` |

Learner state version 5. What this version still cannot prove offline: the 20 sub-tab items without a rectangle in the recorded tree
depend on the live read at the next event, and the next driven run on the test PC fills the v0.10 column of the scorecard.

## Version 0.9: after the second measured run (v0.8 against the same ERP, 2026-11-19)

The same scripted workload, measured again with 0.8 (`docs/test-v0.8-inform.md` is the scorecard): dialogs, lookups, hints,
flows, backends, health and the CLI worked; tab documents reached 7 of 12, presses 16%, the order fills came once and 45 s late,
no formula was learned and the order-entry screen was never a screen. The probes on the program itself (not guesses) showed why,
and each cause has its fix and test below. The anonymised copy of that run is `tests/AppWatcher.Tests/Fixtures/inform-v0.8`.

| # | What the probes showed | What changed | Test |
|---|---|---|---|
| 1 | A document is an MDI child window (`ControlType.Window`, parent a Pane whose class contains `MDICLIENT`); with one document open there is no TabItem anywhere; the only `Tab` controls are the document's inner tab controls. The `select TabItem` events 0.8 got came from UIA selection events only some documents raise; the 2 s poll found the inner tab control instead of a strip. 5 of 12 documents, including Sales Order Entry, were never screens. | Documents come from the MDI client: create/show/focus/destroy events of child windows whose parent is the MDICLIENT pane (already hooked, out of context), the one in front read with `GetTopWindow` at most every 2 s after events (`MdiWorkspace`), the screen titled "Main › Document" and dumped from the document's own window, closed on destroy. A `Tab` control inside a document is never taken for the workspace strip (`TabDocuments.IsInnerTab`), neither in the collector nor in the learner. The status gets `MdiDocuments` and `DocumentsNeverRecorded`; health.md lists those titles. | `The_document_in_front_is_the_screen_and_a_destroyed_document_hands_over_to_the_next`, `A_tab_control_inside_a_document_is_never_the_workspace_strip`; live: the sample's View › Workspace opens an MDI workspace with two documents |
| 2 | 0 of 50 sub-tab clicks (Main, General, Credit) were recorded. | After a click or a focus move inside a document the inner tab controls are read with the document (the selection through the selection property only where the profile says it is safe, `selectionReads`; on plain WinForms by the page on screen, since asking a WinForms tab item selects it) and a changed page is a `select` with `how: read` (`InnerTabSelections` in the status). | `Fifty_sub_tab_clicks_give_fifty_selections_and_an_unchanged_page_gives_none`; live: a real click on the document's Notes tab |
| 3 | The pointer read was stale: Infragistics toolbar buttons raise a state change on hover, so "hover Report, click Exit" credited Report; Exit was caught 2 of 18 times because a document closing confirmed nothing; 3 wrong names, 4 phantom toggles, 2 duplicates. | `PressDecider`: at every event the collector already receives it samples whether the left button went down since the previous one (`GetAsyncKeyState`, a read, no hook) and then reads the control under the pointer at once; that fresh read is the click's target, a control remembered from a PRESSED state only wins when the pointer has since moved more than 20 px; a state change on hover remembers nothing. A press is confirmed by a window or document opening or closing, a message, an inner tab change or a value the program fills; a confirmation that comes while the read is in flight waits for it. A tick box change needs a click or the focus on the box, else it is `by: app`. The same name within 10 s with nothing recorded between is one press. | `Hover_A_then_click_B_is_a_press_on_B_and_exit_then_close_prompt_is_a_press_on_exit`, `A_tick_box_flipped_by_a_re_read_is_not_a_toggle_and_a_repeated_name_with_nothing_between_is_one_press`; live: the Refresh clicks and the document's Exit |
| 4 | Grid cell editors report no value change when the user types, so no re-read followed the typed customer, product and quantities; one re-read came 45 s later from a plain Edit and read everything at once. 0 of the 5 values typed in Sales Order Entry were recorded. | The re-read also runs on focus moves inside the document and when Enter or Tab went down (sampled, not hooked) while a document is in front. The changed field that has, or just had, the focus is the user's typed value (`how: read`); the other changed fields are `by: app` (`FieldReads.Diff`). `ValuesTyped` now counts emitted typed events, `ValuesTypedReread` the ones found this way. | `The_cell_that_had_the_focus_is_the_typed_value_and_the_other_changed_cells_are_filled_by_the_program`, `Re_reads_of_qty_1_5_20_give_the_extension_formula_three_of_three_and_the_header_links_two_of_two` |
| 5 | The OCR fitter paired any label with any number: "Monsey, NY = Order Balance × -365.0667" (an address line), "Fax is copied from Include Area Code" and "Ship Complete is copied from Auto Prompt SDS" (tick boxes). | A label on a picture must be a field or column name of that screen's record (or look like one where no record exists: letters, no comma, not a street line, no phone), numbers must be money or quantity shaped (four decimals at most), constant factors between 0.001 and 1000 and never negative, and tick box states never take part in "copied from", on the picture path and the tree path alike. | `The_three_wrong_findings_of_the_v08_run_produce_nothing`, `Ocr_numbers_are_money_or_quantity_shaped_and_constant_factors_stay_between_a_thousandth_and_a_thousand`, `A_screen_without_a_record_still_learns_from_pictures_when_the_labels_look_like_fields` |
| 6 | A customer's city reached logic.md; the company name stayed in the main title; the program's fills carried phone numbers and e-mail addresses into the events. | Text from a picture never reaches memory except as a known field name. The profile's `titleMasks` ("DDI System Inform - *") turn the starred part of the title into "…" in every memory key. Values the program fills into phone, fax, e-mail, address, name and attention fields are kept as shapes in the events (`Masking.ForAppValue`). | `Personal_fields_the_program_fills_keep_their_shape_and_the_company_name_leaves_the_title` |
| 7 | Flow steps counted `by: app` values as typing; the vendor notes window the program pops up when a vendor loads gave 7 flows of 723 ms. | Program fills are not steps. A window that appears with no click, menu pick or selection in the 2 s before it was shown by the program: it is not a visit and starts no flow, and the visits around it join; the main window, prompts and session windows are never that. | `No_flow_starts_at_the_notes_window_the_program_opens_and_program_fills_are_not_steps`, `A_window_that_opens_two_seconds_after_the_last_click_is_shown_by_the_program` |
| 8 | health.md called a REST host from the program's log files "the data server"; it did not say that Sales Order Entry was never recorded nor that typing there was lost. | The primary backend is the profile's `backendKind` match, then any database server; REST hosts from log files are "services the program talks to". Health lists the documents opened but never recorded (from the collector's list and from the events) and the screens where the focus moved between fields but no typed value was recorded, in the plain-words section too. | `The_profiles_backend_is_the_data_server_and_log_file_hosts_are_services`, `Health_lists_the_documents_never_recorded_and_the_screens_typed_on_without_a_typed_value` |
| 9 | A grid cell typed into counted as a lookup ("12"); a hint over a field was its own label and value ("Remit-to: Same"); `ValuesTyped` counted re-read differences (96 for 19 typed values). | A `dropdown` needs a grid or list that was not on screen before the typing (by identity, not by size) and does not hold the cell itself; a tip over a field that reads "Label: value" is dropped (`Hints.IsFieldValueTip`); `ValuesTyped` counts one per typed value after the keystrokes settle. | `A_grid_that_holds_the_cell_being_typed_in_is_not_a_lookup_and_a_field_value_tip_is_dropped` |

The learner state is version 4 (the first learn after the upgrade rebuilds from the events). The sample program got an MDI
workspace (View › Workspace: two documents with inner tabs, a quantity field, a calculated total and Exit), and the live test
opens it, clicks a document's inner tab and its Exit button with the real mouse, and checks that the document in front was the
screen, that the inner tab click was a selection, that Exit was counted, and that the next document became the screen.

What this version cannot prove on the sample program: the Infragistics tab strip through IAccessible (the MSAA role PAGETAB
walk named in the brief) was not needed once the documents came from the MDI client, so it was not built; the Rocket U2 / UniVerse
data model reader (`UniVerseSource`) waits for a read-only account from the vendor. Both stay listed as next steps in the scorecard.

## Version 0.8: after the measured run against a tabbed-MDI ERP (2026-11-19)

A scripted 23-minute workload with exact ground truth (Infragistics on WinForms, UltraTabbedMdi workspace, UniVerse
backend) showed what 0.7 missed. Each item names the fix and its test; the fixtures under `tests/AppWatcher.Tests/Fixtures/inform`
are anonymised copies of that session's records (see the README there).

| # | What was measured | What changed | Test |
|---|---|---|---|
| 1 | 11 of ~90 presses; UIA 0, MSAA 0: the toolkit's buttons raise no event and take no focus | Press inference is a first-class path: the control under the pointer is read (ElementFromPoint, no hook) when the mouse is captured, a state changes or a menu starts; it, or the button with focus, is counted when a window, tab or message changes or a value arrives on another control within 1.5 s (`PressesPointer` in the status). One click is one event whichever path saw it. | live: `scripts\live-test.ps1` clicks the sample's Refresh button (raises nothing, only changes the title) with the real mouse, inside that window only, and checks the press |
| 2 | 4 of 6 tab documents recorded; events stayed credited to a closed tab; tab records held the window's toolbar and other documents | The tab strip is read after events (at most every 2 s, a true condition, filtered in code): the selected tab is the screen, only its document subtree is dumped (`TabDocuments.Document`), and a vanished or unselected tab clears the active tab and closes the document. Old records lose the parent's controls in the learner (`WithoutParentControls`). | `A_tab_screen_holds_only_the_selected_document_never_the_toolbar_or_other_documents`, `The_active_tab_follows_what_the_tab_strip_says_and_clears_when_the_tab_is_gone` |
| 3 | 0 of the order fills caught: grid cells and bound editors raise no value change | 300 ms after each typed value, press, tick, pick or focus move, the document's on-screen fields are read once (one cached batch, 300 fields at most) and diffed with the previous read; what changed and was not typed is a `value` with `by: app`, cells named by their column header (`FieldReads`). | `Typing_in_one_grid_cell_shows_as_the_program_filling_the_other_cells`, `The_learner_turns_re_read_fills_into_the_extension_formula_and_the_header_links` |
| 4 | Close? ×26, No Selection Specified ×2, an information box ×2: Dialogs 0, all three recorded as screens | The body Edit's inner wrapper editor folds into it, a body-named Edit counts whatever its read-only flag says, the window's own System/Minimize/Maximize buttons are allowed, one tick box is allowed. A profile's `dialogWindows` pattern now applies unless a grid, list, tree or tab says screen. The learner re-checks old records by shape and turns their `open` events into `dialog` events. | `The_three_real_message_boxes_are_dialogs_by_their_shape` on the real records |
| 5 | One 25-minute flow | Flows cut at the main window (the parent of the tab documents), at a prompt answered when a different window follows, at a document's close event, at Exit/Cancel then the main window; a run over 20 minutes and 40 steps is split at its prompts. Popup menus are not steps. | `The_real_session_gives_many_flows_not_one…`, `Flows_are_cut_at_the_main_window…`, `A_long_run_with_many_steps_is_split_at_its_prompts` |
| 6 | 0 formulas although Price Extension = Quantity × Net Price was on 3 pictures | `OcrLogic`: label → number pairs from each picture's text layer; a number that changed since the previous picture of the screen, or appeared right after a user action, is tested against the nearest other numbers; findings carry the `.ocr.json` and say "from the pictures' text". Processed once per picture (`OcrFiles` in the state). | `The_extension_formula_is_found_from_three_pictures_when_the_tree_gave_no_values` |
| 7 | "Not used" lists on tabs held the window's 40 toolbar buttons; a question asked about Bank Ledger on the Customer Order Pad | Tab records lose the parent's controls; button questions only for documents people opened, never the tabbed window itself, at most 10 per screen; one question per calculation with exceptions and per message seen once. | `Logic_report_lists_only_the_documents_own_controls_as_not_used…` |
| 8 | Three stock user DSNs and two unrelated TCP ports reported; the program's own XML ticked as data | Stock DSNs ignored; vendor config files under `ProgramData\<maker>\<product>\**` (found through the maker's name too) parsed for `<HostName>`, `<Port>`, `<AccountPath>` and the like; an open connection to the same host confirms the entry whatever port it uses now; the profile says the kind (`backendKind: universe`); the install folder only yields database files; XML namespaces are not services. | `The_vendor_config_under_ProgramData_names_the_server_and_the_stock_DSNs_are_ignored`, `Attributes_and_INI_style_lines_name_a_backend_too` |
| 9 | health.md said "Nothing is missing that I know of" | In plain words and in the first-hour notice: presses only by fallback (UIA 0, MSAA 0) after 50 windows; N windows that look like message boxes were recorded as screens; N% of screen reads failed (from 25%); a tab was selected but never recorded. | `Health_says_what_the_collector_could_not_do_instead_of_nothing_is_missing` on the real status |
| 10 | `appwatcher.cmd --detect` printed nothing when redirected | Already fixed in 0.6 (`Program.StdoutRedirected`, `start /b /wait`); re-checked on the 0.8 build: `--detect … > out.txt` fills the file. | `Cli_writes_to_a_pipe_and_to_a_file` |
| 11 | 0 lookups (the product lookup opened inside the window), 0 hints (Infragistics draws its tips in a plain small window) | A grid or list that is newly on screen under the field being typed in is a `dropdown`; an untitled window with a grid is one too. An untitled window under 40×400 px that appears while the pointer is still is a hint: text from the window, its accessible name, its UIA name or text child, else OCR of that small box; the control under the pointer is the subject. Never a screen, never a picture. | `A_lookup_list_that_opens_under_the_field_inside_the_window_is_a_dropdown`; live: the hover on the sample's Refresh button |
| 12 | Profile | `ddi-inform.json`: dialogWindows + "No Selection Specified", "*Specified", Confirm*/Warning*/Error* kept (safe now: a named dialog still has to look like one); sessionWindows + "DDI System Inform Login"; `backendKind`; the note says presses come from inference and fills from re-reads. | `The_profile_names_the_real_prompts_and_no_real_screen` |

Seen in the live test on the sample program with real mouse clicks (the test now makes a few, inside that window only): a real click on a
WinForms button does raise the older accessibility "pressed" state (`PressesMsaa`), which a posted click never did, so the earlier "not
proven" note was about the test's clicks, not the watcher; the hover tip of that button is read; and asking a WinForms tab item whether it
is selected selects it, so the tab strip is read by what is on screen, never through the selection pattern.

Also: untitled windows no longer produce `close` noise (113 such events in the run); popup menus that are windows are recorded
once with kind `menu` instead of as screens; `Describe` names a wrapper editor after its outer control (the grid cell's column,
the editor's id), so events say "Quant Ordered" instead of "Embeddable Text Box".

## Version 0.7: learning the logic like a trainee

Asked for by the client: a watcher that learns a program the way a new person does next to an experienced user,
useful on its own to fill gaps in a system that already exists.

- **Who changed a value** (`by: app` in the events): a value that changes in a field without keyboard focus (or a
  focused inner editor) was filled in by the program. These no longer count as typed and take no picture of their own.
- **What fills what** (`LogicLearner`): the program filling a field within 3 seconds after the user typed, chose,
  ticked or pressed something, reported once seen twice and after at least half of the times the cause happened.
- **Calculations on screen**: every number the program fills in is tested against sums, differences, products,
  ratios, percentages, discount and tax forms over the other numbers on that screen; the formula that keeps fitting
  is reported with its fit count, a formula with exceptions becomes a question, a fill nothing explains too.
- **Cause first**: a program that recalculates while the user types reports its total some milliseconds before the
  typed value; such a fill is processed after the value that caused it (found in the live test).
- **What each button leads to, hover tips** (help texts read when a tip shows, no mouse hook, never grid or list tips),
  **menus as they open** (`menu` events), and **what nobody used** (buttons, tabs, fields, menu items).
- **Questions I would ask an experienced user**, from what could not be explained.
- `memory/logic.md` for people and `memory/logic.json` for a program or an AI; both are in the export.
- Learn state version 2: an older state is relearned from the events once.
- Sample program: a Quote tab where the program fills the customer name from the code and calculates the line total.
- Live test (19 November): Line total = Quantity × Unit price × (1 − Discount % / 100), seen 5× of 5; typing
  Customer code fills Customer name, seen 3× of 3; the order number Post assigns was seen as filled by the program.
- Tests: `LogicTests` (6). 76 tests in all. Not proven live: hover tips (the test cannot hover without moving the mouse).

## Version 0.6: setup for an office user, not IT

Principle: the program decides, the user only confirms. Every default is the "collect everything that is
safe and local" choice.

- **Three-step first run** (`Forms/SetupWizard.cs`, `Forms/ProgramPicker.cs`): 1. Which program should I watch?
  (programs open now with icons and friendly names, "Find it on this PC...", a green box in plain words, a
  Details link for the technical facts); 2. Anything to keep private? (three ticks, all on, and the windows
  never to record); 3. a password twice and a big "Start watching". Data sources are not asked: everything
  found is ticked. File-access detection runs by itself for two minutes after the first start when the
  program is running; if Windows needs permission it asks once, and a No is skipped silently.
- **Settings, Simple and Advanced**: Simple has the program with a Change button, the three privacy choices,
  the learn time as a clock picker, and "What has been collected so far". Advanced has every other option with
  a plain explanation, the recommended value, and "Reset this page to recommended".
- **Tray menu in plain words**: Pause for an hour, Resume, Update what I learned, Make a report folder, Open
  the folder with everything collected, Settings..., Quit. Each notice says what happened and what to do next.
- **Maximal defaults** (`Core/Setup/SetupDefaults.cs`): tabs, MSAA and press inference on, pictures per state,
  OCR on, titles masked, 180 days, learn at 02:00 and one hour after the first start, every scanned source
  ticked except the ones that need a login. ODBC, SQL Server, SQL capture, ODBC tracing and AI stay off.
  The scanner no longer walks system folders when the program lives in one (Notepad in System32).
- **Dialogs by structure, never by title alone** (`DialogDetector`): a `dialogWindows` title only marks a
  window as a dialog when its structure could be one (few controls, only OK/Cancel-type buttons, a message).
  The DDI inForm profile no longer lists "Advanced Demand Forecasting" as ignored or "A/P Invoice Entry" as a dialog.
- **Redirected command-line output**: `appwatcher --detect <exe> > out.txt` fills the file (the console is
  attached only when the output is not already a file or a pipe).
- **First-hour report in plain words** (`Core/Setup/PlainReport.cs`): one hour after the first start
  `memory/health.md` is written even if no learn ran, and the same words appear in Settings and in one notice
  ("First hour done: 12 screens and 140 actions recorded. ...").
- `--setup <exe> <password> [profile]` uses the same defaults as the wizard.
- Tests: `EasySetupTests` (defaults, dialog structure, the first-hour text, redirected output). 70 tests in all.

Live test on the sample program (19 November), and what it changed:

- A window whose reading timed out once was never read again (no screen, picture or events while it stayed open).
  Registration now retries twice while the window is still open.
- The plain summary could say "0 pictures": a learn read the status file before the watcher had saved it. The tray
  now saves the status before every learn, and reading the file retries when it meets a save.
- The setup check said "Tables and rules: yes" for a plain data file it can only fingerprint. It now says "partly"
  and names what would give the tables.
- The .NET start-up files next to a program (`*.deps.json`, `*.runtimeconfig.json`) are no longer taken for data.
- The Windows permission prompt for file-access detection no longer blocks the tray menu while it is open.
- Press inference now measures from the moment a window appeared, and a data file written within 3 seconds of a
  button getting focus counts as a press of that button.
- Not proven: button presses in WinForms programs. WinForms raises no UI Automation Invoke event, and a click's
  pressed state is often over before it can be read. A test cannot make a real mouse click on this PC (no global
  input), and a posted click does not move focus, so the fallbacks can only be checked with a real user.
- The test now opens the blocking New Customer window with a posted click (a UI Automation Invoke stays pending
  until that window closes, and nothing can read the window meanwhile) and stops its own programs if a step fails.

## Version 0.5: OCR text layer and shortcut learning

- **Windows OCR on the pictures** (#1): the built-in Windows OCR (Windows.Media.Ocr, no install, stays on
  the PC) reads the text of each state picture and stores it next to the picture as `<pic>.png.ocr.json`
  (lines with boxes) and `.txt`. For apps that draw their own screens (Qt, Java, Delphi) this gives the
  labels and column headers the control tree could not. On by default, Settings to switch off; the
  export carries the OCR files, and the twin kit's `layout.py ocr-extra` uses them.
- **Shortcut learning** (#4): the keyboard shortcut of every button and menu item (AcceleratorKey /
  AccessKey) is in the screen record and now in a compact "Keyboard shortcuts:" line per screen in
  memory, so the twin keeps the power users' keys.
- Target framework moved to net8.0-windows10.0.19041.0 for the OCR API; still runs on Windows 10 1809+.
- Verified by the unit tests and a single-file publish; the OCR path is exercised at runtime on a real app.

## Version 0.4: micro-level states (client request)

- **One picture per state, not per screen**: after every press, tab switch, typed value, dialog and suggestion
  list, the window is pictured once it settled (650 ms). Filed page-wise under `screens/shots/<screen>/` with an
  `index.md` (picture, state, time, event). Duplicates are dropped by a coarse hash of the picture; caps of 25 per
  screen and 500 MB per app. Privacy tab: off / first / actions.
- **Suggestion lists** (autocomplete, lookups on key-up): the popup's items are recorded as a `dropdown` event
  (field, text typed so far, suggestions) plus a picture of the popup; the memory says which fields have lookups
  and how big they get; the probes export names them.
- **Grid results**: after a press, tab, typed value, open or close, the grids on the screen are read once and
  written as a `result` event (header line and first rows); the memory says what each action fills (columns,
  row counts, empty results).
- Tests: `StateCaptureTests` (5). Not run live: the sample app was not started again at the client's request.

## New in the same release

- **One picture per screen** (client idea): the app's window only, taken the first time a screen structure is
  seen, replaced when a richer tree of the same screen is found, never for login/password windows or excluded
  windows, never the desktop. `screens/shots/<screen>.png`, referenced from the screen record and copied into the export.
- `--detect <exe>` prints toolkit, profile and backends for any program file.
