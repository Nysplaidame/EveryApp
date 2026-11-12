# EveryApp — STATE (read this first, every session)

Only mandatory start-of-session read. Keep under ~200 lines. `/handoff` replaces the ▶ START HERE block; history goes in `HANDOFF.md`.

## ▶ START HERE (updated 2026-11-11, by Claude Opus 5.5 — task 14 moved to agent-ops card 002)

- **Branch:** `feat/m1-foundation-ferry-engine` (pushed; draft PR #1). Clean working tree. M1 tasks 1–13 complete and review-clean.
- **Task 14 (rescue mode + network retry) is owned by agent-ops card 002** — `.agents/tasks/002-task14-rescue-retry.md`. Don't resume it through the superpowers plan run; run, stop or close the card instead (Agent Ops page or `dispatch`).
- **Partial work** from the paused 2026-11-09 run is saved on `wip/task14-partial` (`593e293`, tests written, RED). The card's branch `agent/002-task14-rescue` starts from it.
- **Card 001** (task 13 security round 3) is closed: done in the plan run, `b0e4c09`.
- **Waiting on the user:** approve and run card 002. Optional: Explorer spike Part A `--swallow` (step 17+) and Part C; left-drag decision before M2; run `scripts/New-SigningCert.ps1` before the first release.
- **Next step (tier):** card 002 (Executor, Codex at high effort; Claude reviews) → merge its branch into the feature branch → Tasks 15–17 (`ferry` CLI, benchmarks/tuning incl. R27 levers, soak + milestone security review).
- **Touches (this update):** `STATE.md`, `.agents/tasks/002-*`; branch `wip/task14-partial`.

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
