# Screen: DDI System Inform - Sample Co › Sales Order Inquiry

**Menu path:** (observed, not probed)
**Purpose:** tab document of the watched app, recorded the first time the tab was selected.
**Reached from:** DDI System Inform - Sample Co
**Kind:** tab
**Title pattern:** DDI System Inform - Sample Co › Sales Order Inquiry (first seen as "DDI System Inform - Sample Co › Sales Order Inquiry")
**Structure hash:** ee6a60d67e4b16d8
**Tree source:** uia-cached

## Fields

| Label | Type | Required | Default | Validation seen | AutomationId |
|---|---|---|---|---|---|
| Col Scroll Region: 0, Row Scroll Region: 0 | Tree | ? | | | ColScrollRegion: 0, RowScrollRegion: 0 |
| Job Name | Edit | ? | | | [Editor] Edit Area |
| Print Type | Edit | ? | | | [Editor] Edit Area |
| Sales Order | Edit | ? | | | [Editor] Edit Area |
| Manual Ship To Info | Edit | ? | | | [Editor] Edit Area |
| P/O Number | Edit | ? | | | txtPONumber_EmbeddableTextBox |
| [Editor] Edit Area | Edit | ? | | | [Editor] Edit Area |
| Customer | Edit | ? | | | [Editor] Edit Area |
| Product | Edit | ? | | | [Editor] Edit Area |
| Order # | Edit | ? | | | [Editor] Edit Area |

## Actions

| Button / menu item | What it does | Changes data? | Pressed during discovery? | Shortcut |
|---|---|---|---|---|
| Search | (Button) see memory/screens.md | ? | watched only |  |
| Print | (Button) see memory/screens.md | ? | watched only |  |
| Report | (Button) see memory/screens.md | ? | watched only |  |
| Exit | (Button) see memory/screens.md | ? | watched only |  |
| Reset | (Button) see memory/screens.md | ? | watched only |  |
| Inquiry | (TabItem) see memory/screens.md | ? | watched only |  |
| Analysis | (TabItem) see memory/screens.md | ? | watched only |  |

## Notes
- Recorded passively by App Watcher. Nothing was pressed by the watcher.
- A tab document inside its parent window; recorded as a screen of its own.

## Control tree (auto-generated)

Window: DDI System Inform - Sample Co › Sales Order Inquiry  
Captured: 2026-11-19 16:09  
Screenshot: shots/ddi-system-inform-sample-co-sales-order-inquiry/01-first-seen.png

| Depth | Type | Name | AutomationId | Position |
|---|---|---|---|---|
| 0 | Window | Sales Order Inquiry | SalesOrderInquiryView | 0,155 1920x855 |
| 1 | Pane |  | pnlWork | 0,155 1920x855 |
| 2 | Pane |  | 1771604 | 0,155 1920x855 |
| 3 | Pane |  | ctrlSOSearch | 0,155 1920x855 |
| 4 | Pane |  | pnlWork | 0,155 1920x855 |
| 5 | Pane |  | 1444308 | 0,155 1920x855 |
| 6 | Custom | Sales Orders | gridSearchResults | 0,458 1924x504 |
| 7 | Custom |  | Data Area | 1,489 1922x472 |
| 8 | Tree |  | ColScrollRegion: 0, RowScrollRegion: 0 | 1,489 1922x472 |
| 9 | Header |  | Band Headers | 1,489 19x28 |
| 6 | Pane |  | pnlButtons | 0,964 1920x46 |
| 7 | Pane |  | 2229904 | 0,964 1920x46 |
| 8 | Button | Search | cmdSearch | 1460,967 112x35 |
| 8 | Button | Print | cmdPrint | 1575,967 112x35 |
| 8 | Button | Report | cmdReport | 1690,967 112x35 |
| 8 | Button | Exit | cmdExit | 1805,967 112x35 |
| 8 | Button | Reset | cmdReset | 4,969 112x35 |
| 6 | Tab |  | DdiTabControl1 | 0,155 1924x298 |
| 7 | Pane |  | UltraTabPageControl1 | 72,156 1851x296 |
| 8 | Text |  | lblJobResults | 426,325 494x32 |
| 8 | Edit |  | srcJobID | 267,328 150x32 |
| 9 | Edit |  | [Editor] Edit Area | 270,329 125x30 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : Redo | 0,0 0x0 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : SearchDDB | 0,0 0x0 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : Search | 395,329 21x30 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : Add | 0,0 0x0 |
| 8 | ComboBox |  | cboSOPrintOptions | 694,408 225x32 |
| 9 | Edit |  | [Editor] Edit Area | 695,409 206x30 |
| 9 | Button |  | [Editor] dropdown button | 901,409 17x30 |
| 9 | ListItem | Sales Order | [Editor] [valuelist] ValueListItem 0 | 0,0 0x0 |
| 9 | ListItem | Shipping Confirmation | [Editor] [valuelist] ValueListItem 1 | 0,0 0x0 |
| 9 | ListItem | Order Recap | [Editor] [valuelist] ValueListItem 2 | 0,0 0x0 |
| 9 | ListItem | Order Confirmation | [Editor] [valuelist] ValueListItem 3 | 0,0 0x0 |
| 9 | ListItem | Worksheet | [Editor] [valuelist] ValueListItem 4 | 0,0 0x0 |
| 8 | Text | Print Type: | lblPrintType | 570,408 116x32 |
| 8 | Text | Active/Good | 920654 | 928,242 153x32 |
| 8 | ComboBox |  | cboSalesOrder | 267,408 225x32 |
| 9 | Edit |  | [Editor] Edit Area | 268,409 206x30 |
| 9 | Button |  | [Editor] dropdown button | 474,409 17x30 |
| 9 | ListItem | Open | [Editor] [valuelist] ValueListItem 0 | 0,0 0x0 |
| 9 | ListItem | Completed | [Editor] [valuelist] ValueListItem 1 | 0,0 0x0 |
| 9 | ListItem | Backorders | [Editor] [valuelist] ValueListItem 2 | 0,0 0x0 |
| 9 | ListItem | Quotes | [Editor] [valuelist] ValueListItem 3 | 0,0 0x0 |
| 9 | ListItem | Expired Quotes | [Editor] [valuelist] ValueListItem 4 | 0,0 0x0 |
| 9 | ListItem | Recurring | [Editor] [valuelist] ValueListItem 5 | 0,0 0x0 |
| 9 | ListItem | Ready to Pick | [Editor] [valuelist] ValueListItem 6 | 0,0 0x0 |
| 9 | ListItem | All | [Editor] [valuelist] ValueListItem 7 | 0,0 0x0 |
| 8 | Edit |  | txtManualShipToInfo | 267,367 652x32 |
| 9 | Edit |  | [Editor] Edit Area | 270,368 648x30 |
| 8 | Edit |  | txtPONumber | 267,284 150x32 |
| 9 | Edit |  | txtPONumber_EmbeddableTextBox | 272,289 144x23 |
| 9 | Edit |  | [Editor] Edit Area | 0,0 0x0 |
| 8 | Text | Test Customer | DdiAccentLabel1 | 426,242 494x32 |
| 8 | Edit |  | srcCustomer | 267,242 150x32 |
| 9 | Edit |  | [Editor] Edit Area | 270,243 146x30 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : Redo | 0,0 0x0 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : SearchDDB | 0,0 0x0 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : Search | 0,0 0x0 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : Add | 0,0 0x0 |
| 8 | Text |  | lblProductResult | 501,201 580x32 |
| 8 | Edit |  | srcProduct | 267,201 225x32 |
| 9 | Edit |  | [Editor] Edit Area | 270,202 221x30 |
| 9 | Button |  | [Editor] [ButtonsRight] Index : 0 | 0,0 0x0 |
| 9 | Button |  | [Editor] [ButtonsRight] Index : 1 | 0,0 0x0 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : Redo | 0,0 0x0 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : SearchDDB | 0,0 0x0 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : Search | 0,0 0x0 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : Add | 0,0 0x0 |
| 8 | Edit |  | txtOrderNumber | 267,159 150x32 |
| 9 | Edit |  | [Editor] Edit Area | 270,160 146x30 |
| 8 | Text | Sales Order: | DdiLabel7 | 76,408 182x32 |
| 8 | Text | Manual Ship To Info: | DdiLabel6 | 76,367 182x32 |
| 8 | Text | Job Name: | DdiLabel5 | 76,325 182x32 |
| 8 | Text | P/O Number: | DdiLabel4 | 76,284 182x32 |
| 8 | Text | Customer: | DdiLabel3 | 76,242 182x32 |
| 8 | Text | Product: | DdiLabel2 | 76,201 182x32 |
| 8 | Text | Order #: | DdiLabel1 | 76,159 182x32 |
| 7 | TabItem | Inquiry | TabItem Index : 0 | 0,158 72x48 |
| 7 | TabItem | Analysis | TabItem Index : 1 | 0,209 71x48 |
