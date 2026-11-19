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

A small window opens and asks three questions, one per step. Everything else is already decided.

1. **Which program should I watch?** Pick the app from the list of programs open right now, or press
   **Find it on this PC...** and choose its .exe. Read the green box: it says in plain words what I
   recognised and what I will learn. If it says the data lives on a server I cannot read, that is normal
   for the demo. **Details** shows the technical facts if you are curious. Press **Next**.
2. **Anything to keep private?** Leave the three ticks on (hide names and numbers in window titles, keep
   pictures of the program's screens, read the text on them). Type any window that must never be recorded,
   for example Payroll, or leave it empty. Press **Next**.
3. **Choose a password**, type it twice, and press **Start watching**. You need it to open Settings or quit.

A notice near the clock says "I am watching <app>. Just use it as usual." Close nothing else.

You do not pick data sources: every data file found on this PC is ticked by itself. For two minutes after
the start I also note which files the app really reads and writes. If Windows asks for permission for that,
say Yes for the fullest result, or No to skip it; I keep watching either way.

## 3. Use the app normally (20 minutes)

Do the things a user does every day. Good demo coverage:

- Open 5 to 10 different screens or pages, including menus and dialogs.
- Use the tabs of a tabbed window; each tab is recorded as its own screen.
- Search or filter on at least two screens with a few different values, and let any suggestion list
  (autocomplete) appear while typing. The watcher records the suggestions and the grid the search returns.
- Open some existing records and look at them.
- If it is safe, create one test record (name it starting with `ZZTEST`), save it, then edit it
  and save again. Saves are where the watcher learns which table and columns a screen writes.
- Switch to another program for a minute and come back. The watcher pauses and resumes by itself.

The watcher never presses anything in the app and never opens a window of its own. If you see a prompt, it is from the app.

## 4. Learn and make the report (1 minute)

After one hour a notice says what happened, for example "First hour done: 12 screens and 140 actions
recorded." For a shorter demo, do not wait. Right-click the eye icon:

1. **Update what I learned**. Wait for the short notice; it says how many screens, rules and flows I know.
2. **Make a report folder**. The folder opens by itself with the zip selected.
3. **Open the folder with everything collected** opens `C:\ProgramData\AppWatcher\data\<app-name>`.

**Settings...** (asks the password) shows the same plain summary under **What has been collected so far**.

## 5. What to send for analysis

Zip the whole folder `C:\ProgramData\AppWatcher\data\<app-name>\`. If it is very large, leave out
`snapshots\` (it is only the latest copy of the data) and keep:

- `events\` the actions, one file per day
- `screens\` every screen's fields and buttons, and `screens\shots\<screen>\` one picture per state (with an OCR text layer when on)
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

- No eye icon: run `AppWatcher.exe` by hand once; the three-step window opens if nothing is set up yet.
- `events\` stays empty: the app was not the window in front, or it was picked by the wrong process.
  Open Settings, check the program (press **Change...** if it is the wrong one), and click inside the app's window.
- The first-hour notice names problems: open `memory\health.md`, section "What the collector could not
  do". "No window's control tree could be read" means the app does not expose its screens to Windows UI
  Automation; the watcher still records windows, timings, dialog text and data changes, but not fields.
  "No button or menu press was seen" means the app's controls stay silent; the fallbacks keep trying.
- `effects\` stays empty: no data source was found or ticked. Open Settings, **Advanced**, page 2 and press **Find what it uses (admin, 2 min)**,
  or ask the app's vendor or DBA where the data lives. Without it, the watcher learns screens and
  flows but not tables.
