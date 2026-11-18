# EveryApp — STATE (read this first, every session)

Only mandatory start-of-session read. Keep under ~200 lines. `/handoff` replaces the ▶ START HERE block; history goes in `HANDOFF.md`.

## ▶ START HERE (updated 2026-11-18, by Claude Opus 5.5 — tasks 14 and 15 merged; task 16 not done)

- **Branch:** `feat/m1-foundation-ferry-engine` (draft PR #1). M1 tasks 1–15 complete and merged.
- **Just finished:** task 15 (`ferry` CLI, card 006) merged and reviewed. `--rescue` turned on (card 006 predates task 14); `ferry verify` checks sizes before hashing. Card 004 (M2 Ctrl+V risk note) merged. Full `./build.ps1`: 703 tests, 702 passed, 1 skipped (no ReFS drive).
- **Task 16 is NOT done.** Card 007 (`agent/007-…`) has no harness and no measurements: only "blocked" notes written before tasks 14–15 existed. Deliberately not merged. Its `docs/perf/results.md` lists what must be measured (keep as a checklist). Needs FastCopy installed on this PC and the bench harness (`bench/Ferry.Bench`, `bench.ps1`) built.
- **Cards to close:** 002 and 003 (task 14 merged), 004, 006. Card 005 was prep only. Card 007 should be redone.
- **M2 risk to carry:** Ferry's Ctrl+V hook will compete with copy tools like Ultracopier. Test with one running; decide detection/handling. Not yet in the spec.
- **Waiting on the user:** install FastCopy for the task 16 comparison; optional Explorer spike Part A `--swallow` (step 17+) and Part C; left-drag decision before M2; run `scripts/New-SigningCert.ps1` before the first release.
- **Next step:** task 16 (benchmark harness, FastCopy comparison, R27 tuning), then task 17 (soak + milestone security review against the threat model).
- **Touches (this update):** `STATE.md`, `HANDOFF.md`, `docs/security/threat-model.md`, `src/Ferry.Cli/`, `tests/Ferry.Cli.Tests/CliTests.cs`.

## Current state

| Area | State |
|---|---|
| M1 tasks 1–12 | Complete, each review-clean after ≤1 fix round |
| M1 task 13 (tree copy) | Complete — task review + 3 security rounds clean |
| M1 task 14 (rescue + retry) | Complete — full build.ps1 green, Claude review clean, merged 2026-11-18 |
| M1 task 15 (`ferry` CLI) | Complete — card 006 merged, Claude review, `--rescue` enabled, 703 tests green (2026-11-18) |
| M1 tasks 16–17 | Not done: card 007 produced no measurements (not merged); 17 not started |
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
