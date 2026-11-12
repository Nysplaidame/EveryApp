# EveryApp — STATE (read this first, every session)

Only mandatory start-of-session read. Keep under ~200 lines. `/handoff` replaces the ▶ START HERE block; history goes in `HANDOFF.md`.

## ▶ START HERE (updated 2026-11-12, by Codex - card 006 blocked)

- **Branch:** `agent/006-implement-the-ferry-cli-for-task-15`, based on `7f1bb7f`. Task 15 is not implemented.
- **Blockers:** Task 14 remains incomplete: rescue mode has no engine implementation and unreadable-file Ask falls back to skip. The required System.CommandLine package is not cached and NuGet was unreachable in this session.
- **Verification:** The acceptance command again failed at locked solution restore without a diagnostic; `dotnet restore EveryApp.slnx --locked-mode -m:1` succeeded. The reported later build failure could not be reproduced in this checkout.
- **Next step:** complete Task 14, provide the System.CommandLine dependency, then implement and test card 006.
- **Touches:** `STATE.md`, `HANDOFF.md`.

## Current state

| Area | State |
|---|---|
| M1 tasks 1–12 | Complete, each review-clean after ≤1 fix round |
| M1 task 13 (tree copy) | Complete — task review + 3 security rounds clean |
| M1 task 14 (rescue + retry) | Card 002 (proposed); partial work on `wip/task14-partial` |
| M1 tasks 15–17 | Not started |
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
