# Screen: Vendor - # "…" Internal Notes

**Menu path:** (observed, not probed)
**Purpose:** window of the watched app, recorded by App Watcher the first time it opened.
**Reached from:** DDI System Inform - Sample Co
**Kind:** screen
**Title pattern:** Vendor - # "…" Internal Notes (first seen as "Vendor - 00001 "ACME" Internal Notes")
**Structure hash:** 8daf8a7ccba6cfcd
**Tree source:** uia-cached

## Fields

| Label | Type | Required | Default | Validation seen | AutomationId |
|---|---|---|---|---|---|
| Notes | Edit | ? | | | txtNotes_EmbeddableTextBox |
| [Editor] Edit Area | Edit | ? | | | [Editor] Edit Area |

## Actions

| Button / menu item | What it does | Changes data? | Pressed during discovery? | Shortcut |
|---|---|---|---|---|
| Line up | (Button) see memory/screens.md | ? | watched only |  |
| Line down | (Button) see memory/screens.md | ? | watched only |  |
| Edit | (Button) see memory/screens.md | ? | watched only |  |
| Exit | (Button) see memory/screens.md | ? | watched only |  |
| System | (MenuItem) see memory/screens.md | ? | watched only | Space |
| Close | (Button) see memory/screens.md | ? | watched only |  |

## Notes
- Recorded passively by App Watcher. Nothing was pressed by the watcher.

## Control tree (auto-generated)

Window: Vendor - 00001 "ACME" Internal Notes  
Captured: 2026-11-19 13:08  
Screenshot: shots/vendor-internal-notes/01-first-seen.png

| Depth | Type | Name | AutomationId | Position |
|---|---|---|---|---|
| 0 | Window | Vendor - 00001 "ACME" Internal Notes | NotesView | 648,152 624x727 |
| 1 | Pane |  | pnlWork | 656,183 608x688 |
| 2 | Pane |  | 1115962 | 656,183 608x688 |
| 3 | Edit |  | txtNotes | 660,188 600x632 |
| 4 | Edit |  | txtNotes_EmbeddableTextBox | 663,189 596x630 |
| 5 | ScrollBar | Vertical | NonClientVerticalScrollBar | 1242,189 17x630 |
| 6 | Button | Line up | UpButton | 1242,189 17x17 |
| 6 | Button | Line down | DownButton | 1242,802 17x17 |
| 4 | Edit |  | [Editor] Edit Area | 0,0 0x0 |
| 3 | Pane |  | pnlButtonsRight | 656,828 608x43 |
| 4 | Pane |  | 1050264 | 656,828 608x43 |
| 5 | Button | Edit | cmdEdit | 1038,831 112x35 |
| 5 | Button | Exit | cmdExit | 1153,831 108x35 |
| 1 | TitleBar |  | TitleBar | 672,155 592x28 |
| 2 | MenuBar | System | SystemMenuBar | 656,160 22x22 |
| 3 | MenuItem | System |  | 656,160 22x22 |
| 2 | Button | Close | Close | 1231,153 34x30 |
