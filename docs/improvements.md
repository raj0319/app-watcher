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
