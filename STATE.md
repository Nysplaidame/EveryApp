# EveryApp — STATE (read this first, every session)

Only mandatory start-of-session read. Keep under ~200 lines. `/handoff` replaces the ▶ START HERE block; history goes in `HANDOFF.md`.

## ▶ START HERE (updated 2026-11-27, by Claude Opus 5.5 — large-file gap closed)

- **Branch:** `feat/m1-foundation-ferry-engine` (pushed to 6d98f5f, draft PR #1) has parallel copies. Session branch `claude/copy-app-next-features-798066` is ahead with Task 8 (large files: several chunks in flight) and its security fixes, not merged or pushed yet.
- **Task 8 (2026-11-27):** a large file keeps 4 reads and 4 writes in flight (was effectively one chunk at a time; a write to a fresh part file runs inside the Windows call); the check reads 4 ahead. Reopen after a network/sharing failure now checks identity on the opened handle and stops after a failed check (security review L1–L3 fixed, threat model updated). Large set vs FastCopy, 1 round: **verify off 0.78, verify on 1.00 — both pass** (were 1.28 / 1.39). Mixed, 3 rounds: off 0.68 (pass), on 1.45 (fail, was 1.62). Full `./build.ps1`: 871 tests, 870 passed, 1 skipped. Task 9: start-up cut from 0.73 s to 0.19 s by skipping the WMI Storage Spaces lookup unless a disk is a Storage Space (Spaces still pay ~0.56 s).
- **Old NVMe source is gone** (now in an array): the Storage Space → NVMe setup is the bench baseline from now on.
- **Just finished:** parallel copies. On solid-state drives a job copies up to 8 files at once (measured: 1/2/4/8 → 14.3/9.4/7.3/5.8 s on the small set); large files take turns and each is checked while the next copies. Hard drives, unknown drives, network, removable, rescue and same-volume moves stay one at a time. Names, conflicts and resume are still decided one file at a time in order. New `Ferry.Bench jobs` mode. Security-auditor review: L1 fixed, three low risks accepted (threat model 2026-11-26). Full `./build.ps1`: 849 tests, 848 passed, 1 skipped (ReFS). Final whole-branch review: ready to merge; its 7 minor findings fixed.
- **Speed gate (≤1.10 × FastCopy), 2026-11-27, source now a Storage Space:** mixed off 0.74 (pass, was 2.02), mixed on 1.62 (was 7.61), large now 0.78 / 1.00 after Task 8. Details in `docs/perf/results.md`. **Only mixed with verify on (1.45) still fails** (per-small-file read-back); HDD sets not run.
- **New accepted risk:** a finished copy job is never resumed, so a power cut seconds after it ends can leave small files blank (threat model; possible fix: one drive flush at job end).
- **Cards to close:** 002, 003, 004, 006 (merged), 005 (prep), 007 (superseded by this work; its branches `agent/007-run-task-16-…` and `agent/007-task16-bench-v2` hold only "blocked" notes, safe to delete).
- **M2 risk to carry:** Ferry's Ctrl+V hook will compete with copy tools like Ultracopier. Test with one running; decide detection/handling. Not yet in the spec.
- **Waiting on the user:** speed-gate decision; HDD back online for the HDD sets; optional Explorer spike Part A `--swallow` (step 17+) and Part C; left-drag decision before M2; run `scripts/New-SigningCert.ps1` before the first release. Bench data is still on the test drives (`ferry-bench`, `ferry-bench-out` folders, ~25 GB) for reruns.
- **Next step:** merge/push Task 8 (user's call); then the mixed-with-verify gap or task 17 (soak + milestone security review), user's choice.
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
