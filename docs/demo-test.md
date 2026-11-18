# Demo test in 30 minutes

How to try App Watcher on one PC with one app, collect the result, and hand it to an AI.

## Before you start

- A Windows 10 or 11 PC where the app is installed, and you are logged in on a real desktop
  (sitting at it, or Remote Desktop). Plain SSH does not work: the watcher needs to see windows.
- The app you want to watch is open and you are logged in to it.
- You can run PowerShell as the same Windows user who uses the app.

## 1. Get the program (2 minutes)

Download two things from the GitHub releases page of this repo:

- `AppWatcher.exe`
- `install.ps1` (also in `scripts\` if you cloned the repo)

Put both in one folder, for example `C:\AppWatcherDemo\`.

## 2. Install and start (2 minutes)

Open PowerShell in that folder and run:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -ExePath .\AppWatcher.exe
```

The setup window opens.

1. **App to watch**: pick the app from the list of running programs, or press **Browse...** and
   choose its .exe from any drive. Read the box at the bottom: it says which toolkit the program uses,
   which profile was picked, where its data seems to live, and what App Watcher will and will not be
   able to learn. If it says the data is on a server this PC cannot read, that is normal for the demo.
2. **Where its data lives**: tick the files or folders it found. If the list is empty or you are
   not sure, press **Detect by watching file access**, allow the administrator prompt once, then use
   the app normally for two minutes. It proposes the files the app really reads and writes.
   If the app uses SQL Server or has an ODBC data source on this PC, you can add a read-only connection
   here, or point **Add export drop folder** at a folder with CSV exports, or skip it for the demo.
3. **Privacy** (tab 4): decide whether to keep one picture per screen (on by default; the app's window
   only) and list any window that must never be recorded.
4. **Settings password** (tab 6): choose one. You need it to open settings or quit.

Press **Start watching**. An eye icon appears in the tray. Close nothing else.

## 3. Use the app normally (20 minutes)

Do the things a user does every day. Good demo coverage:

- Open 5 to 10 different screens or pages, including menus and dialogs.
- Search or filter on at least two screens with a few different values.
- Open some existing records and look at them.
- If it is safe, create one test record (name it starting with `ZZTEST`), save it, then edit it
  and save again. Saves are where the watcher learns which table and columns a screen writes.
- Switch to another program for a minute and come back. The watcher pauses and resumes by itself.

The watcher never presses anything and never shows a pop-up. If you see a prompt, it is from the app.

## 4. Learn and export (1 minute)

Right-click the eye icon:

1. **Learn now**. Wait for the short tray notice.
2. **Export**. The notice shows the folder it wrote.
3. **Copy data folder path for AI**. Paste it somewhere; it is
   `C:\ProgramData\AppWatcher\data\<app-name>`.

## 5. What to send for analysis

Zip the whole folder `C:\ProgramData\AppWatcher\data\<app-name>\`. If it is very large, leave out
`snapshots\` (it is only the latest copy of the data) and keep:

- `events\` the actions, one file per day
- `screens\` every screen's fields and buttons, and `screens\shots\` one picture per screen
- `effects\` what changed in the data after each save
- `collector-status.json` and `expectations.md` what the watcher could read and what it expected
- `memory\` the learned data model, screens, rules, flows, architecture, health
- `export\<date>\` the bundle in App Twin Kit format

Send the zip. An AI reading it can tell you: which screens exist and what fields they have, which
flows users take and how long they take, which tables and columns each screen writes (when a data
source was found), the rules and calculations it could prove, and what it could not learn yet and why.
`memory\health.md` already says the last part in one line.

## 6. Uninstall (1 minute)

Same folder, PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

This stops the watcher, removes the start-at-login entry and the program. The learned data stays in
`C:\ProgramData\AppWatcher` so you can still send it. To delete the data too:

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -RemoveData
```

`uninstall.ps1` is in `scripts\` of this repo and on the releases page.

## If something looks wrong

- No eye icon: run `AppWatcher.exe` by hand once and read the setup window.
- `events\` stays empty: the app was not the window in front, or it was picked by the wrong process.
  Open Settings, check the app, and make sure you click inside the app's window.
- The first-hour notice names problems: open `memory\health.md`, section "What the collector could not
  do". "No window's control tree could be read" means the app does not expose its screens to Windows UI
  Automation; the watcher still records windows, timings, dialog text and data changes, but not fields.
  "No button or menu press was seen" means the app's controls stay silent; the fallbacks keep trying.
- `effects\` stays empty: no data source was found or ticked. Use **Detect by watching file access**,
  or ask the app's vendor or DBA where the data lives. Without it, the watcher learns screens and
  flows but not tables.
