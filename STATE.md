# EveryApp — STATE (read this first, every session)

Only mandatory start-of-session read. Keep under ~200 lines. `/handoff` replaces the ▶ START HERE block; history goes in `HANDOFF.md`.

## ▶ START HERE (updated 2026-11-08, by Claude Opus 5.5 — agent setup, no code changes)

- **Branch:** `feat/m1-foundation-ferry-engine` (pushed to origin). Last commit `db369af` docs(security): threat model for the tree copier security fixes.
- **Milestone M1** (foundation + Ferry engine), plan `docs/superpowers/plans/2026-11-06-everyapp-m1-foundation-and-ferry-engine.md`: tasks 1–12 complete; **task 13 (tree copy) in its security fix rounds.**
- **In progress:** Task 13 fix round 3/5 — narrow: findings N1–N4 (N1 Medium: GENERIC_ALL/GENERIC_WRITE ACEs bypass the broad-write filter; N2 resume part delete/target metadata by path through a junction; N3 elevation seam on public ctor; N4 no access recheck after revert, BroadGroups missing INTERACTIVE/Guests/Domain Users) + 2 threat-model corrections. Ruling R33: dispatch a **fresh** Executor-tier implementer with the brief and findings — don't resume the old one (~680k tokens).
- **Waiting on the user:** nothing.
- **Next step (tier):** Task 13 round 3 (Executor) → security re-review (Executor, other vendor or security-auditor) → Tasks 14–17 (rescue mode, `ferry` CLI, benchmarks/tuning, soak + milestone security review).
- **Touches (this update):** `AGENTS.md` (new; shared rules moved from CLAUDE.md so Codex sees them), `CLAUDE.md`, `STATE.md`, `HANDOFF.md`, `.claude/settings.json`, `.agents/`.

## Current state

| Area | State |
|---|---|
| M1 tasks 1–12 | Complete, each review-clean after ≤1 fix round |
| M1 task 13 (tree copy) | Implemented; refactor + task-review fixes clean; security round 2 applied (F1–F6, F8, F9); round 3 (N1–N4) pending |
| M1 tasks 14–17 | Not started |
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
