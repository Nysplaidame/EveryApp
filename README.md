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

## Ferry CLI

`ferry.exe` is built to `artifacts/bin/Ferry.Cli/release_win-x64/` (`./build.ps1` or `dotnet build src/Ferry.Cli -c Release`). Run `ferry --help` for the short list.

```powershell
ferry copy <source>... <destination>    # copy files and folders; checks every file after copying
ferry copy <source>... <destination> --move   # copy, then remove the source once each file is checked
ferry resume <job-id>                   # finish a job that was stopped or interrupted
ferry verify <folderA> <folderB>        # compare two folders by content
ferry jobs                              # list saved jobs and whether they finished
```

Options for `copy`:

- `--if-exists ask|overwrite|newer|skip|rename|keep-both`: what to do when a file is already there (default `ask`).
- `--if-unreadable retry-skip|ask|stop`: what to do with a file that cannot be read. `--rescue` saves what it can from a failing drive and lists what it could not read.
- `--limit 50MB/s`: cap the speed. `--exclude <glob>` skips matching names (repeat it for more).
- `--no-verify`: skip the check after copying (faster, less safe).
- `--dry-run`: show what would happen and change nothing.
- `--yes`: confirm the preview that moves and automatic replacements show.
- `--json`: print one JSON event per line, for scripts. It needs an `--if-exists` rule other than `ask`.

Exit codes: 0 success, 1 problems, 2 blocked, 3 cancelled, 4 invalid arguments, 10 internal error.

`bench.ps1` compares Ferry's speed with FastCopy on your drives (copy `bench.example.json` to `bench.local.json` first). `soak.ps1` runs copy, verify and move in a loop for hours, with random stalls and crashes, and reports whether anything leaked or failed. Both need real drives and are not part of `build.ps1`.
