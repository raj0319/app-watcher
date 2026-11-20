# Screen: DDI System Inform - Sample Co › Projected Purchase Order

**Menu path:** (observed, not probed)
**Purpose:** tab document of the watched app, recorded the first time the tab was selected.
**Reached from:** DDI System Inform - Sample Co
**Kind:** tab
**Title pattern:** DDI System Inform - Sample Co › Projected Purchase Order (first seen as "DDI System Inform - Sample Co › Projected Purchase Order")
**Structure hash:** e520f6f827280bd8
**Tree source:** uia-cached

## Fields

| Label | Type | Required | Default | Validation seen | AutomationId |
|---|---|---|---|---|---|
| [Editor] Edit Area | Edit | ? | | | [Editor] Edit Area |
| Suppress Products | CheckBox | ? | | | chkSuppressProducts |
| Buyers | CheckBox | ? | | | chkPrimaryVendor |
| All Vendors | Tree | ? | | | ColScrollRegion: 0, RowScrollRegion: 0 |
| P/O Warehouse | CheckBox | ? | | | 60 |
| [Editor] Edit Area | Edit | ? | | | [Editor] Edit Area |
| [Editor] Edit Area | Edit | ? | | | [Editor] Edit Area |
| 60 | CheckBox | ? | | | 60 |
| [Editor] Edit Area | Edit | ? | | | [Editor] Edit Area |
| [Editor] Edit Area | Edit | ? | | | [Editor] Edit Area |
| 60 | CheckBox | ? | | | 60 |
| [Editor] Edit Area | Edit | ? | | | [Editor] Edit Area |
| [Editor] Edit Area | Edit | ? | | | [Editor] Edit Area |
| 60 | CheckBox | ? | | | 60 |
| [Editor] Edit Area | Edit | ? | | | [Editor] Edit Area |
| [Editor] Edit Area | Edit | ? | | | [Editor] Edit Area |
| Buy Zone | Edit | ? | | | [Editor] Edit Area |
| P/O Warehouse | Edit | ? | | | [Editor] Edit Area |
| Buyers | Edit | ? | | | [Editor] Edit Area |
| [Editor] Edit Area | Edit | ? | | | [Editor] Edit Area |
| Proj Critical Items Only | CheckBox | ? | | | chkProjCriticalItemsOnly |
| Turn Cycles | Edit | ? | | | [Editor] Edit Area |
| Lead Days | Edit | ? | | | [Editor] Edit Area |
| Starting Month For Pre-season Order | Edit | ? | | | [Editor] Edit Area |
| Project From Open SO | CheckBox | ? | | | chkProjectFromOpenSO |
| Project W Min Qty | CheckBox | ? | | | chkProjectWMinQty |
| Auto Link SO | CheckBox | ? | | | chkAutoLinkSO |
| Project To Meet Vendor | CheckBox | ? | | | chkProjectToMeetVendor |
| Lead Days | CheckBox | ? | | | chkDisplayZeros |
| Inc Uncommitted SO | CheckBox | ? | | | chkIncUncommittedSO |

## Actions

| Button / menu item | What it does | Changes data? | Pressed during discovery? | Shortcut |
|---|---|---|---|---|
| Generate Summary | (Button) see memory/screens.md | ? | watched only |  |
| Exit | (Button) see memory/screens.md | ? | watched only |  |
| Clear Criteria | (Button) see memory/screens.md | ? | watched only |  |
| Product Criteria | (Button) see memory/screens.md | ? | watched only |  |
| Projection | (TabItem) see memory/screens.md | ? | watched only |  |
| Vendor Returns | (TabItem) see memory/screens.md | ? | watched only |  |
| Stock Transfer | (TabItem) see memory/screens.md | ? | watched only |  |
| Criteria | (TabItem) see memory/screens.md | ? | watched only |  |
| Summary Results | (TabItem) see memory/screens.md | ? | watched only |  |
| Projection Results | (TabItem) see memory/screens.md | ? | watched only |  |

## Notes
- Recorded passively by App Watcher. Nothing was pressed by the watcher.
- A tab document inside its parent window; recorded as a screen of its own.

## Control tree (auto-generated)

Window: DDI System Inform - Sample Co › Projected Purchase Order  
Captured: 2026-11-19 16:07  
Screenshot: shots/ddi-system-inform-sample-co-projected-purchase-order/01-first-seen.png

| Depth | Type | Name | AutomationId | Position |
|---|---|---|---|---|
| 0 | Window | Projected Purchase Order | ProjectedPOView | 0,155 1920x855 |
| 1 | Pane |  | pnlWork | 0,155 1920x855 |
| 2 | Pane |  | 2557276 | 0,155 1920x855 |
| 3 | Pane |  | pnlButtonsRight | 0,964 1920x46 |
| 4 | Pane |  | 3081648 | 0,964 1920x46 |
| 5 | Button | Generate Summary | cmdGo | 1581,967 225x35 |
| 5 | Button | Exit | cmdExit | 1809,967 108x35 |
| 3 | Tab |  | tabMain | 4,160 1911x799 |
| 4 | Pane |  | tpcSettings | 5,161 1909x768 |
| 5 | Tab |  | tabOptions | 321,158 1278x777 |
| 6 | Pane |  | UltraTabPageControl5 | 322,159 1276x746 |
| 7 | Button | Clear Criteria | cmdClearCriteria | 562,301 165x35 |
| 7 | ComboBox |  | cboSearch | 326,430 150x32 |
| 8 | Edit |  | [Editor] Edit Area | 327,431 131x30 |
| 8 | Button |  | [Editor] dropdown button | 458,431 17x30 |
| 8 | ListItem | Vendor | [Editor] [valuelist] ValueListItem 0 | 0,0 0x0 |
| 8 | ListItem | Buy Line | [Editor] [valuelist] ValueListItem 1 | 0,0 0x0 |
| 7 | Text | Weeks: | DdiLabel44 | 846,479 123x32 |
| 7 | Text |  | lblWeeks | 978,479 81x31 |
| 7 | CheckBox |  | chkSuppressProducts | 326,811 513x32 |
| 7 | CheckBox |  | chkPrimaryVendor | 326,390 342x32 |
| 7 | Group | Warehouses | gbWarehouses | 1078,164 516x734 |
| 8 | Text |  | lblBuyZoneResult | 1404,198 182x32 |
| 8 | Text | Buy Zone: | DdiLabel4 | 1087,198 148x32 |
| 8 | Text | MAIN | lblPurchaseWhseResult | 1404,239 182x32 |
| 8 | Custom |  | gridWhse | 1087,281 498x607 |
| 9 | Custom |  | Data Area | 1088,282 496x605 |
| 10 | Tree |  | ColScrollRegion: 0, RowScrollRegion: 0 | 1088,282 496x605 |
| 11 | Header |  | Band Headers | 1088,282 496x28 |
| 12 | HeaderItem | Description | [Column Header] Description | 1241,282 343x28 |
| 12 | HeaderItem | Code | [Column Header] DataID | 1155,282 86x28 |
| 12 | HeaderItem |  | [Column Header] IsSelected | 1088,282 67x28 |
| 11 | DataItem | False | 0 | 1088,310 496x28 |
| 12 | CheckBox |  | 60 | 1088,309 67x29 |
| 12 | Edit | Code | 58 | 1154,309 87x29 |
| 13 | Edit |  | [Editor] Edit Area | 1157,310 83x27 |
| 12 | Edit | Description | 0 | 1240,309 344x29 |
| 13 | Edit |  | [Editor] Edit Area | 1243,310 340x27 |
| 11 | DataItem | True | 1 | 1088,338 496x28 |
| 12 | CheckBox |  | 60 | 1088,337 67x29 |
| 12 | Edit | Code | 58 | 1154,337 87x29 |
| 13 | Edit |  | [Editor] Edit Area | 1157,338 83x27 |
| 12 | Edit | Description | 0 | 1240,337 344x29 |
| 13 | Edit |  | [Editor] Edit Area | 1243,338 340x27 |
| 11 | DataItem | False | 2 | 1088,366 496x28 |
| 12 | CheckBox |  | 60 | 1088,365 67x29 |
| 12 | Edit | Code | 58 | 1154,365 87x29 |
| 13 | Edit |  | [Editor] Edit Area | 1157,366 83x27 |
| 12 | Edit | Description | 0 | 1240,365 344x29 |
| 13 | Edit |  | [Editor] Edit Area | 1243,366 340x27 |
| 11 | DataItem | False | 3 | 1088,394 496x28 |
| 12 | CheckBox |  | 60 | 1088,393 67x29 |
| 12 | Edit | Code | 58 | 1154,393 87x29 |
| 13 | Edit |  | [Editor] Edit Area | 1157,394 83x27 |
| 12 | Edit | Description | 0 | 1240,393 344x29 |
| 13 | Edit |  | [Editor] Edit Area | 1243,394 340x27 |
| 8 | Edit |  | srcWarehouseBuyZone | 1244,198 150x32 |
| 9 | Edit |  | [Editor] Edit Area | 1247,199 125x30 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : Redo | 0,0 0x0 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : SearchDDB | 0,0 0x0 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : Search | 1372,199 21x30 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : Add | 0,0 0x0 |
| 8 | Edit |  | srcPurchaseWhse | 1244,239 150x32 |
| 9 | Edit |  | [Editor] Edit Area | 1247,240 125x30 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : Redo | 0,0 0x0 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : SearchDDB | 0,0 0x0 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : Search | 1372,240 21x30 |
| 9 | Button |  | [Editor] [ButtonsRight] Key : Add | 0,0 0x0 |
| 8 | Text | P/O Warehouse: | DdiLabel5 | 1087,239 148x32 |
| 7 | Edit |  | cboBuyer | 487,348 416x32 |
| 8 | Edit |  | [Editor] Edit Area | 490,349 395x30 |
| 8 | Button |  | [Editor] [ButtonsRight] Key : DropDown | 885,349 17x30 |
| 7 | Text | Buyers: | DdiLabel13 | 326,348 152x32 |
| 7 | Edit |  | srcSearch | 486,431 150x32 |
| 8 | Edit |  | [Editor] Edit Area | 489,432 146x30 |
| 8 | Button |  | [Editor] [ButtonsRight] Key : Redo | 0,0 0x0 |
| 8 | Button |  | [Editor] [ButtonsRight] Key : SearchDDB | 0,0 0x0 |
| 8 | Button |  | [Editor] [ButtonsRight] Key : Search | 0,0 0x0 |
| 8 | Button |  | [Editor] [ButtonsRight] Key : Add | 0,0 0x0 |
| 7 | Text | All Vendors | lblSearchResult | 644,431 421x32 |
| 7 | CheckBox |  | chkProjCriticalItemsOnly | 326,770 342x32 |
| 7 | Edit |  | txtTurnCycles | 732,479 105x32 |
| 8 | Edit |  | [Editor] Edit Area | 735,480 101x30 |
| 7 | Edit |  | txtLeadDays | 486,479 105x32 |
| 8 | Edit |  | [Editor] Edit Area | 489,480 101x30 |
| 7 | ComboBox |  | cboPreseasonMonths | 646,853 150x32 |
| 8 | Edit |  | [Editor] Edit Area | 647,854 131x30 |
| 8 | Button |  | [Editor] dropdown button | 778,854 17x30 |
| 8 | ListItem | None | [Editor] [valuelist] ValueListItem 0 | 0,0 0x0 |
| 8 | ListItem | January | [Editor] [valuelist] ValueListItem 1 | 0,0 0x0 |
| 8 | ListItem | February | [Editor] [valuelist] ValueListItem 2 | 0,0 0x0 |
| 8 | ListItem | March | [Editor] [valuelist] ValueListItem 3 | 0,0 0x0 |
| 8 | ListItem | April | [Editor] [valuelist] ValueListItem 4 | 0,0 0x0 |
| 8 | ListItem | May | [Editor] [valuelist] ValueListItem 5 | 0,0 0x0 |
| 8 | ListItem | June | [Editor] [valuelist] ValueListItem 6 | 0,0 0x0 |
| 8 | ListItem | July | [Editor] [valuelist] ValueListItem 7 | 0,0 0x0 |
| 8 | ListItem | August | [Editor] [valuelist] ValueListItem 8 | 0,0 0x0 |
| 8 | ListItem | September | [Editor] [valuelist] ValueListItem 9 | 0,0 0x0 |
| 8 | ListItem | October | [Editor] [valuelist] ValueListItem 10 | 0,0 0x0 |
| 8 | ListItem | November | [Editor] [valuelist] ValueListItem 11 | 0,0 0x0 |
| 8 | ListItem | December | [Editor] [valuelist] ValueListItem 12 | 0,0 0x0 |
| 7 | Text | Starting Month For Pre-season Order: | DdiLabel6 | 326,853 310x32 |
| 7 | CheckBox |  | chkProjectFromOpenSO | 326,604 342x32 |
| 7 | CheckBox |  | chkProjectWMinQty | 326,687 342x32 |
| 7 | CheckBox |  | chkAutoLinkSO | 326,728 342x32 |
| 7 | CheckBox |  | chkProjectToMeetVendor | 326,562 342x32 |
| 7 | Text | Turn Cycles: | DdiLabel12 | 600,479 123x32 |
| 7 | CheckBox |  | chkDisplayZeros | 326,521 342x32 |
| 7 | CheckBox |  | chkIncUncommittedSO | 326,645 342x32 |
| 7 | Text | Lead Days: | DdiLabel11 | 326,479 150x32 |
| 7 | Button | Product Criteria | cmdCriteria | 736,301 165x35 |
| 7 | Pane |  | slCriteria | 328,164 576x175 |
| 8 | Group | Product Criteria | gbMain | 328,164 576x175 |
| 9 | Pane |  | DdiPanel1 | 330,190 572x147 |
| 10 | Pane |  | 2163922 | 330,190 572x147 |
| 6 | TabItem | Projection | TabItem Key Projection | 324,905 87x30 |
| 6 | TabItem | Vendor Returns | TabItem Key VendorReturns | 414,906 127x29 |
| 6 | TabItem | Stock Transfer | TabItem Key StockTransfer | 544,906 117x29 |
| 4 | TabItem | Criteria | TabItem Key Criteria | 7,929 66x30 |
| 4 | TabItem | Summary Results | TabItem Key SummaryResults | 0,0 0x0 |
| 4 | TabItem | Projection Results | TabItem Key ProjectionResults | 0,0 0x0 |
