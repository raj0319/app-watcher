# Anonymised copies of a real session (do not "fix" the names back)

These files are copies of what App Watcher 0.7 recorded during a measured 23-minute test against a real ERP
(Infragistics on WinForms, tabbed MDI workspace) on 2026-11-19. Every real name and identifier was replaced by
a fixed stand-in; the structure, the counts, the depths, the automation ids, the order of events, the timestamps
and the `how` / `by` fields are unchanged, so the tests see the same shapes the collector saw:

| Real thing | Stand-in |
|---|---|
| the company | Sample Co |
| the user | Writer One |
| a customer number | 0000001 |
| a vendor | 00001 "ACME" |
| a product id | 1000000001 (product code 100EX kept: it is a stand-in itself) |
| station, register, program version | S00001, 01, 1.0.0 |
| the server | 192.0.2.10 |
| typed searches and names | `***` or a same-shape stand-in |
| user and machine hashes in the start event | 00000001 / 00000002 |

No picture and no OCR text was copied: the pictures show real screen content. Tests that need picture text build
a short synthetic text layer with the same label → number pairs.

`InformFixturesTests.Fixtures_hold_none_of_the_real_strings` checks these files against the hashes of the real
strings, so a copy of the originals can never land here unnoticed. `scripts\check-fixtures.ps1 -Forbidden <file>`
does the same with the plain list kept outside the repository.
