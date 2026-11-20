# Screen: DDI System Inform - Sample Co › Customer Order Pad

**Menu path:** (observed, not probed)
**Purpose:** tab document of the watched app, recorded the first time the tab was selected.
**Reached from:** DDI System Inform - Sample Co
**Kind:** tab
**Title pattern:** DDI System Inform - Sample Co › Customer Order Pad (first seen as "DDI System Inform - Sample Co › Customer Order Pad")
**Structure hash:** 6660fc4e67d75842
**Tree source:** uia-cached

## Fields

| Label | Type | Required | Default | Validation seen | AutomationId |
|---|---|---|---|---|---|
| Cut Off Date | Edit | ? | | | [Editor] Edit Area |
| Hits | Edit | ? | | | [Editor] Edit Area |
| Filter Type | ComboBox | ? | | | optFilterType |
| Show All | RadioButton | ? | | | [Editor] [valuelist] ValueListItem 0 |
| Due To Buy | RadioButton | ? | | | [Editor] [valuelist] ValueListItem 1 |
| Quoted | RadioButton | ? | | | [Editor] [valuelist] ValueListItem 2 |
| History | RadioButton | ? | | | [Editor] [valuelist] ValueListItem 3 |
| Hidden | RadioButton | ? | | | [Editor] [valuelist] ValueListItem 4 |
| Group By | ComboBox | ? | | | optGroupBy |
| Product | RadioButton | ? | | | [Editor] [valuelist] ValueListItem 0 |
| Product Line | RadioButton | ? | | | [Editor] [valuelist] ValueListItem 1 |
| Primary Vendor | RadioButton | ? | | | [Editor] [valuelist] ValueListItem 2 |
| 24 Month#Hits | RadioButton | ? | | | [Editor] [valuelist] ValueListItem 3 |
| Col Scroll Region: 0, Row Scroll Region: 0 | Tree | ? | | | ColScrollRegion: 0, RowScrollRegion: 0 |
| Ship To | Edit | ? | | | srcShipToID_EmbeddableTextBox |
| [Editor] Edit Area | Edit | ? | | | [Editor] Edit Area |
| Customer | Edit | ? | | | [Editor] Edit Area |
| Order | Edit | ? | | | [Editor] Edit Area |

## Actions

| Button / menu item | What it does | Changes data? | Pressed during discovery? | Shortcut |
|---|---|---|---|---|
| Create Quote | (Button) see memory/screens.md | ? | watched only |  |
| Create Order | (Button) see memory/screens.md | ? | watched only |  |
| Search | (Button) see memory/screens.md | ? | watched only |  |
| Exit | (Button) see memory/screens.md | ? | watched only |  |
| Find | (Button) see memory/screens.md | ? | watched only |  |
| Report | (Button) see memory/screens.md | ? | watched only |  |

## Notes
- Recorded passively by App Watcher. Nothing was pressed by the watcher.
- A tab document inside its parent window; recorded as a screen of its own.

## Control tree (auto-generated)

Window: DDI System Inform - Sample Co › Customer Order Pad  
Captured: 2026-11-19 16:02  
Screenshot: shots/ddi-system-inform-sample-co-customer-order-pad/01-first-seen.png

| Depth | Type | Name | AutomationId | Position |
|---|---|---|---|---|
| 0 | Window | Customer Order Pad | CustomerOrderPadView | 0,155 1920x855 |
| 1 | Pane |  | pnlWork | 0,155 1920x855 |
| 2 | Pane |  | 3606684 | 0,155 1920x855 |
| 3 | Pane |  | pnlButtonsRight | 0,964 1920x46 |
| 4 | Pane |  | 2099132 | 0,964 1920x46 |
| 5 | Button | Create Quote | cmdCreateQuote | 1400,967 144x35 |
| 5 | Button | Create Order | cmdCreateOrder | 1547,967 144x35 |
| 5 | Button | Search | cmdSearch | 1694,967 112x35 |
| 5 | Button | Exit | cmdExit | 1809,967 108x35 |
| 5 | Button | Find | cmdFind | 126,969 112x35 |
| 5 | Button | Report | cmdReport | 4,969 112x35 |
| 3 | Pane |  | ctrlTableLayoutPanel | 0,155 1920x855 |
| 4 | Group | Options | gbButtonBox | 1688,160 228x799 |
| 5 | Pane |  | gbpnlButtonBox | 1690,165 200x792 |
| 6 | Group | Filter | gbFilter | 1690,479 200x478 |
| 7 | Group | History | gbHistory | 1699,791 168x118 |
| 8 | Text | Cut Off Date | DdiLabel4 | 1708,820 146x32 |
| 8 | Edit |  | deHistoryCutOffDate | 1708,853 146x32 |
| 9 | Edit |  | [Editor] Edit Area | 1709,854 127x30 |
| 9 | Button |  | [Editor] dropdown button | 1836,854 17x30 |
| 7 | Edit |  | neHits | 1752,750 72x32 |
| 8 | Edit |  | [Editor] Edit Area | 1753,751 53x30 |
| 8 | Button |  | [Editor] [Spin Button] Up | 1806,751 17x15 |
| 8 | Button |  | [Editor] [Spin Button] Down | 1806,766 17x15 |
| 7 | Text | Hits: | DdiLabel3 | 1699,750 44x32 |
| 7 | ComboBox |  | optFilterType | 1699,513 168x228 |
| 8 | RadioButton | Show All | [Editor] [valuelist] ValueListItem 0 | 1705,517 85x31 |
| 8 | RadioButton | Due To Buy | [Editor] [valuelist] ValueListItem 1 | 1705,548 105x31 |
| 8 | RadioButton | Quoted | [Editor] [valuelist] ValueListItem 2 | 1705,579 77x31 |
| 8 | RadioButton | History | [Editor] [valuelist] ValueListItem 3 | 1705,610 73x31 |
| 8 | RadioButton | Hidden | [Editor] [valuelist] ValueListItem 4 | 1705,641 75x31 |
| 6 | Group | Group By | gbGroupBy | 1694,174 191x290 |
| 7 | ComboBox |  | optGroupBy | 1703,208 168x242 |
| 8 | RadioButton | Product | [Editor] [valuelist] ValueListItem 0 | 1709,212 79x31 |
| 8 | RadioButton | Product Line | [Editor] [valuelist] ValueListItem 1 | 1709,243 113x31 |
| 8 | RadioButton | Primary Vendor | [Editor] [valuelist] ValueListItem 2 | 1709,274 135x31 |
| 8 | RadioButton | 24 Month#Hits | [Editor] [valuelist] ValueListItem 3 | 1709,305 131x31 |
| 4 | Custom | Items | gridItems | 4,335 1676x624 |
| 5 | Custom |  | Data Area | 5,366 1674x592 |
| 6 | Tree |  | ColScrollRegion: 0, RowScrollRegion: 0 | 5,366 1674x592 |
| 7 | Header |  | Band Headers | 5,366 19x28 |
| 4 | Group | Input | gbInput | 4,160 776x165 |
| 5 | Pane |  | UltraExpandableGroupBoxPanel1 | 6,189 772x134 |
| 6 | Edit |  | srcShipToID | 132,277 380x32 |
| 7 | Edit |  | srcShipToID_EmbeddableTextBox | 137,282 353x23 |
| 7 | Edit |  | [Editor] Edit Area | 0,0 0x0 |
| 7 | Button |  | [Editor] [ButtonsRight] Key : Redo | 0,0 0x0 |
| 7 | Button |  | [Editor] [ButtonsRight] Key : SearchDDB | 0,0 0x0 |
| 7 | Button |  | [Editor] [ButtonsRight] Key : Search | 490,278 21x30 |
| 7 | Button |  | [Editor] [ButtonsRight] Key : Add | 0,0 0x0 |
| 6 | Edit |  | srcCustomerID | 132,235 380x32 |
| 7 | Edit |  | [Editor] Edit Area | 135,236 376x30 |
| 7 | Button |  | [Editor] [ButtonsRight] Key : Redo | 0,0 0x0 |
| 7 | Button |  | [Editor] [ButtonsRight] Key : SearchDDB | 0,0 0x0 |
| 7 | Button |  | [Editor] [ButtonsRight] Key : Search | 0,0 0x0 |
| 7 | Button |  | [Editor] [ButtonsRight] Key : Add | 0,0 0x0 |
| 6 | ComboBox |  | cboOrders | 132,194 603x32 |
| 7 | Edit |  | [Editor] Edit Area | 133,195 584x30 |
| 7 | Button |  | [Editor] dropdown button | 717,195 17x30 |
| 7 | ListItem | New | [Editor] [valuelist] ValueListItem 0 | 0,0 0x0 |
| 6 | Text | Order: | DdiLabel5 | 10,194 112x32 |
| 6 | Text | Ship To: | DdiLabel2 | 10,277 112x32 |
| 6 | Text | Customer: | DdiLabel1 | 10,235 112x32 |
