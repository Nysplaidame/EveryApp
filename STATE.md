# EveryApp — STATE (read this first, every session)

Only mandatory start-of-session read. Keep under ~200 lines. `/handoff` replaces the ▶ START HERE block; history goes in `HANDOFF.md`.

## ▶ START HERE (updated 2026-11-18, by Claude Opus 5.5 — task 14 accepted and merged)

- **Branch:** `feat/m1-foundation-ferry-engine` (draft PR #1). Task 14 merged in from `agent/002-task14-rescue` (merge not pushed yet). M1 tasks 1–14 complete.
- **Just finished:** task 14 (rescue mode + network retry). Full `./build.ps1` passed: 671 tests, 670 passed, 1 skipped (no ReFS drive). Code reviewed by Claude, no issues. Card 002 can be closed; card 003 (merge) is superseded by this merge.
- **Unmerged agent work to review next:** card 004 (`agent/004-…`, M2 Ctrl+V risk doc), 005 (prep for tasks 15–17), 006 (`agent/006-…`, `ferry` CLI, 5 commits), 007 (`agent/007-…`, task 16 benchmarks, build tweaks). None is based on the task 14 merge, so expect conflicts in `FileCopier.cs`/`STATE.md`. Review and run `./build.ps1` on each before merging.
- **M2 risk to carry:** Ferry's Ctrl+V hook will compete with copy tools like Ultracopier. Test with one running; decide detection/handling. Not yet in the spec.
- **Waiting on the user:** optional Explorer spike Part A `--swallow` (step 17+) and Part C; left-drag decision before M2; run `scripts/New-SigningCert.ps1` before the first release.
- **Next step:** review and merge cards 006 then 007 (tasks 15–16), then task 17 (soak + milestone security review against the threat model).
- **Touches (this update):** `STATE.md`, `HANDOFF.md`.

## Current state

| Area | State |
|---|---|
| M1 tasks 1–12 | Complete, each review-clean after ≤1 fix round |
| M1 task 13 (tree copy) | Complete — task review + 3 security rounds clean |
| M1 task 14 (rescue + retry) | Complete — full build.ps1 green, Claude review clean, merged 2026-11-18 |
| M1 tasks 15–17 | 15 and 16 done by agents on unmerged branches (cards 006, 007), not reviewed; 17 not started |
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
