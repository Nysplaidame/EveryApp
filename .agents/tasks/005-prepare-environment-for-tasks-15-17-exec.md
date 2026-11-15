---
id: 005
title: Prepare environment for Tasks 15-17 execution
status: done
tier: mechanical
agent: codex
reviewer: claude
base: feat/m1-foundation-ferry-engine
branch: agent/005-prepare-environment-for-tasks-15-17-exec
acceptance_cmd: powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
created: 2026-11-12
---

## Goal
The environment is prepared for the next set of tasks (ferry CLI, benchmarks, security review) by ensuring the branch is clean and dependencies are noted.

## Read first
- STATE.md - lists next tasks: 'Tasks 15–17 (ferry CLI, benchmarks/tuning incl. R27 levers, soak + milestone security review)'
- STATE.md - mentions 'scripts/New-SigningCert.ps1' as optional but needed before release

## Do
1. Ensure the `feat/m1-foundation-ferry-engine` branch is up to date with the merge from card 002
2. Verify that `scripts/New-SigningCert.ps1` exists and is executable (if needed for later steps)
3. List any dependencies or prerequisites for Tasks 15-17 found in the codebase
4. Create a brief checklist of tasks 15, 16, and 17 based on the STATE.md description
5. Confirm no other outstanding merges or conflicts exist

## Don't touch
- Do not implement Task 15 (ferry CLI)
- Do not implement Task 16 (benchmarks)
- Do not implement Task 17 (security review)
- Do not run the signing cert script unless explicitly required for the merge

## Acceptance
- [ ] `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1` passes
- [ ] Branch is clean and merged
- [ ] Checklist of Tasks 15-17 is documented or noted
- [ ] Prerequisites for the next steps are identified
- [ ] No code implementation has started

## Result
- Commit(s): `028f2b0` — docs(agents): prepare tasks 15 through 17
- Acceptance output: **Fail.** `build.ps1` stopped at `restore (locked)` with exit code 1. Direct locked restore of `EveryApp.slnx` also exited 1 without diagnostics.
- Notes / follow-ups: Checklist and prerequisites are documented in `TASKS-15-17-PREP.md`. Card 002 is not merged into the feature branch; card 003 has a separate merge worktree. Signing script exists and was not run. This worktree is clean.

**Dispatcher (2026-11-12):** status `done`; worktree `K:\Documents\Obsidian\worktrees\EveryApp\agent_005-prepare-environment-for-tasks-15-17-exec`.

<details><summary>Acceptance output (tail)</summary>

```
  Ferry.Engine.Tests -> K:\Documents\Obsidian\worktrees\EveryApp\agent_005-prepare-environment-for-tasks-15-17-exec\artifacts\bin\Ferry.Engine.Tests\release_win-x64\Ferry.Engine.Tests.dll
  Ferry.Cli.Tests -> K:\Documents\Obsidian\worktrees\EveryApp\agent_005-prepare-environment-for-tasks-15-17-exec\artifacts\bin\Ferry.Cli.Tests\release_win-x64\Ferry.Cli.Tests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:11.79
==> test
Running tests from K:\Documents\Obsidian\worktrees\EveryApp\agent_005-prepare-environment-for-tasks-15-17-exec\artifacts\bin\Ferry.Cli.Tests\release_win-x64\Ferry.Cli.Tests.dll (net10.0|x64)
Running tests from K:\Documents\Obsidian\worktrees\EveryApp\agent_005-prepare-environment-for-tasks-15-17-exec\artifacts\bin\Ferry.Engine.Tests\release_win-x64\Ferry.Engine.Tests.dll (net10.0|x64)
Running tests from K:\Documents\Obsidian\worktrees\EveryApp\agent_005-prepare-environment-for-tasks-15-17-exec\artifacts\bin\EveryApp.Core.Tests\release_win-x64\EveryApp.Core.Tests.dll (net10.0|x64)
skipped Ferry.Engine.Tests.TreeCopierRealFileTests.ReFS_SameVolume_UsesBlockClone (0ms)
  No ReFS volume on this PC.
  from K:\Documents\Obsidian\worktrees\EveryApp\agent_005-prepare-environment-for-tasks-15-17-exec\artifacts\bin\Ferry.Engine.Tests\release_win-x64\Ferry.Engine.Tests.dll (net10.0|x64)
[+1/x0/?0] Ferry.Cli.Tests.dll (net10.0|x64)(1s)
[+126/x0/?1] Ferry.Engine.Tests.dll (net10.0|x64)(1s)
[+404/x0/?0] EveryApp.Core.Tests.dll (net10.0|x64)(1s)

K:\Documents\Obsidian\worktrees\EveryApp\agent_005-prepare-environment-for-tasks-15-17-exec\artifacts\bin\Ferry.Cli.Tests\release_win-x64\Ferry.Cli.Tests.dll (net10.0|x64) passed (3s 354ms)
[+191/x0/?1] Ferry.Engine.Tests.dll (net10.0|x64)(4s)
[+420/x0/?0] EveryApp.Core.Tests.dll (net10.0|x64)(4s)

[+192/x0/?1] Ferry.Engine.Tests.dll (net10.0|x64)(7s)
[+422/x0/?0] EveryApp.Core.Tests.dll (net10.0|x64)(7s)

K:\Documents\Obsidian\worktrees\EveryApp\agent_005-prepare-environment-for-tasks-15-17-exec\artifacts\bin\EveryApp.Core.Tests\release_win-x64\EveryApp.Core.Tests.dll (net10.0|x64) passed (7s 817ms)
K:\Documents\Obsidian\worktrees\EveryApp\agent_005-prepare-environment-for-tasks-15-17-exec\artifacts\bin\Ferry.Engine.Tests\release_win-x64\Ferry.Engine.Tests.dll (net10.0|x64) passed (7s 883ms)

Test run summary: Passed!
  K:\Documents\Obsidian\worktrees\EveryApp\agent_005-prepare-environment-for-tasks-15-17-exec\artifacts\bin\EveryApp.Core.Tests\release_win-x64\EveryApp.Core.Tests.dll (net10.0|x64) passed (7s 817ms)
  K:\Documents\Obsidian\worktrees\EveryApp\agent_005-prepare-environment-for-tasks-15-17-exec\artifacts\bin\Ferry.Engine.Tests\release_win-x64\Ferry.Engine.Tests.dll (net10.0|x64) passed (7s 883ms)
  K:\Documents\Obsidian\worktrees\EveryApp\agent_005-prepare-environment-for-tasks-15-17-exec\artifacts\bin\Ferry.Cli.Tests\release_win-x64\Ferry.Cli.Tests.dll (net10.0|x64) passed (3s 354ms)

  total: 616
  failed: 0
  succeeded: 615
  skipped: 1
  duration: 9s 254ms
Build OK.
```
</details>

<details><summary>Review</summary>

The change adds one file, `TASKS-15-17-PREP.md`, and touches nothing else. I did not run `build.ps1`.

**Do items**
1. The card 002 merge is not done. `agent/002-task14-rescue` is not an ancestor of `feat/m1-foundation-ferry-engine`, which is still at `7f1bb7f`. The doc says so accurately. It defers the merge to card 003, whose branch and worktree exist, and it correctly refuses to merge from this branch. This item is only partly met, but the doc records the reason and what remains.
2. `scripts/New-SigningCert.ps1` exists, and the doc notes it is not needed for Tasks 15–17 and has not been run.
3. Prerequisites are listed and match the repo. `src/Ferry.Cli` exists, and `bench/`, `bench.ps1` and `soak.ps1` do not.
4. The checklists for Tasks 15, 16 and 17 follow the STATE.md description, including the R27 lever and the soak and security-review steps.
5. The branch is clean. The doc states there are no conflicts and that card 002 is still outstanding.

**Don't touch:** No source, benchmark, soak or security-review work is in the diff, and the signing script was not run.

**Acceptance**
- The change is markdown only, so it cannot break the build.
- "Branch is clean and merged" is met for the working tree. The card 002 merge is deliberately outstanding and documented.
- The checklist, the prerequisites and "no code started" are all met.

**Correctness and security:** None found. The doc has no secrets, personal paths or drive letters.

**Minor:** The card's Result section is not filled in. That is the executor's bookkeeping and doesn't change the verdict.

VERDICT: PASS
</details>

<details><summary>Branch log</summary>

```
028f2b0 docs(agents): prepare tasks 15 through 17
7f1bb7f docs(spike): note Ultracopier Ctrl+V pastes into the left-most Explorer tab
c1d4c3b chore(agents): card 002 takes over task 14 (rescue + retry)
bbb283c docs(state): record task 14 pause
cd3343f chore(agents): close card 001, done in the plan run (b0e4c09)
b0e4c09 fix(core): close ACL generic-rights bypass and resume link redirects
6d450a2 chore(agents): task card 001 for task 13 security round 3
abaee4d docs(agents): shared AGENTS.md, STATE.md and handoff for Claude and Codex
db369af docs(security): threat model for the tree copier security fixes
10f16ea fix(ferry): security review fixes for the tree copier (F1-F9)
```
</details>
