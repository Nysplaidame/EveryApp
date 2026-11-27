# EveryApp — STATE (read this first, every session)

Only mandatory start-of-session read. Keep under ~200 lines. `/handoff` replaces the ▶ START HERE block; history goes in `HANDOFF.md`.

## ▶ START HERE (updated 2026-11-27, by Claude Opus 5.5 — parallel copies built and measured)

- **Branch:** `claude/copy-app-next-features-798066` (session worktree, based on `feat/m1-foundation-ferry-engine`, not pushed). Plan `docs/superpowers/plans/2026-11-26-ferry-parallel-copy.md`, tasks 1–7 done; final whole-branch review pending.
- **Just finished:** parallel copies. On solid-state drives a job copies up to 8 files at once (measured: 1/2/4/8 → 14.3/9.4/7.3/5.8 s on the small set); large files take turns and each is checked while the next copies. Hard drives, unknown drives, network, removable, rescue and same-volume moves stay one at a time. Names, conflicts and resume are still decided one file at a time in order. New `Ferry.Bench jobs` mode. Security-auditor review: L1 fixed, three low risks accepted (threat model 2026-11-26). Full `./build.ps1`: 779 tests, 778 passed, 1 skipped (ReFS).
- **Speed gate (≤1.10 × FastCopy), 2026-11-27, source now a Storage Space:** mixed off 0.74 (pass, was 2.02), mixed on 1.62 (was 7.61), large off 1.28, large on 1.39. The large-file drop is the setup, not the code (before/after A/B identical). Details and next ideas in `docs/perf/results.md`. **Gate still not met; user decides** whether to chase large files / mixed-verify before M2.
- **New accepted risk:** a finished copy job is never resumed, so a power cut seconds after it ends can leave small files blank (threat model; possible fix: one drive flush at job end).
- **Cards to close:** 002, 003, 004, 006 (merged), 005 (prep), 007 (superseded by this work; its branches `agent/007-run-task-16-…` and `agent/007-task16-bench-v2` hold only "blocked" notes, safe to delete).
- **M2 risk to carry:** Ferry's Ctrl+V hook will compete with copy tools like Ultracopier. Test with one running; decide detection/handling. Not yet in the spec.
- **Waiting on the user:** speed-gate decision; HDD back online for the HDD sets; optional Explorer spike Part A `--swallow` (step 17+) and Part C; left-drag decision before M2; run `scripts/New-SigningCert.ps1` before the first release. Bench data is still on the test drives (`ferry-bench`, `ferry-bench-out` folders, ~25 GB) for reruns.
- **Next step:** final whole-branch review of the parallel-copy branch and merge into `feat/m1-foundation-ferry-engine` (user's call), then the user's speed-gate decision, then task 17 (soak + milestone security review).
- **Touches (this update):** `STATE.md`, `HANDOFF.md`, `docs/perf/results.md`.

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
