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
