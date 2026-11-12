# EveryApp — STATE (read this first, every session)

Only mandatory start-of-session read. Keep under ~200 lines. `/handoff` replaces the ▶ START HERE block; history goes in `HANDOFF.md`.

## ▶ START HERE (updated 2026-11-12, by Codex - card 006 CLI implemented, acceptance blocked)

- **Branch:** `agent/006-implement-the-ferry-cli-for-task-15`, based on `7f1bb7f`. Copy, resume, verify and jobs are implemented with System.CommandLine, JSON lines, previews/confirmation, Ctrl+C and process tests.
- **Scope gap:** Task 14 is untouched. `--rescue` and `--if-unreadable ask` return a clear exit 4 until their engine support exists. `--yes` authorizes previews/resume; `jobs --json` is also supported.
- **Verification:** Exact `build.ps1` acceptance still fails at locked restore (exit 1). With a temporary reference to the SDK's System.CommandLine 2.0.2 DLL: solution build passed without warnings; CLI 33/33 passed, including real Ctrl+C and 2,000-file kill/resume. Standalone regressions: Core 410 passed / 12 permission failures; Engine 190 passed / 2 ACL failures / 1 ReFS skip. The temporary reference was removed.
- **Environment:** NuGet is unreachable and System.CommandLine is not cached; serial locked restore reports NU1301 and NU1900. Full format and `dotnet test` also hit denied named-pipe access. Whitespace formatting and `git diff --check` passed. Lock-file checksum comes from the installed SDK's official package manifest; normal locked restore is still required.
- **Next step:** restore System.CommandLine 2.0.2 and run `build.ps1` in an environment permitting its build/test IPC and filesystem permission tests; review the CLI changes, then connect the two unavailable options after Task 14. Do not mark Task 15 accepted yet.
- **Touches:** CLI and CLI tests, read-only job-history/verification APIs, dependency pins/locks, threat model, `STATE.md`, `HANDOFF.md`.

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
