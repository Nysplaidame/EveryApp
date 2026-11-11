# EveryApp — STATE (read this first, every session)

Only mandatory start-of-session read. Keep under ~200 lines. `/handoff` replaces the ▶ START HERE block; history goes in `HANDOFF.md`.

## ▶ START HERE (updated 2026-11-09, by Claude Opus 5.5 — paused by user mid Task 14)

- **Branch:** `feat/m1-foundation-ferry-engine` (pushed). Last commit `b0e4c09` fix(core): close ACL generic-rights bypass and resume link redirects. Draft PR #1 open.
- **Milestone M1:** tasks 1–13 complete and review-clean (task 13 incl. 3 security rounds).
- **PAUSED in Task 14 (rescue mode + network retry):** the implementer was stopped mid-task. **Uncommitted partial work is in the working tree** (RescueReader.cs, RetryPolicy.cs, their tests, TreeCopierRescueTests.cs, IFileSystem/fake/WriteHandle edits). RED was captured (42/54 failing); GREEN not reached. Don't commit it as-is.
- **On resume:** check `git status`; dispatch a **fresh** implementer with `task-14-brief.md` plus the Task 14 decisions in the progress log (rescue only on BadBlock + RescueMode, lost-sector cap, rescued file never a clean success and never deletes a move source, plain lost report, RetryPolicy with TimeProvider, handle reopen + `OpenWriteExisting`). Tell it to build on or discard the partial work.
- **Waiting on the user:** nothing blocking. Optional: Explorer spike Part A `--swallow` (step 17+) and Part C; left-drag decision before M2; run `scripts/New-SigningCert.ps1` before the first release.
- **Next step (tier):** Task 14 (Executor, strong) → review → Tasks 15–17 (`ferry` CLI, benchmarks/tuning incl. WriteThrough/in-flight levers R27, soak + milestone security review).
- **Touches (this update):** `STATE.md` only.

## Current state

| Area | State |
|---|---|
| M1 tasks 1–12 | Complete, each review-clean after ≤1 fix round |
| M1 task 13 (tree copy) | Complete — task review + 3 security rounds clean |
| M1 task 14 (rescue + retry) | Paused mid-implementation; partial uncommitted work in tree |
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
