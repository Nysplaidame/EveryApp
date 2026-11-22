# EveryApp — STATE (read this first, every session)

Only mandatory start-of-session read. Keep under ~200 lines. `/handoff` replaces the ▶ START HERE block; history goes in `HANDOFF.md`.

## ▶ START HERE (updated 2026-11-22, by Claude Opus 5.5 — task 16 measured; speed gate NOT met)

- **Branch:** `feat/m1-foundation-ferry-engine` (draft PR #1). M1 tasks 1–16 done; task 16 ended with the gate not met (below).
- **Just finished:** task 16 in-session (card 007 superseded; Codex's sandbox could not reach NuGet or the disks). Harness: `bench/Ferry.Bench` + `bench.ps1` (config in git-ignored `bench.local.json`). Results and causes: `docs/perf/results.md`. Engine changes: small copies skip the per-file flush (journaled `unflushed`, resume rechecks this job's own files); verify reads small files back through their open unbuffered handle (avoids the antivirus wait on reopen); part files lost write-through. Security-auditor review: M1/M2/L1–L3 fixed (threat-model review log 2026-11-22). Full `./build.ps1`: 729 tests, 728 passed, 1 skipped (ReFS).
- **Speed gate (≤1.10 × FastCopy):** NVMe large verify off 0.52 (pass); large verify on 1.17; mixed 2.02 / 7.61 (off / on). HDD sets not run (HDD offline). **Needs the user's decision before M2** (spec: tell the user): options in `results.md` — copy several small files at once, overlap large-file verify, trim per-file work.
- **New accepted risk:** a finished copy job is never resumed, so a power cut seconds after it ends can leave small files blank (threat model; possible fix: one drive flush at job end).
- **Cards to close:** 002, 003, 004, 006 (merged), 005 (prep), 007 (superseded by this work; its branches `agent/007-run-task-16-…` and `agent/007-task16-bench-v2` hold only "blocked" notes, safe to delete).
- **M2 risk to carry:** Ferry's Ctrl+V hook will compete with copy tools like Ultracopier. Test with one running; decide detection/handling. Not yet in the spec.
- **Waiting on the user:** speed-gate decision; HDD back online for the HDD sets; optional Explorer spike Part A `--swallow` (step 17+) and Part C; left-drag decision before M2; run `scripts/New-SigningCert.ps1` before the first release. Bench data is still on the test drives (`ferry-bench`, `ferry-bench-out` folders, ~25 GB) for reruns.
- **Next step:** user's speed-gate decision, then task 17 (soak + milestone security review against the threat model).
- **Touches (this update):** `STATE.md`, `HANDOFF.md`, `.agents/tasks/007-…`, `docs/perf/results.md`.

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
