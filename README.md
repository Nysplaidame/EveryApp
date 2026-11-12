# EveryApp

A suite of small, stable, lightweight, secure Windows 11 utilities, built with C# on .NET 10.

- Ferry: copy and sync (first app, in progress)
- More apps follow; see `docs/superpowers/specs/` for the design.

## Build

Requires the .NET SDK pinned in `global.json` and PowerShell.

```powershell
./build.ps1                 # restore (locked), format check, build, test
./build.ps1 -Configuration Debug
./build.ps1 -SkipTests
```

CI runs exactly this script.
