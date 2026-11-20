# Screen: DDI System Inform - Sample Co › Purchase Order Inquiry

**Menu path:** (observed, not probed)
**Purpose:** tab document of the watched app, recorded the first time the tab was selected.
**Reached from:** DDI System Inform - Sample Co
**Kind:** tab
**Title pattern:** DDI System Inform - Sample Co › Purchase Order Inquiry (first seen as "DDI System Inform - Sample Co › Purchase Order Inquiry")
**Structure hash:** 2a0f05f71d8b07b8
**Tree source:** uia-cached

## Fields

| Label | Type | Required | Default | Validation seen | AutomationId |
|---|---|---|---|---|---|
| Col Scroll Region: 0, Row Scroll Region: 0 | Tree | ? | | | ColScrollRegion: 0, RowScrollRegion: 0 |
| Job | Edit | ? | | | txtJob_EmbeddableTextBox |
| [Editor] Edit Area | Edit | ? | | | [Editor] Edit Area |
| Purchase Order | Edit | ? | | | [Editor] Edit Area |
| Vendor | Edit | ? | | | [Editor] Edit Area |
| Product | Edit | ? | | | [Editor] Edit Area |
| P/O Number | Edit | ? | | | [Editor] Edit Area |

## Actions

| Button / menu item | What it does | Changes data? | Pressed during discovery? | Shortcut |
|---|---|---|---|---|
| Search | (Button) see memory/screens.md | ? | watched only |  |
| Print | (Button) see memory/screens.md | ? | watched only |  |
| Exit | (Button) see memory/screens.md | ? | watched only |  |
| Reset | (Button) see memory/screens.md | ? | watched only |  |
| Inquiry | (TabItem) see memory/screens.md | ? | watched only |  |
| Analysis | (TabItem) see memory/screens.md | ? | watched only |  |

## Notes
- Recorded passively by App Watcher. Nothing was pressed by the watcher.
- A tab document inside its parent window; recorded as a screen of its own.

## Control tree (auto-generated)

Window: DDI System Inform - Sample Co › Purchase Order Inquiry  
Captured: 2026-11-20 08:44  
Screenshot: shots/ddi-system-inform-sample-co-purchase-order-inquiry/01-first-seen.png

| Depth | Type | Name | AutomationId | Position |
|---|---|---|---|---|
| 0 | Window | Purchase Order Inquiry | PurchaseOrderInquiryView | 0,155 1920x855 |
| 1 | Pane |  | pnlWork | 0,155 1920x855 |
| 2 | Pane |  | 1573864 | 0,155 1920x855 |
| 3 | Pane |  | ctrlPOSearch | 0,155 1920x855 |
| 4 | Pane |  | pnlWork | 0,155 1920x855 |
| 5 | Pane |  | 1444766 | 1,156 1918x853 |
| 6 | Custom | Purchase Orders | gridSearchResults | 1,379 1921x582 |
| 7 | Custom |  | Data Area | 2,410 1919x550 |
| 8 | Tree |  | ColScrollRegion: 0, RowScrollRegion: 0 | 2,410 1919x550 |
| 9 | Header |  | Band Headers | 2,410 19x28 |
| 6 | Pane |  | pnlButtons | 1,963 1918x46 |
| 7 | Pane |  | 2752626 | 1,963 1918x46 |
| 8 | Button | Search | cmdSearch | 1574,966 112x35 |
| 8 | Button | Print | cmdPrint | 1689,966 112x35 |
| 8 | Button | Exit | cmdExit | 1804,966 112x35 |
| 8 | Button | Reset | cmdReset | 5,968 112x35 |
| 6 | Tab |  | DdiTabControl1 | 1,156 1918x218 |
| 7 | Pane |  | UltraTabPageControl2 | 73,157 1845x216 |
| 8 | Edit |  | txtJob | 239,286 150x32 |
| 9 | Edit |  | txtJob_EmbeddableTextBox | 244,291 144x23 |
| 9 | Edit |  | [Editor] Edit Area | 0,0 0x0 |
| 8 | Text | ACME | lblVendorResults | 399,245 579x32 |
| 8 | Text |  | lblProductResults | 473,203 504x32 |
| 8 | ComboBox |  | cboPOType | 239,328 303x32 |
| 9 | Edit |  | [Editor] Edit Area | 240,329 284x30 |
| 9 | Button |  | [Editor] dropdown button | 524,329 17x30 |
| 9 | ListItem | Open | [Editor] [valuelist] ValueListItem 0 | 0,0 0x0 |
| 9 | ListItem | Completed | [Editor] [valuelist] ValueListItem 1 | 0,0 0x0 |
| 9 | ListItem | Request for Quote | [Editor] [valuelist] ValueListItem 2 | 0,0 0x0 |
| 9 | ListItem | Expired Request for Quote | [Editor] [valuelist] ValueListItem 3 | 0,0 0x0 |
| 9 | ListItem | All | [Editor] [valuelist] ValueListItem 4 | 0,0 0x0 |
| 8 | Edit |  | srcVendor | 239,245 150x32 |
| 9 | Edit |  | [Editor] Edit Area | 242,246 146x30 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : Redo | 0,0 0x0 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : SearchDDB | 0,0 0x0 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : Search | 0,0 0x0 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : Add | 0,0 0x0 |
| 8 | Edit |  | srcProduct | 239,203 225x32 |
| 9 | Edit |  | [Editor] Edit Area | 242,204 221x30 |
| 9 | Button |  | [Editor] [ButtonsRight] Index : 0 | 0,0 0x0 |
| 9 | Button |  | [Editor] [ButtonsRight] Index : 1 | 0,0 0x0 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : Redo | 0,0 0x0 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : SearchDDB | 0,0 0x0 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : Search | 0,0 0x0 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : Add | 0,0 0x0 |
| 8 | Edit |  | txtPONo | 239,162 150x32 |
| 9 | Edit |  | [Editor] Edit Area | 242,163 146x30 |
| 8 | Text | Purchase Order: | DdiLabel5 | 77,328 153x32 |
| 8 | Text | Job: | DdiLabel4 | 77,286 153x32 |
| 8 | Text | Vendor: | DdiLabel3 | 77,245 153x32 |
| 8 | Text | Product: | DdiLabel2 | 77,203 153x32 |
| 8 | Text | P/O Number: | DdiLabel1 | 77,162 153x32 |
| 7 | TabItem | Inquiry | TabItem Index : 0 | 1,159 72x48 |
| 7 | TabItem | Analysis | TabItem Index : 1 | 1,210 71x48 |
