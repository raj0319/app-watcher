# Anonymised copies of the v0.9 measured session (do not "fix" the names back)

These files are copies of what App Watcher 0.9.0 recorded during the third measured run against the same real ERP
(Infragistics on WinForms, MDI workspace, UniVerse backend) on 2026-11-19, 15:55 to 16:24 (the driven workload and a
rerun of four flows). Every real name and identifier was replaced by the same fixed stand-ins as in `../inform-v0.8`
(Sample Co, Writer One, 0000001 "Test Customer", 00001 "ACME", 100EX "SAMPLE PRODUCT", 192.0.2.10, Sample City, ZIP 00000,
phones "555-555-0100"); the structure, the counts, the depths, the automation ids, the positions, the order of events, the
timestamps and the `how` / `by` fields are unchanged.

`clicks.json` holds every click of the driven workload that the watcher had to resolve without the toolkit's help
(20 Exit, 8 prompt buttons, 2 Add Line Item, 1 Run, 57 sub-tab clicks), each with the screen point at the centre of the
button's or tab item's rectangle in the anonymised record of the document that was in front. The driver logs carry no
pointer positions, so the points come from the records themselves; a tab item that was off screen when its document was
recorded has no point (`x: null`) and must be read live under the pointer.

No picture and no OCR text was copied. `InformV09Tests.Both_fixture_folders_hold_none_of_the_real_strings` covers the
v0.7 and v0.8 folders; `scripts\check-fixtures.ps1 -Forbidden <file>` checks all of `tests\` with the plain list kept
outside the repository.
