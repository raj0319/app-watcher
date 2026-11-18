# App Watcher

A small Windows program that sits in the tray, watches one chosen desktop app while real people use
it, and slowly writes down how that app works: its screens, fields, tables, rules and flows. Think of it
as a quiet architect taking notes behind the user.

It does not take screenshots, it does not watch other programs, and it never changes anything in the
app it watches.

## What you get

After a few days of normal work, the folder `C:\ProgramData\AppWatcher\data\<app-name>\memory\` holds
five living documents, rewritten every night:

| File | What it says |
|---|---|
| `data-model.md` | the tables and columns the app saves to, the type of each column, which screen field fills it, and how tables link to each other |
| `screens.md` | every window seen, its fields and buttons, and which tables that window writes |
| `rules.md` | validation messages (which field, which kind of value, which message), calculated columns with the formula that fits, numbering sequences |
| `flows.md` | status flows (for example open → paid → shipped) with the transitions seen and the ones never seen, and how users move between windows |
| `architecture.md` | modules (screens grouped by the tables they share) and an honest "seen vs inferred" count |

Every line carries a confidence: `seen 12×` means it was observed twelve times; `inferred` means the
structure suggests it but nobody proved it. Every line also names the evidence file to open.
`changelog.md` lists what changed since the last learn.

The memory files never contain values that users typed. They contain names, shapes and counts.

## Install (five minutes)

You need a Windows 10 or 11 PC where the app to watch is used.

1. Get `AppWatcher.exe`. Either download it from the releases of this repository, or build it yourself
   (install the .NET 8 SDK, then run `scripts\publish.ps1`; the exe appears in `publish\`).
2. Open PowerShell in this folder and run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

   This copies the program to your user folder, makes it start at login, and starts it now.
   Add `-ScheduledTask` if your IT prefers a scheduled task over the Run key.
3. The setup window opens:
   - **1. App to watch**: pick the program from the list of running programs, or browse to its .exe.
   - **2. Where its data lives**: App Watcher scans the usual places (install folder, ProgramData,
     AppData, Documents) for database and data files. Tick the ones that belong to the app, or add a file,
     a folder, or a read-only SQL Server connection. Nothing is written there, ever.
   - **5. Settings password**: choose a password. It is needed to open settings or quit.
   Press **Start watching**. An eye icon appears in the tray. That is all.

Install is per Windows user: run it under the account that uses the app.

## What is recorded

Only inside the chosen app, and only while it is the window in front:

- which window is open (its title) and the control tree of each window, once
- which buttons and menu items were pressed, which fields got focus, what was selected
- the values typed into fields of the app (so a typed value can be matched to the column it lands in)
- a read-only before/after copy of the app's data around each save-type action, and the difference

Password fields, and any field whose name contains "password", "pin" or "secret", are recorded as `***`.

## What is never recorded

- other programs, the desktop, the browser, e-mail, chat: nothing outside the chosen app
- anything while another program is in front, even if the app is still open behind it
- the clipboard
- screenshots or pictures of any kind
- keystrokes outside the app (there is no keyboard hook at all)
- anything over the network: the watcher makes no network calls. The one optional exception is the
  AI analysis described below, which is off by default and sends only the memory files.

Everything stays in `C:\ProgramData\AppWatcher\`. You can open that folder any time from the tray menu.

## How it learns

Once a night (default 02:00, changeable in Settings), and whenever you choose **Learn now** from the tray,
the learner reads the evidence and rewrites the memory files. No AI, no network, just rules:

- **Flows**: user actions are grouped into sessions and into flows by the sequence of windows. For each
  flow: how often, median duration, the usual path, and the step people most often go back from.
- **Screen to table**: a value typed on a screen that shows up in a changed column becomes a mapping
  "field X fills column Y", with a count. A screen that always changes the same tables is noted.
- **Relations**: a value written to one table that exists as a key in another is a link (foreign key candidate).
- **Calculated fields**: columns that change without being typed are tested against simple formulas
  (sum of child rows, a × b, a + b, a percentage with common rates, rounding to 2 places).
  The best fit and how many rows it explains are recorded.
- **Validation rules**: a message window right after a typed value becomes a rule (field, kind of value, message).
- **Status flows**: columns with a small set of values are ordered into a state diagram from the
  transitions seen over time, with the transitions never seen.
- **Numbering**: columns that go up by one per new row are sequences.

Resource use: the watcher is event driven. When the app is not in front it does nothing. Idle memory
is small (target under 60 MB). Data snapshots are capped: databases over the size cap (default 256 MB)
only get their recently changed tables read, and tables over the row cap are skipped.

In the first hour of watching, if the app shows fewer than ten controls or no data source was found,
one tray message tells you what App Watcher will not be able to learn from this app.

## The tray menu

Right-click the eye icon:

| Item | What it does |
|---|---|
| Pause / Resume | stop or continue recording (the icon turns grey while paused) |
| Learn now | rewrite the memory files from today's evidence |
| Export... | write a bundle for the App Twin Kit (see below) and open the folder |
| Settings... | change the app, data sources, learn time, AI analysis, password (asks the password) |
| Open data folder | opens `C:\ProgramData\AppWatcher\data\<app-name>` |
| Quit | stops App Watcher until the next login (asks the password) |

Running until Quit is the normal state. It starts again at every login.

## Changing the watched app

Open Settings, pick another program on the first tab, save. App Watcher starts a new folder
`data\<new-app-name>\` and leaves the old folder exactly as it is.

## Stopping and removing

- Pause from the tray to stop recording for a while.
- Quit from the tray to stop until the next login.
- To remove it completely:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1
```

  The learned data is kept; add `-RemoveData` to delete it too.

## Handing the result to the App Twin Kit, or to any AI

Choose **Export...** from the tray (or run `AppWatcher.exe --export`). It writes
`data\<app-name>\export\<date>\docs\raw\` in the format the [App Twin Kit](https://github.com/raj0319/app-twin-kit)
expects, and a zip next to it:

- `screens\*.md`: one per window, with the control tree
- `tasks\*.md`: one per learned flow, with count and median duration
- `probes\*.md`: per screen, the fields, the distinct values seen (personal-looking values masked) and the rules learned
- `data-model.md`: tables, columns, relations, snapshot paths
- `memory\*.md`: the five living documents and the changelog

Then: copy `docs\raw` into your `app-twin-kit` folder, open it in Claude Code and type `/mode spec`.
The kit continues from the SPEC step with everything the watcher learned.

Any other AI can read `memory\*.md` directly; they are plain text.

## Deeper reasoning with Claude

The watcher only applies rules. For real architecture work, let Claude read the memory and the evidence:

- **On this PC, no network from the watcher**: open this repository (or the data folder) in Claude Code
  and type `/analyze`. Claude reads `memory\`, opens the evidence behind weak findings, challenges them,
  and writes `memory\architecture-reviewed.md`: modules, a schema proposal, the rule list, open questions.
  It never writes to the app's data.
- **Nightly through the Claude API (optional, off by default)**: in Settings, tab 4, turn it on and paste
  the client's own API key. After each nightly learn, only the `memory\*.md` files are sent, with the
  same analyst prompt, and the answer is saved as `memory\architecture-reviewed.md`. The button
  **Show exactly what is sent** displays the full message before you decide. Raw events and typed values
  are never sent.

## Tell your users

Something like this can go on the notice board or in an e-mail:

> We have installed a small program called App Watcher on this PC. It watches only **<app name>** while
> you work in it, and writes down which screens, buttons and fields are used and what the program saves,
> so that we can document and later rebuild it. It does not record other programs, e-mails, the web,
> the clipboard or screenshots, and it does not send anything over the internet. Passwords are never
> recorded. You will see a small eye icon near the clock; it does not need anything from you. Questions:
> ask <contact>.

## Honest limits

- Apps that draw their own screens (some old Delphi, Java or custom programs) show few or no controls.
  The watcher will say so in the first hour; it can still learn from the data side.
- Encrypted or server-only data cannot be read unless you give a read-only connection.
- The learner finds simple formulas and plain rules. Anything cleverer is for the /analyze step and for people.
- A rule is only as sure as its count. One observation is a hint, not a fact.

## For developers

C# on .NET 8, WinForms for the tray and settings, FlaUI (UI Automation) for the collector,
Microsoft.Data.Sqlite / OleDb / SqlClient as read-only readers. `dotnet build`, `dotnet test`,
`scripts\publish.ps1`. See `CLAUDE.md` for the folder layout and the rules the code follows.

## License

MIT.
