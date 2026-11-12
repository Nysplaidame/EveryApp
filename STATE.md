# EveryApp — STATE (read this first, every session)

Only mandatory start-of-session read. Keep under ~200 lines. `/handoff` replaces the ▶ START HERE block; history goes in `HANDOFF.md`.

## ▶ START HERE (updated 2026-11-12, by Codex - card 006 line endings fixed)

- **Branch:** `agent/006-implement-the-ferry-cli-for-task-15`, based on `7f1bb7f`. Copy, resume, verify and jobs are implemented with System.CommandLine, JSON lines, previews/confirmation, Ctrl+C and process tests.
- **Scope gap:** Task 14 is untouched. `--rescue` and `--if-unreadable ask` return a clear exit 4 until their engine support exists. `--yes` authorizes previews/resume; `jobs --json` is also supported.
- **Formatting fix:** Normalized CLI source/tests and the console-signal helper to CRLF. The CLI whitespace check passes. Git already stores normalized LF with `eol=crlf`, so this repair changes working-tree bytes only; fresh checkouts get CRLF automatically.
- **Verification:** Single-worker locked restore succeeds with normal package references. Release solution build passes with zero warnings/errors using `-m:1 -p:UseSharedCompilation=false`. Standalone CLI tests: 33 passed, zero failures (including Ctrl+C and kill/resume).
- **Acceptance blocker:** Exact `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1` still exits 1 at locked restore without a diagnostic. Full solution format check separately throws `UnauthorizedAccessException` connecting to its build-host named pipe. Acceptance is not passed.
- **Next step:** Run the exact acceptance command in an environment allowing build/test IPC and filesystem permission tests; review CLI changes, then connect the two unavailable options after Task 14. Benchmark tuning and milestone soak remain untouched.
- **Touches:** Working-tree CLI/test line endings, `STATE.md`, `HANDOFF.md`.

## Current state

| Area | State |
|---|---|
| M1 tasks 1–12 | Complete, each review-clean after ≤1 fix round |
| M1 task 13 (tree copy) | Complete — task review + 3 security rounds clean |
| M1 task 14 (rescue + retry) | Card 002 (proposed); partial work on `wip/task14-partial` |
| M1 task 15 | CLI implemented on card 006; acceptance and Task 14-dependent options pending |
| M1 tasks 16–17 | Not started |
| Deferred minors | Listed per task in the progress log (`- Task N: minor (deferred)`) — sweep before M1 release |
| Notes for M2 | In progress log (`NOTE for M2`): scheduler can block ~3 s on WMI — never on UI thread; same-folder copy should name "- Copy"/"(2)" |

## Key locations

| What | Where |
|---|---|
| Spec (scope, architecture, §14 security) | `docs/superpowers/specs/2026-11-06-ferry-suite-v1-design.md` |
| M1 plan | `docs/superpowers/plans/2026-11-06-everyapp-m1-foundation-and-ferry-engine.md` |
| Plan progress log + rulings (local, git-ignored) | `.superpowers/sdd/2026-11-06-everyapp-m1-foundation-and-ferry-engine/progress.md` |
| Threat model | `docs/security/threat-model.md` |
| ADRs | `docs/adr/` |
| UI designs | `docs/design/` |

## Commands

```powershell
./build.ps1                        # restore (locked), format check, build, test — exactly what CI runs
./build.ps1 -Configuration Debug
./build.ps1 -SkipTests
```
