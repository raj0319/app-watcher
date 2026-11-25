# Contributing

## Report how it works on your program

The most useful help right now is a report. Fill in the **Did it work on your app?** form:
https://github.com/raj0319/app-watcher/issues/new/choose

Read what you paste first (`memory\health.md`, the `--detect` output) and remove anything private, such as
company names in window titles or server names.

## Build and test

You need the .NET 8 SDK on Windows.

```powershell
dotnet build
dotnet test
```

## Never commit customer evidence

Evidence copied from a real customer's PC (data folders, `app-watcher-v*-evidence-*/`, `evidence/`) must never
be committed. Anything that goes into `tests/` is anonymised first. See the rules in `CLAUDE.md`.
