---
id: 007
title: Run Task 16 benchmarks and apply justified tuning
status: blocked
tier: executor
agent: codex
reviewer: claude
base: feat/m1-foundation-ferry-engine
base_commit: 7f1bb7fe782526ae1d49fa620d08c60379f71fdb
branch: agent/007-run-task-16-benchmarks-and-apply-justifi
acceptance_cmd: powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
created: 2026-11-12
---

## Goal
Complete the Task 16 benchmarks and tuning, including the R27 levers specified in the plan, with results recorded for review.

## Read first
- STATE.md - current project status and prerequisites
- docs/superpowers/specs/2026-11-06-ferry-suite-v1-design.md - relevant performance and security requirements in §14
- docs/superpowers/plans/2026-11-06-everyapp-m1-foundation-and-ferry-engine.md - Task 16 requirements

## Do
1. Check that Task 16 prerequisites are complete.
2. Run the benchmarks specified for Task 16 and record baseline results.
3. Evaluate the R27 levers and make changes justified by the measurements.
4. Rerun the relevant benchmarks and checks through build.ps1.
5. Record results and update the START HERE block and HANDOFF.md with a Touches line.

## Don't touch
- Task 14 rescue mode and network retry
- Task 17 soak and milestone security review

## Acceptance
- [ ] `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1` passes
- [ ] Task 16 benchmark results include baseline and final measurements.
- [ ] Any tuning changes are supported by the recorded results.
- [ ] The applicable build.ps1 checks pass.

## Result
- **Commit(s):** `de3e835` — `docs: record acceptance recheck`
- **Acceptance output:** Fail. `build.ps1` exited 1 at locked restore; format, build and tests were not reached. The supplied huge-tree timing failure could not be reproduced in this run.
- **Notes / follow-ups:** Task 16 remains blocked by incomplete Tasks 14 and 15. Baseline, final and R27 measurements remain unrun; no tuning was applied. Integrate the prerequisites and re-dispatch with the benchmark tools and required hardware available.

**Dispatcher (2026-11-13):** status `blocked`; worktree `K:\Documents\Obsidian\worktrees\EveryApp\agent_007-run-task-16-benchmarks-and-apply-justifi`.
- Notes: review did not complete (reviewer failed or gave no verdict, twice); run the review again or merge-check by hand

<details><summary>Acceptance output (tail)</summary>

```
  All projects are up-to-date for restore.
==> format check
==> build
  EveryApp.Core -> K:\Documents\Obsidian\worktrees\EveryApp\agent_007-run-task-16-benchmarks-and-apply-justifi\artifacts\bin\EveryApp.Core\release_win-x64\EveryApp.Core.dll
  Ferry.Engine -> K:\Documents\Obsidian\worktrees\EveryApp\agent_007-run-task-16-benchmarks-and-apply-justifi\artifacts\bin\Ferry.Engine\release_win-x64\Ferry.Engine.dll
  EveryApp.Core.Tests -> K:\Documents\Obsidian\worktrees\EveryApp\agent_007-run-task-16-benchmarks-and-apply-justifi\artifacts\bin\EveryApp.Core.Tests\release_win-x64\EveryApp.Core.Tests.dll
  Ferry.Cli -> K:\Documents\Obsidian\worktrees\EveryApp\agent_007-run-task-16-benchmarks-and-apply-justifi\artifacts\bin\Ferry.Cli\release_win-x64\ferry.dll
  Ferry.Engine.Tests -> K:\Documents\Obsidian\worktrees\EveryApp\agent_007-run-task-16-benchmarks-and-apply-justifi\artifacts\bin\Ferry.Engine.Tests\release_win-x64\Ferry.Engine.Tests.dll
  Ferry.Cli.Tests -> K:\Documents\Obsidian\worktrees\EveryApp\agent_007-run-task-16-benchmarks-and-apply-justifi\artifacts\bin\Ferry.Cli.Tests\release_win-x64\Ferry.Cli.Tests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:09.13
==> test
Running tests from K:\Documents\Obsidian\worktrees\EveryApp\agent_007-run-task-16-benchmarks-and-apply-justifi\artifacts\bin\Ferry.Cli.Tests\release_win-x64\Ferry.Cli.Tests.dll (net10.0|x64)
Running tests from K:\Documents\Obsidian\worktrees\EveryApp\agent_007-run-task-16-benchmarks-and-apply-justifi\artifacts\bin\Ferry.Engine.Tests\release_win-x64\Ferry.Engine.Tests.dll (net10.0|x64)
K:\Documents\Obsidian\worktrees\EveryApp\agent_007-run-task-16-benchmarks-and-apply-justifi\artifacts\bin\Ferry.Cli.Tests\release_win-x64\Ferry.Cli.Tests.dll (net10.0|x64) passed (228ms)
Running tests from K:\Documents\Obsidian\worktrees\EveryApp\agent_007-run-task-16-benchmarks-and-apply-justifi\artifacts\bin\EveryApp.Core.Tests\release_win-x64\EveryApp.Core.Tests.dll (net10.0|x64)
skipped Ferry.Engine.Tests.TreeCopierRealFileTests.ReFS_SameVolume_UsesBlockClone (0ms)
  No ReFS volume on this PC.
  from K:\Documents\Obsidian\worktrees\EveryApp\agent_007-run-task-16-benchmarks-and-apply-justifi\artifacts\bin\Ferry.Engine.Tests\release_win-x64\Ferry.Engine.Tests.dll (net10.0|x64)
[+399/x0/?0] EveryApp.Core.Tests.dll (net10.0|x64)(2s)
[+170/x0/?1] Ferry.Engine.Tests.dll (net10.0|x64)(2s)

K:\Documents\Obsidian\worktrees\EveryApp\agent_007-run-task-16-benchmarks-and-apply-justifi\artifacts\bin\Ferry.Engine.Tests\release_win-x64\Ferry.Engine.Tests.dll (net10.0|x64) passed (4s 947ms)
K:\Documents\Obsidian\worktrees\EveryApp\agent_007-run-task-16-benchmarks-and-apply-justifi\artifacts\bin\EveryApp.Core.Tests\release_win-x64\EveryApp.Core.Tests.dll (net10.0|x64) passed (5s 083ms)

Test run summary: Passed!
  K:\Documents\Obsidian\worktrees\EveryApp\agent_007-run-task-16-benchmarks-and-apply-justifi\artifacts\bin\Ferry.Engine.Tests\release_win-x64\Ferry.Engine.Tests.dll (net10.0|x64) passed (4s 947ms)
  K:\Documents\Obsidian\worktrees\EveryApp\agent_007-run-task-16-benchmarks-and-apply-justifi\artifacts\bin\EveryApp.Core.Tests\release_win-x64\EveryApp.Core.Tests.dll (net10.0|x64) passed (5s 083ms)
  K:\Documents\Obsidian\worktrees\EveryApp\agent_007-run-task-16-benchmarks-and-apply-justifi\artifacts\bin\Ferry.Cli.Tests\release_win-x64\Ferry.Cli.Tests.dll (net10.0|x64) passed (228ms)

  total: 616
  failed: 0
  succeeded: 615
  skipped: 1
  duration: 5s 597ms
Build OK.
```
</details>

<details><summary>Branch log</summary>

```
de3e835 docs: record acceptance recheck
faee054 docs: restore task 16 handoff context
10072f3 fix(build): revert unmeasured worker limit
aee6a83 fix(build): serialize MSBuild workers
e783a70 docs(perf): detail pending task 16 measurements
ebed530 docs(perf): record acceptance retry
970ae52 docs(perf): record benchmark blockers
7f1bb7f docs(spike): note Ultracopier Ctrl+V pastes into the left-most Explorer tab
c1d4c3b chore(agents): card 002 takes over task 14 (rescue + retry)
bbb283c docs(state): record task 14 pause
```
</details>
