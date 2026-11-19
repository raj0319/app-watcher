# Anonymised copies of the v0.8 measured session (do not "fix" the names back)

These files are copies of what App Watcher 0.8.0 recorded during the second measured run against the same real ERP
(Infragistics on WinForms, MDI workspace, UniVerse backend) on 2026-11-19, 13:07 to 13:26 (docs/test-v0.8-inform.md is the
scorecard). Every real name and identifier was replaced by a fixed stand-in; the structure, the counts, the depths, the
automation ids, the order of events, the timestamps and the `how` / `by` fields are unchanged, so the tests see the same
shapes the collector saw:

| Real thing | Stand-in |
|---|---|
| the company | Sample Co |
| the user (the order writer) | Writer One |
| a customer number and name | 0000001, Test Customer |
| a vendor | 00001 "ACME" |
| a product id and description | 1000000001, 100EX "SAMPLE PRODUCT" |
| the branch, warehouse and certificate codes | 02 - Sample Co, MAIN, Cert1,Cert2 |
| a city, a town, three street lines | Sample City, Sample Town, 1 Sample Street, 2 Sample Avenue, 3 Sample Road |
| zip codes, phone numbers, e-mail addresses | 00000, (masked), *** |
| the server | 192.0.2.10 |
| typed codes other than quantities and the product stand-in | `***` |
| user and machine hashes in the start event | 00000001 / 00000002 |

Values the program filled in (`by: app`) stay when they are numbers, dates, On/Off states or plain codes (SAME, PICK UP,
House, EA); personal text became `***`. No picture and no OCR text was copied: the pictures show real screen content. Tests
that need picture text build a short synthetic text layer.

`InformV09Tests.Both_fixture_folders_hold_none_of_the_real_strings` checks these files (and the v0.7 folder) against the
hashes of the real strings, so a copy of the originals can never land here unnoticed. `scripts\check-fixtures.ps1 -Forbidden
<file>` does the same with the plain list kept outside the repository.
