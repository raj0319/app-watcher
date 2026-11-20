# Screen: DDI System Inform - Sample Co › Inventory Inquiry

**Menu path:** (observed, not probed)
**Purpose:** tab document of the watched app, recorded the first time the tab was selected.
**Reached from:** DDI System Inform - Sample Co
**Kind:** tab
**Title pattern:** DDI System Inform - Sample Co › Inventory Inquiry (first seen as "DDI System Inform - Sample Co › Inventory Inquiry")
**Structure hash:** 4b896ecbe27dfa77
**Tree source:** uia-cached

## Fields

| Label | Type | Required | Default | Validation seen | AutomationId |
|---|---|---|---|---|---|
| Product | Edit | ? | | | [Editor] Edit Area |
| Product Filter | Edit | ? | | | [Editor] Edit Area |
| Product Filter | Tree | ? | | | ColScrollRegion: 0, RowScrollRegion: 0 |
| Formula | Tree | ? | | | ColScrollRegion: 0, RowScrollRegion: 0 |
| Ship To | Edit | ? | | | [Editor] Edit Area |
| Ship To | Tree | ? | | | ColScrollRegion: 0, RowScrollRegion: 0 |
| Customer | Edit | ? | | | [Editor] Edit Area |
| As of | CheckBox | ? | | | chkShowGP |
| Col Scroll Region: 0, Row Scroll Region: 0 | Tree | ? | | | ColScrollRegion: 0, RowScrollRegion: 0 |

## Actions

| Button / menu item | What it does | Changes data? | Pressed during discovery? | Shortcut |
|---|---|---|---|---|
| Exit | (Button) see memory/screens.md | ? | watched only |  |
| Search | (Button) see memory/screens.md | ? | watched only |  |
| Category Prices | (TabItem) see memory/screens.md | ? | watched only |  |
| Competitive Prices | (TabItem) see memory/screens.md | ? | watched only |  |
| Inventory | (TabItem) see memory/screens.md | ? | watched only |  |
| Johnstone National | (TabItem) see memory/screens.md | ? | watched only |  |

## Notes
- Recorded passively by App Watcher. Nothing was pressed by the watcher.
- A tab document inside its parent window; recorded as a screen of its own.

## Control tree (auto-generated)

Window: DDI System Inform - Sample Co › Inventory Inquiry  
Captured: 2026-11-19 16:05  
Screenshot: shots/ddi-system-inform-sample-co-inventory-inquiry/01-first-seen.png

| Depth | Type | Name | AutomationId | Position |
|---|---|---|---|---|
| 0 | Window | Inventory Inquiry | InventoryInquiryView | 0,155 1920x855 |
| 1 | Pane |  | pnlWork | 0,155 1920x855 |
| 2 | Pane |  | 2688346 | 0,155 1920x855 |
| 3 | Pane |  | pnlButtonsRight | 0,964 1920x46 |
| 4 | Pane |  | 3343748 | 0,964 1920x46 |
| 5 | Button | Exit | cmdExit | 1809,967 108x35 |
| 3 | Text | Product: | lblFilter | 362,160 104x32 |
| 3 | Edit |  | srcFilter | 474,160 324x32 |
| 4 | Edit |  | [Editor] Edit Area | 477,161 320x30 |
| 4 | Button |  | [Editor] [ButtonsRight] Key : Redo | 0,0 0x0 |
| 4 | Button |  | [Editor] [ButtonsRight] Key : SearchDDB | 0,0 0x0 |
| 4 | Button |  | [Editor] [ButtonsRight] Key : Search | 0,0 0x0 |
| 4 | Button |  | [Editor] [ButtonsRight] Key : Add | 0,0 0x0 |
| 4 | Button |  | [Editor] [ButtonsRight] Index : 4 | 0,0 0x0 |
| 4 | Button |  | [Editor] [ButtonsRight] Index : 5 | 0,0 0x0 |
| 3 | Button | Search | cmdSearch | 828,158 112x35 |
| 3 | Text | Product Filter: | DdiLabel4 | 4,160 114x32 |
| 3 | ComboBox |  | cboProductFilters | 128,160 200x32 |
| 4 | Edit |  | [Editor] Edit Area | 129,161 181x30 |
| 4 | Button |  | [Editor] dropdown button | 310,161 17x30 |
| 4 | ListItem | Product | [Editor] [valuelist] ValueListItem 0 | 0,0 0x0 |
| 4 | ListItem | Product Line | [Editor] [valuelist] ValueListItem 1 | 0,0 0x0 |
| 4 | ListItem | Price Group | [Editor] [valuelist] ValueListItem 2 | 0,0 0x0 |
| 4 | ListItem | Vendor | [Editor] [valuelist] ValueListItem 3 | 0,0 0x0 |
| 4 | ListItem | Buy Line | [Editor] [valuelist] ValueListItem 4 | 0,0 0x0 |
| 3 | Pane |  | DdiPanel1 | 4,201 1911x750 |
| 4 | Pane |  | 2558030 | 4,201 1911x750 |
| 5 | Custom |  | gridInventoryInfo | 4,201 1221x750 |
| 6 | Custom |  | Data Area | 5,202 1219x748 |
| 7 | Tree |  | ColScrollRegion: 0, RowScrollRegion: 0 | 5,202 1219x748 |
| 8 | Header |  | Band Headers | 5,202 780x28 |
| 9 | HeaderItem | On Hand | [Column Header] OnHand | 485,202 75x28 |
| 9 | HeaderItem | Comm | [Column Header] Committed | 560,202 75x28 |
| 9 | HeaderItem | Avail | [Column Header] Available | 635,202 75x28 |
| 9 | HeaderItem | On P/O | [Column Header] OnPurchaseOrder | 710,202 75x28 |
| 9 | HeaderItem | UOM | [Column Header] UOMID | 425,202 60x28 |
| 9 | HeaderItem | Product | [Column Header] DataID | 5,202 150x28 |
| 9 | HeaderItem | Description | [Column Header] Description | 155,202 270x28 |
| 5 | Pane |  | 1575376 | 1225,201 15x750 |
| 5 | Pane |  | DdiPanel2 | 1240,201 675x750 |
| 6 | Pane |  | 2295736 | 1240,201 675x750 |
| 7 | Pane |  | 2885074 | 1240,628 675x15 |
| 7 | Pane |  | ctrlProductPricing | 1240,201 675x442 |
| 8 | Pane |  | pnlWork | 1240,201 675x442 |
| 9 | Pane |  | 2557256 | 1240,201 675x442 |
| 10 | Tab |  | tabPrices | 1240,543 675x100 |
| 11 | Pane |  | UltraTabPageControl23 | 1240,543 675x100 |
| 12 | Custom |  | gridCategoryPricing | 1240,543 675x100 |
| 13 | Custom |  | Data Area | 1241,544 673x98 |
| 14 | Tree |  | ColScrollRegion: 0, RowScrollRegion: 0 | 1241,544 673x98 |
| 15 | Header |  | Band Headers | 1241,544 19x28 |
| 11 | TabItem | Category Prices | TabItem Key Category | 0,0 0x0 |
| 11 | TabItem | Competitive Prices | TabItem Key Competitive | 0,0 0x0 |
| 10 | Text | L1 List: | lblL1 | 1244,206 114x32 |
| 10 | Group | Customer Pricing | gbPricing | 1240,247 675x286 |
| 11 | Edit |  | srcShipToID | 1686,281 220x32 |
| 12 | Edit |  | [Editor] Edit Area | 1689,282 216x30 |
| 12 | Button |  | [Editor] [ButtonsRight] Key : Redo | 0,0 0x0 |
| 12 | Button |  | [Editor] [ButtonsRight] Key : SearchDDB | 0,0 0x0 |
| 12 | Button |  | [Editor] [ButtonsRight] Key : Search | 0,0 0x0 |
| 12 | Button |  | [Editor] [ButtonsRight] Key : Add | 0,0 0x0 |
| 11 | Custom | Price Breaks | gridBreaks | 1702,322 204x157 |
| 12 | Custom |  | Data Area | 1703,353 202x125 |
| 13 | Tree |  | ColScrollRegion: 0, RowScrollRegion: 0 | 1703,353 202x125 |
| 14 | Header |  | Band Headers | 1703,353 19x28 |
| 11 | Text |  | lblQuotedDateAndBy | 1603,447 90x32 |
| 11 | Text | Date: | DdiLabel71 | 1540,447 63x32 |
| 11 | Text |  | lblQuotedPrice | 1363,447 168x32 |
| 11 | Text | Quoted Price: | DdiLabel72 | 1249,447 114x32 |
| 11 | Text |  | lblLastSaleDate | 1603,405 90x32 |
| 11 | Text | Date: | DdiLabel73 | 1540,405 63x32 |
| 11 | Text |  | lblLastSalePrice | 1363,405 168x32 |
| 11 | Text | Last Price: | DdiLabel74 | 1249,405 114x32 |
| 11 | Text |  | lblFormula | 1363,489 543x32 |
| 11 | Text | Formula: | lblCustomerFormula | 1249,489 114x32 |
| 11 | Text |  | lblNetPrice | 1363,364 168x32 |
| 11 | Text | Net Price: | DdiLabel76 | 1249,364 114x32 |
| 11 | Text |  | lblCustomerCategory | 1363,322 330x32 |
| 11 | Text | Category: | DdiLabel77 | 1249,322 114x32 |
| 11 | Edit |  | srcCustomerID | 1372,281 210x32 |
| 12 | Edit |  | [Editor] Edit Area | 1375,282 206x30 |
| 12 | Button |  | [Editor] [ButtonsRight] Key : Redo | 0,0 0x0 |
| 12 | Button |  | [Editor] [ButtonsRight] Key : SearchDDB | 0,0 0x0 |
| 12 | Button |  | [Editor] [ButtonsRight] Key : Search | 0,0 0x0 |
| 12 | Button |  | [Editor] [ButtonsRight] Key : Add | 0,0 0x0 |
| 11 | Text | Ship To: | DdiLabel68 | 1591,281 86x32 |
| 11 | Text | Customer: | DdiLabel69 | 1249,281 114x32 |
| 10 | Text |  | lblL1PriceDate | 1598,206 90x32 |
| 10 | Text |  | lblL1Price | 1358,206 168x32 |
| 10 | Text | As of: | lblDateOfPricing | 1536,206 63x32 |
| 10 | CheckBox |  | chkShowGP | 1774,206 128x32 |
| 7 | Pane |  | ctrlInventory | 1240,643 675x308 |
| 8 | Pane |  | pnlWorkWWW | 1240,643 675x308 |
| 9 | Pane |  | 2295422 | 1240,643 675x308 |
| 10 | Tab |  | tabInventory | 1240,643 675x308 |
| 11 | Pane |  | 1967788 | 1240,643 675x308 |
| 12 | Custom |  | 1117160 | 1240,643 675x308 |
| 13 | Custom |  | Data Area | 1240,643 675x308 |
| 14 | Tree |  | ColScrollRegion: 0, RowScrollRegion: 0 | 1240,643 675x308 |
| 15 | Header |  | Band Headers | 1240,643 652x28 |
| 16 | HeaderItem | Warehouse | [Column Header] FullDescriptionSingleLine | 1240,643 225x28 |
| 16 | HeaderItem | On Hand | [Column Header] OnHand | 1465,643 90x28 |
| 16 | HeaderItem | Cmttd | [Column Header] Committed | 1555,643 75x28 |
| 16 | HeaderItem | Avail | [Column Header] Available | 1630,643 75x28 |
| 16 | HeaderItem | On P/O | [Column Header] OnPurchaseOrder | 1705,643 75x28 |
| 16 | HeaderItem | P/O Expd Dt | [Column Header] PurchaseOrderExpectedDateEstOrConf | 1780,643 112x28 |
| 11 | TabItem | Inventory | TabItem Key DDI | 0,0 0x0 |
| 11 | TabItem | Johnstone National | TabItem Key ExternalInventory | 0,0 0x0 |
