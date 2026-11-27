# EveryApp — STATE (read this first, every session)

Only mandatory start-of-session read. Keep under ~200 lines. `/handoff` replaces the ▶ START HERE block; history goes in `HANDOFF.md`.

## ▶ START HERE (updated 2026-11-27, by Claude Opus 5.5 — NVMe speed gate met)

- **Branch:** `feat/m1-foundation-ferry-engine` (pushed to d7b4395, draft PR #1). Session branch `claude/copy-app-next-features-798066` is ahead with Tasks 9–10, the fair bench order and results; not merged or pushed yet.
- **Done since 2026-11-26:** parallel copies (up to 8 files at once on SSDs; HDD, unknown, network, removable, rescue and same-volume moves one at a time). Task 8: a large file keeps 4 reads and 4 writes in flight, the check reads 4 ahead; reopen identity checked on the opened handle (security L1–L3 fixed). Task 9: start-up 0.73 → 0.19 s (WMI Storage Spaces lookup only when a disk is a Storage Space; Spaces still pay ~0.56 s). Task 10: sector size asked once per job root, not twice per checked file. `bench.ps1` now runs each tool off then on back to back. Full `./build.ps1`: 881 tests, 880 passed, 1 skipped.
- **Old NVMe source is gone** (now in an array): the Storage Space → NVMe setup is the bench baseline from now on.
- **Speed gate (≤1.10 × FastCopy), 2026-11-27, NVMe: met on every set.** Large 0.78 / 1.00 (verify off / on, 1 round); mixed 0.90 / 0.64 (3 rounds). Caveat: a FastCopy run slows whatever copy runs next on this PC (FastCopy too), so mixed ratios depend on run order; like for like, mixed verify on is about 0.81 (both after Ferry) or about 1.0 (both after FastCopy). Details in `docs/perf/results.md`. HDD sets not run.
- **New accepted risk:** a finished copy job is never resumed, so a power cut seconds after it ends can leave small files blank (threat model; possible fix: one drive flush at job end).
- **Cards to close:** 002, 003, 004, 006 (merged), 005 (prep), 007 (superseded by this work; its branches `agent/007-run-task-16-…` and `agent/007-task16-bench-v2` hold only "blocked" notes, safe to delete).
- **M2 risk to carry:** Ferry's Ctrl+V hook will compete with copy tools like Ultracopier. Test with one running; decide detection/handling. Not yet in the spec.
- **Waiting on the user:** merge/push of the session branch; HDD back online for the HDD sets; optional Explorer spike Part A `--swallow` (step 17+) and Part C; left-drag decision before M2; run `scripts/New-SigningCert.ps1` before the first release. Bench data is still on the test drives (`ferry-bench`, `ferry-bench-out` folders, ~25 GB) for reruns.
- **Next step:** merge/push (user's call), then task 17 (soak + milestone security review against the threat model). Open notes: the after-FastCopy slowdown (cause unknown); Storage Space runs pay ~0.56 s WMI; confirm a misaligned unbuffered write (mount point inside a destination) fails only that file.
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
