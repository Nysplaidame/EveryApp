---
id: 007
title: Run Task 16 benchmarks and apply justified tuning
status: approved
tier: executor
agent: codex
reviewer: claude
base: feat/m1-foundation-ferry-engine
base_commit: 418185d88d77824e5f47041abcb6349c1723b378
branch: agent/007-task16-bench-v2
acceptance_cmd: powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
created: 2026-11-19
---

## Goal
Build the Task 16 benchmark harness, measure Ferry against FastCopy, and tune the R27 levers only where the measurements justify it.

## Background (rebuilt 2026-11-19)
The first attempt (branch `agent/007-run-task-16-benchmarks-and-apply-justifi`, kept for reference) started from a base older than tasks 14 and 15, so it never had the `ferry` CLI and only wrote "blocked" notes. Tasks 1–15 are now merged into this base: the `ferry copy` CLI exists (`src/Ferry.Cli`), and rescue mode and network retry are done. Do not reuse the old branch's notes; `docs/perf/results.md` does not exist yet on this base.

## Read first
- STATE.md: current status
- docs/superpowers/plans/2026-11-06-everyapp-m1-foundation-and-ferry-engine.md: the "Task 16" section only
- docs/superpowers/specs/2026-11-06-ferry-suite-v1-design.md: §14 (security), and the performance gate
- src/Ferry.Engine/FileCopier.cs: the tunable constants

## Do
1. Build the harness from the plan: `bench/Ferry.Bench/*` (BenchmarkDotNet micro-benchmarks: `BufferPool` rent/return, XxHash3 throughput, `FileCopier` pipeline on a temp folder), `bench.ps1 [-Generate]` and `bench.example.json`. Real paths go only in the git-ignored `bench.local.json` (add it to `.gitignore`). Pin any new package in `Directory.Packages.props` and the lock files, exactly as `build.ps1`'s locked restore needs.
2. Keep the bench project out of the normal `build.ps1` test run, so CI stays fast. It must still build and pass the format check.
3. If `bench.local.json` exists and names a FastCopy executable, run the full comparison (3 rounds, alternating, verify on and off). If it doesn't, run the Ferry-only runs and the micro-benchmarks, and write "FastCopy comparison not run: not configured on this PC" in the results.
4. If `EmptyStandbyList` isn't available, say so in the results instead of failing.
5. Try the R27 levers (buffer 1/4/8/16 MiB, channel depth 2/4/8, unbuffered threshold 1/8/32 MiB, small-file ordering). Change a default in `FileCopier.cs` only when the measurements show it is faster; record the numbers behind each kept or changed default.
6. Write `docs/perf/results.md`: hardware described by drive model only (no drive letters, paths or machine names), medians, ratios, the gate result for each set, and any gap with its cause.
7. Run `build.ps1`. Update the START HERE block in STATE.md and add a HANDOFF.md entry with a `Touches:` line.

## Don't touch
- Rescue mode and network retry (task 14), the CLI's behaviour (task 15)
- Task 17 (soak test, milestone security review)
- Any committed file with a personal path, drive letter or machine name

## Notes
- `TreeCopierSecurityTests.HugeTree_PlansQuickly` failed once on the old branch by timing (2.2 s on a busy machine). If it fails here, report it; don't loosen it as part of this card.
- If the gate (ratio ≤ 1.10 on every set) isn't met, record the gap and stop. The user decides before M2.

## Acceptance
- [ ] `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1` passes
- [ ] `bench.ps1` and `bench/Ferry.Bench` exist and run; `bench.example.json` holds placeholder paths only
- [ ] `docs/perf/results.md` has baseline and final numbers (or says plainly which runs could not happen and why)
- [ ] Every tuning change is backed by a number in `results.md`
