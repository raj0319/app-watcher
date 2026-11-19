<!-- Anonymised copy of the scorecard written on the test PC after the v0.8 run: the company, the customer's city and the
     program's service hosts are replaced by stand-ins. The evidence it refers to is kept outside the repository. -->
# v0.8 against DDI Inform: the same workload, measured again

Date: 2026-11-19, 13:07 to 13:26. Watcher v0.8.0 (release exe from GitHub, sha256 (the published exe), installed with
`scripts\install.ps1`, set up with `--setup` and profile ddi-inform, old data moved to `C:\ProgramData\AppWatcher\backup-v0.7-20261119-1301`).
The workload was driven by the same UI Automation scripts as the v0.7 test (now with real mouse moves and clicks through
`mouse_event`, so hover tips can appear). Nothing was saved in Inform: every entry screen was left through Exit and the
"All changes will be lost. Continue?" prompt with OK. Ground truth is the drivers' own logs.

## The workload (18.5 minutes)

| Part | Minutes | What was done |
|---|---|---|
| A | 4 | Customer Master, Vendor Master, Product Master, Product Analysis from the Fast Click toolbar; one code typed in each; 5 sub-tabs clicked per document; 6 toolbar buttons hovered 1.2 s each; Exit; two global Contact Searches |
| B | 14.5 | Sales Order Entry (customer, product, qty 5 then 20, Add Line Item, partial code lookup, second line, 6 sub-tabs, Exit → Close? → OK), Purchase Order Entry, Customer Order Pad, Price Change, Price Matrix (menu), Price Book Report (menu, criteria → OK → No Selection Specified → No), Inventory Inquiry, Customer Ledger, Vendor Ledger, Projected Purchase Order (info box), Product Analysis, Executive Performance Summary (Protected Area box), Sales Order Inquiry, Purchase Order Inquiry |

Counted from the driver logs: 16 toolbar opens, 4 menu clicks, 18 Exit presses, 5 prompt buttons, 50 sub-tab clicks,
1 Add Line Item, 19 codes typed, 103 hovers, 3 on-screen recalculations of Price Extension (45.34 → 226.71 → 906.85).

## Scorecard: v0.8 against v0.7

| Evidence | Ground truth | v0.7 caught | v0.8 caught | Verdict |
|---|---|---|---|---|
| Tab documents recorded as screens | 12 distinct (Customer Ledger, Vendor Ledger and Product Analysis open the Master documents) | 4 (33%) | 7 (58%): Customer Master, Vendor Master, Product Master, Price Change, Price Matrix Master, Inventory Inquiry, Projected PO. Missing: **Sales Order Entry**, Purchase Order Entry, Customer Order Pad, Sales Order Inquiry, Purchase Order Inquiry | better, still misses the order screens |
| Popups and menus | 2 popups, 2 menus | 1 + 2 | 2 + 2, menus with access keys | good |
| Message boxes as dialogs | 5 (Close? ×2, No Selection Specified, Advanced Demand Forecasting, Protected Area) | 0 (recorded as screens) | 5 dialog events with the body text and the right cause in rules.md | fixed |
| Button and menu presses, correct name | ~96 clicks | 11 (10%) | 15 correct (16%): 10 of 16 toolbar opens, 2 of 2 menu items, 2 of 18 Exit, 1 of 5 prompt buttons, 0 of 50 sub-tabs, 0 of 1 Add Line Item. Plus 3 wrong names (Change Branch, Report, Cancel), 4 phantom toggles, 2 duplicates | slightly better, not solved |
| Values typed by the user | 19 codes + 4 quantities | most | 19 events, but **0 of the 5 typed in Sales Order Entry** (customer, product, qty 5, qty 20 missing; "12" caught) | worse on the grid screen |
| Fields filled by the program | order header ×6, line ×3, extension ×3, master data | 9 fields, none of the order | 168 `by: app` values, cells named by column header; the 6 order header fills (Ship To, Branch, Writer, Customer, Ship Via, Whse) and the line fills are there, but read once, 45 s late, credited to the main window; Price Extension read once as 0 | mechanism works, timing does not |
| On-screen calculations | Extension = Qty × Price (3×) | 0 | 0 correct; 3 wrong ones from OCR and tick boxes ("<city>, NY = Order Balance × -365.0667", "Fax is copied from Include Area Code", "Ship Complete is copied from Auto Prompt SDS") | noise instead of the formula |
| Lookup lists | 1 | 0 | 1 correct (`100` → 6 suggestions) + 1 false (`12` typed in a grid cell) | fixed, one false positive |
| Hover tips | 3 tips actually shown (probe: New/Edit/Exit/Report/Labels show no tip; Fast Click tips need 150 ms and the clicks were faster) | 0 | 3 correct (Change Branch, Procurement, "Click to add a new Line Item row") + 1 false ("Remit-to: Same" on a field) | fixed |
| Flows | 16 document visits | 1 flow of 25 min | 8 flows, cut at the main window | fixed in shape; steps list program fills as "type …" |
| Pictures per state | every state | 67 | 40 + 40 OCR pages, 18 duplicates skipped, one folder per document | good |
| Untitled-window close noise | 0 | 113 events | 0 | fixed |
| Title masking | vendor and customer names | works | works (`Vendor - # "…" Internal Notes`); the company name stays in the main title; **"<city>, NY" (a customer's city, from OCR) is in logic.md** | one privacy leak |
| Tree dumps | 60 | 38% failed | 18 of 60 failed (30%) | slightly better |
| Backend discovery | UniVerse 192.0.2.10, account DDI | 3 stock DSNs, 2 wrong ports | universe 192.0.2.10 / DDI from installedapplication.config, confirmed by the TCP peer; stock DSNs gone; config XML no longer ticked. Also two REST hosts from the program's log files, and health.md calls one of them "the data server" | fixed, wrong primary in health |
| Health verdict | several paths silent | "Nothing is missing" | "presses only through fallbacks (UIA 0, MSAA 0, inferred 17)", "30% of reads failed" | honest now; does not say Sales Order Entry was never recorded |
| CLI output when redirected | — | nothing | `--setup`, `--detect`, `--learn` fill the file | fixed |
| Collector status counters | — | — | `ValuesTyped: 91` while the event file holds 19 typed values | counter counts something else |

Score in one line: 7 of the 12 items of the v0.8 list work on the real program (dialogs, lookups, hints, flows, backends, health,
CLI, noise), 3 work in part (tab documents, re-reads, press inference), 2 do not (formulas, logic noise). The order-entry screen,
which carries most of the logic, is still invisible as a screen and its typed values were lost.

## Why (checked against the program, not guessed)

1. **The document strip is not a Tab control.** Probe with one document open: the main window's only `Tab` controls are the
   document's inner tab controls (Setup | Ledger | … and Main | General | Credit | …), and there is no `TabItem` named after the
   document anywhere in the control view. The document is an MDI child: `ControlType.Window`, AutomationId `1`, Name "Price Change",
   parent Pane class `WindowsForms10.MDICLIENT.app.0…`, grandparent the main window. The 7 `select TabItem` events v0.8 did get came from
   UI Automation selection events that the strip raises for some documents only; the 2-second poll never found the strip
   (`tab strip not read: UIA Timeout` once, then silently the inner tab control). The five missing documents are exactly the ones that
   raised no event.
2. **The pointer read is stale.** `ReadPointer` runs on mouse capture, state changes and menu starts. Infragistics toolbar buttons raise
   a state change when the pointer *hovers* them, so after "hover Report, click Exit" the remembered control is Report and the
   document closing is credited to it (events 13:09:29 Report, 13:14:16 Cancel, 13:07:39 Change Branch, where the pointer rested
   before the driver's first click). The fresh read under the pointer is tried last, not first. Exit is caught 2 of 18 times because the
   MDI child closing is not one of the changes that trigger the inference.
3. **Re-reads are not triggered by typing into a grid cell.** In Sales Order Entry the typed customer, product and quantities raised no
   value event (the cell editor is an `_EmbeddableTextBox` that reports nothing), so no 300 ms re-read followed them; the single re-read
   at 13:12:30 was triggered by a value that did report ("12" in a plain Edit) and it read everything at once, 45 s after the fills.
   The 3 Price Extension changes were therefore never seen, and the OCR fitter did not find them either.
4. **The OCR formula fitter pairs any label with any number.** "<city>, NY" (an address line) × "Order Balance" with a factor of
   −365 is accepted as a formula seen 3 of 4 times; a tick box copying another tick box's On/Off counts as "copied from".
5. **Flow steps count `by: app` values as typing** ("type 12 Mon Dmnd (1), type 12 Mon Hits (1)" on Product Master).
6. **A window the program opens by itself** (Vendor Internal Notes appears whenever a vendor is loaded) starts a flow each time
   (7 flows of 723 ms).
7. **Sub-tab clicks** (Main, General, Credit, …) raise nothing and are not polled: 0 of 50.
8. **`ValuesTyped` counts re-read differences**, not emitted typed events.

## Fixes for 0.9, in order (paste this section to the AI that changes the code)

1. **Tab documents through the MDI client, not the tab strip.** When a window whose parent's class name contains `MDICLIENT` is created,
   shown, activated or destroyed (EVENT_OBJECT_CREATE / SHOW / FOCUS / DESTROY on that hwnd, out of context, no hook), treat it as the
   document: screen title `<main title> › <child title>`, dump the child's own subtree (the `Window` element with AutomationId `1`),
   active = `GetTopWindow(mdiClientHwnd)` re-read at most every 2 s after events, closed when its hwnd is destroyed. Keep the TabItem
   path only as a second source. Never take a `Tab` control that lies inside a document as the document strip (the two inner
   tab controls here have AutomationIds 1574634 and 11863606). Test: a fixture with a main window, an MDICLIENT pane and two child
   windows "Price Change" and "Sales Order Entry", the second on top: the screen is `… › Sales Order Entry`; destroying it makes
   `… › Price Change` the screen; destroying both clears it.
2. **Inner tabs as `select` events.** After any click (see 3) or focus change inside a document, read which `TabItem` of each inner tab
   control says selected (true condition, filter in code) and emit `select` when it changed. Test: 50 sub-tab clicks → 50 selects.
3. **Press inference from a real click, fresh pointer first.** Sample `GetAsyncKeyState(VK_LBUTTON)` at every WinEvent and UIA event
   the collector already receives (bit 0 = "pressed since the last call"; a read, not a hook). When it says a click happened since the
   previous event, read `ElementFromPoint` *now* and prefer it over the remembered pointer; keep the remembered one only when the pointer
   has since moved more than 20 px. On `EVENT_OBJECT_STATECHANGE` remember the control only when its state is PRESSED, never for a
   hover highlight. Add "MDI child destroyed" and "dialog opened" to the changes that confirm a press, so Exit → Close? counts.
   A `toggle` needs the click flag or focus on the tick box; a tick box that changes during a re-read is `by: app`, never a press
   (4 phantom "Additional Information" toggles in this run). Drop a press that repeats the same name within 10 s with no event between.
   Tests: hover A then click B → press B; re-read flips a tick box → no toggle; Exit then Close? → press Exit.
4. **Re-read on keystrokes in grid cells.** Also trigger the 300 ms re-read when the focus moves inside a document (cell to cell) and
   when `GetAsyncKeyState(VK_RETURN)` / `VK_TAB` report a key since the last event while a document is in front. Emit the diff of the cell
   that had focus as the user's typed value (`by` absent) and the other changed cells as `by: app`; read the document's grid once per
   trigger, 300 fields at most, as now. This gives the typed customer/product/qty events and three reads of Price Extension in the
   order they happened. Test: synthetic re-reads qty 1 → 5 → 20 with extension 45.34 → 226.71 → 906.85 → formula `Extension = Qty × Net Price`
   seen 3× of 3.
5. **OCR formulas only between fields that look like fields.** A label must match a field or column name of the same screen record
   (or a label the tree knows), not any OCR line; both numbers must be money or quantity shaped; factors between 0.001 and 1000 only;
   no negative factors unless a minus is visible; never use lines with letters and a comma (addresses) as labels; tick boxes never
   take part in "copied from". Everything that comes from OCR carries `from the pictures' text` and must be rejected when the tree
   later contradicts it. Test: the three wrong findings of this run (in evidence) produce nothing.
6. **Privacy of OCR-derived text.** Any OCR line used as a label or a value in memory must pass the same shape masking as typed values
   (`Masking.Shape`): "<city>, NY" must never reach logic.md. Mask the company name in the main window title in memory too.
7. **Flows.** `by: app` values are not steps; a window that opens with no user action in the 2 s before it (Vendor Internal Notes) is
   "shown by the program" and neither starts a flow nor counts as a visit. Test: the real event file → no flow starting at the notes window.
8. **Health and backends.** The primary backend is the profile's `backendKind` match (universe 192.0.2.10); hosts from the program's
   own log files are "services the program talks to" (shop.example, support.example), not "the data server". Health must list the
   documents that were opened but never recorded (from the MDI child titles) and the screens whose typed values were 0 although the
   user typed there (focus moved between its cells).
9. **Small ones.** A `dropdown` needs a list or grid that was *not* on screen before the typing (the order grid is always there, so "12"
   is not a lookup). A hint whose subject is an Edit and whose text is a label pair on the same screen ("Remit-to: Same") is dropped.
   `ValuesTyped` counts emitted typed events. Hover tips in the live test: use `mouse_event` moves and wait 200 ms; SetCursorPos shows nothing.

## Files

- Data and memory of this run: `C:\ProgramData\AppWatcher\data\ddi-inform\` (the watcher's log of the run is copied there as `watcher-v0.8-run.log`)
- v0.7 data for comparison: `C:\ProgramData\AppWatcher\backup-v0.7-20261119-1301\`
- Evidence bundle (data folder, driver scripts, driver logs, probe logs, setup/learn output): `evidence\app-watcher-v0.8-inform-evidence-2026-11-19.zip`
- Probes that back section "Why": `tabstrip-probe2.log` (MDI child), `hover-probe.log` (which buttons show tips, inner tab controls) in the bundle
