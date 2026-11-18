---
id: 002
title: Ferry task 14 — rescue mode (failing drives) and network retry
status: done        # proposed → approved → running → done | blocked | paused | stopped | superseded
tier: executor
agent: codex
effort: high            # strong-executor work: sector-level I/O, failure semantics
reviewer: claude
base: wip/task14-partial
base_commit: 593e293    # partial work saved from the paused 2026-11-09 run (tests written, RED)
branch: agent/002-task14-rescue
acceptance_cmd: powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
created: 2026-11-11
---

## Goal
Finish M1 task 14: Ferry recovers what it can from failing drives (rescue mode) and rides out network drops (retry policy), with the full build and test suite green. Task 14 is owned by this card now, not by the superpowers plan run.

**Starting point.** The branch starts from `593e293` on `wip/task14-partial`: a paused implementer's partial work — `RescueReader.cs`, `RetryPolicy.cs`, their tests, `TreeCopierRescueTests.cs`, and edits to `IFileSystem`, the fakes and `WindowsWriteHandle`. Tests were written and failing (RED, 42/54 at pause); GREEN was not reached. Build on it where it's sound; rewrite or drop what isn't, and say which in *Result*.

**Decisions already made for this task (from the plan's progress log, binding):**
> rescue only on BadBlock + RescueMode, lost-sector cap, rescued file never clean success / never deletes move source, lost report, RetryPolicy w/ TimeProvider, reopen handles + OpenWriteExisting

Read that as:
1. Rescue runs **only** when the read error is `BadBlock` **and** `CopyOptions.RescueMode` is on. Off → a bad block fails the file as before.
2. A **cap on lost sectors** per file; past it, give up on the file instead of zero-filling indefinitely. Pick a sensible default, make it an option, document it.
3. A rescued file is **never** reported as a clean success, and a rescued file **never** causes a move to delete its source.
4. A **lost report**: `<dest>.ferry-lost.txt` listing the lost byte ranges in plain words.
5. `RetryPolicy` takes a **`TimeProvider`** (no real sleeps in tests).
6. On retry after a network drop, **reopen handles** and continue writing via **`OpenWriteExisting`**, not by recreating the file.

If the code shows one of these meant something different, follow the code's established meaning and say so in *Result*.

## Read first
- `AGENTS.md` — working rules and Security section (spec §14 applies; destructive behaviour must stay safe).
- `docs/superpowers/plans/2026-11-06-everyapp-m1-foundation-and-ferry-engine.md` — **"Task 14: Rescue mode (failing drives) and network resilience"** section only (interfaces, the five named tests, rescue/retry behaviour).
- `docs/superpowers/specs/2026-11-06-ferry-suite-v1-design.md` — only the parts on rescue mode / failing drives / network drives, and §14 (security).
- Code: `src/Ferry.Engine/FileCopier.cs`, `src/Ferry.Engine/RescueReader.cs`, `src/Ferry.Engine/RetryPolicy.cs`, `src/Ferry.Engine/CopyMessages.cs`, `src/Ferry.Engine/CopyProgress.cs`, `src/EveryApp.Core/FileSystem/IFileSystem.cs`, and the matching tests. Search for anything else; don't read unrelated files.

## Do
1. Run `build.ps1` first and note what fails (the starting RED).
2. Make the five plan tests pass as specified: `Rescue_BadSectorInMiddle_RecoversRestAndReportsRange`, `Rescue_Off_BadBlockFailsFile`, `Copy_NetworkDropsThenReturns_ResumesAndCompletes` (status text "Waiting for the network drive…"), `Retry_GivesUpAfterMaxWait_ReportsProblem`, `Rescue_WritesLostReport` — plus tests for each decision above (cap, never-clean-success, move source kept, TimeProvider, reopen via `OpenWriteExisting`).
3. Rescue: on a BadBlock read, bisect the chunk down to the sector size, retry each sector twice, zero-fill unreadable sectors, record `LostRange`s. Retry: exponential backoff 1 s → 30 s cap, ~5 min total by default; transient = `NetworkLost` and `SharingViolation`.
4. Wire both into `FileCopier` (and the tree copier where the existing partial work already does).
5. User-facing strings plain and short (AGENTS.md working rules).
6. Small Conventional Commits; the final one `feat(ferry): rescue mode and network retry`.

## Don't touch
- Tasks 15–17 work (CLI, benchmarks, soak). The WriteThrough / in-flight-write tuning (ruling R27) belongs to task 16.
- Public behaviour outside rescue/retry; no refactors beyond what this needs.
- `main`, `feat/m1-foundation-ferry-engine`, `wip/task14-partial` — work only on this card's branch. Don't push.

## Acceptance
- [ ] `build.ps1` passes: locked restore, format check, build, full test suite.
- [ ] The five plan tests and the decision tests exist and pass; none skipped.
- [ ] A move with any lost range keeps its source; a rescued file's outcome is not "success".
- [ ] No personal paths, drive letters or machine names added (public repo).

## Result
- **Commit(s):** `3b01aa9 chore(agent): card 002 edits by local Qwen (exec-executor)`; `838732e docs(state): record task 14 acceptance blocker`.
- **Acceptance output:** **FAIL** — `build.ps1` stopped at locked restore. Single-worker locked restore and the Release build passed with 0 warnings and 0 errors. The format check and full test runner were denied named-pipe access; the latter ran zero tests.
- **Notes / follow-ups:** The partial rescue and retry work was retained and extended in `3b01aa9`. Full acceptance requires an environment with restore and named-pipe access. The requested final `feat(ferry): rescue mode and network retry` commit remains outstanding. The task card is outside this worktree, so its Result section was not edited.

**Dispatcher (2026-11-12):** status `done`; worktree `K:\Documents\Obsidian\worktrees\EveryApp\agent_002-task14-rescue`.

<details><summary>Acceptance output (tail)</summary>

```
  EveryApp.Core -> K:\Documents\Obsidian\worktrees\EveryApp\agent_002-task14-rescue\artifacts\bin\EveryApp.Core\release_win-x64\EveryApp.Core.dll
  Ferry.Engine -> K:\Documents\Obsidian\worktrees\EveryApp\agent_002-task14-rescue\artifacts\bin\Ferry.Engine\release_win-x64\Ferry.Engine.dll
  EveryApp.Core.Tests -> K:\Documents\Obsidian\worktrees\EveryApp\agent_002-task14-rescue\artifacts\bin\EveryApp.Core.Tests\release_win-x64\EveryApp.Core.Tests.dll
  Ferry.Cli -> K:\Documents\Obsidian\worktrees\EveryApp\agent_002-task14-rescue\artifacts\bin\Ferry.Cli\release_win-x64\ferry.dll
  Ferry.Engine.Tests -> K:\Documents\Obsidian\worktrees\EveryApp\agent_002-task14-rescue\artifacts\bin\Ferry.Engine.Tests\release_win-x64\Ferry.Engine.Tests.dll
  Ferry.Cli.Tests -> K:\Documents\Obsidian\worktrees\EveryApp\agent_002-task14-rescue\artifacts\bin\Ferry.Cli.Tests\release_win-x64\Ferry.Cli.Tests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:11.28
==> test
Running tests from K:\Documents\Obsidian\worktrees\EveryApp\agent_002-task14-rescue\artifacts\bin\Ferry.Cli.Tests\release_win-x64\Ferry.Cli.Tests.dll (net10.0|x64)
Running tests from K:\Documents\Obsidian\worktrees\EveryApp\agent_002-task14-rescue\artifacts\bin\Ferry.Engine.Tests\release_win-x64\Ferry.Engine.Tests.dll (net10.0|x64)
Running tests from K:\Documents\Obsidian\worktrees\EveryApp\agent_002-task14-rescue\artifacts\bin\EveryApp.Core.Tests\release_win-x64\EveryApp.Core.Tests.dll (net10.0|x64)
skipped Ferry.Engine.Tests.TreeCopierRealFileTests.ReFS_SameVolume_UsesBlockClone (0ms)
  No ReFS volume on this PC.
  from K:\Documents\Obsidian\worktrees\EveryApp\agent_002-task14-rescue\artifacts\bin\Ferry.Engine.Tests\release_win-x64\Ferry.Engine.Tests.dll (net10.0|x64)
[+1/x0/?0] Ferry.Cli.Tests.dll (net10.0|x64)(2s)
[+218/x0/?1] Ferry.Engine.Tests.dll (net10.0|x64)(2s)
[+422/x0/?0] EveryApp.Core.Tests.dll (net10.0|x64)(2s)

K:\Documents\Obsidian\worktrees\EveryApp\agent_002-task14-rescue\artifacts\bin\Ferry.Cli.Tests\release_win-x64\Ferry.Cli.Tests.dll (net10.0|x64) passed (3s 236ms)
K:\Documents\Obsidian\worktrees\EveryApp\agent_002-task14-rescue\artifacts\bin\EveryApp.Core.Tests\release_win-x64\EveryApp.Core.Tests.dll (net10.0|x64) passed (5s 478ms)
[+247/x0/?1] Ferry.Engine.Tests.dll (net10.0|x64)(5s)

K:\Documents\Obsidian\worktrees\EveryApp\agent_002-task14-rescue\artifacts\bin\Ferry.Engine.Tests\release_win-x64\Ferry.Engine.Tests.dll (net10.0|x64) passed (7s 281ms)

Test run summary: Passed!
  K:\Documents\Obsidian\worktrees\EveryApp\agent_002-task14-rescue\artifacts\bin\Ferry.Engine.Tests\release_win-x64\Ferry.Engine.Tests.dll (net10.0|x64) passed (7s 281ms)
  K:\Documents\Obsidian\worktrees\EveryApp\agent_002-task14-rescue\artifacts\bin\EveryApp.Core.Tests\release_win-x64\EveryApp.Core.Tests.dll (net10.0|x64) passed (5s 478ms)
  K:\Documents\Obsidian\worktrees\EveryApp\agent_002-task14-rescue\artifacts\bin\Ferry.Cli.Tests\release_win-x64\Ferry.Cli.Tests.dll (net10.0|x64) passed (3s 236ms)

  total: 671
  failed: 0
  succeeded: 670
  skipped: 1
  duration: 7s 632ms
Build OK.
```
</details>

<details><summary>Review</summary>

The change meets the card's decisions and acceptance items. The one gap is item 6, the commit structure, and it doesn't block a pass. I read the diff and tests but did not run `build.ps1` myself. The green run below is the dispatcher's pasted output.

**Earlier review is out of date.** The branch has two commits (`3b01aa9`, `838732e`) and a clean tree, so the "nothing committed" finding no longer applies. The dispatcher's acceptance tail shows `build.ps1` ending in "Build OK" with 670 passed, 0 failed and 1 skipped. The skipped test is `ReFS_SameVolume_UsesBlockClone`, which is skipped because this PC has no ReFS volume. It is unrelated to this task.

**Do items**
- **1.** The starting RED run is not recorded anywhere I can see. This is minor.
- **2.** The five plan tests all exist and none are skipped:
  - `Rescue_BadSectorInMiddle_RecoversRestAndReportsRange`
  - `Rescue_Off_BadBlockFailsFile`
  - `Rescue_WritesLostReport`
  - `Copy_NetworkDropsThenReturns_ResumesAndCompletes` (asserts "Waiting for the network drive…")
  - `Retry_GivesUpAfterMaxWait_ReportsProblem`
  
  Tests for the decisions also exist:
  - Cap: `Rescue_LostSectorCap_AppliesAcrossChunks` and `Rescue_TooMuchLost_FailsFileWithPlainProblem`.
  - Never a clean success: `Rescue_RescuedFile_IsAWarningNotACleanSuccess…` and `Copy_RescuedFile_JobIsNotAllVerified`.
  - Move keeps its source: `Move_RescuedFile_KeepsTheSource`.
  - TimeProvider: the retry tests use it.
  - Reopen via `OpenWriteExisting`: `BeforeOpenWriteExisting` is used in the mid-write drop tests.
- **3, 4.** Rescue is wired in only when `RescueMode` is on, and it catches only `BadBlock` (`FileCopier.cs`). Other error kinds are tested to fail normally.
  - The cap is the `MaxLostSectors` option, default 4,096 and documented. It is validated in `FileCopier` and `JobSpec`.
  - `RetryPolicy` takes a `TimeProvider` and treats `NetworkLost` and `SharingViolation` as transient.
  - The retry wrappers reopen the read and write handles.
- **5.** The strings are plain and short.
- **6.** Partly missed. The work is in one `chore(agent): card 002 edits…` commit plus a docs commit. There is no `feat(ferry): rescue mode and network retry` and no small split. This commit structure is the dispatcher's, not the implementer's. The acceptance list doesn't require commit messages, so I'm not failing it on that. Reword or squash before merging if you want the final commit message the card asks for.

**Don't touch.** No changes under the CLI, benchmarks, or the task 16 tuning. The protected branches are untouched.

**Acceptance**
- `build.ps1`: green in the dispatcher's output, as above.
- Move with lost ranges: `TreeJob.cs` skips `DeleteSource` when `outcome.Lost.Count > 0`, adds a "kept original" problem, does not count the file as verified, and does not journal it as done.
- A rescued file's outcome is `Success=true` with a warning `Problem` and a non-empty `Lost` list. That is the existing "saved with a warning" convention, and the `FileCopyOutcome` doc comment and tests say it isn't a clean success.
- No personal paths, drive letters or machine names were added.

**Correctness and security.** I found nothing blocking.
- `LostReport` writes to a part file and moves it with `overwrite: false`. It checks the path stays under the destination root. It never replaces an existing report, and it keeps the name within 255 characters.
- Reopening the write handle aborts if the final path has changed, and it never deletes through a redirected name.
- When the cap is hit or retry gives up, the outcome is `NoRetry`, so the tree-level retry doesn't repeat the work.
- After a reopened writer, a verify pass hashes the source with the lost ranges treated as zeros.
- Merged lost ranges are capped at `int.MaxValue`.

VERDICT: PASS
</details>

<details><summary>Branch log</summary>

```
838732e docs(state): record task 14 acceptance blocker
3b01aa9 chore(agent): card 002 edits by local Qwen (exec-executor)
593e293 wip(ferry): partial task 14 rescue mode + network retry (RED: tests written, not passing)
bbb283c docs(state): record task 14 pause
cd3343f chore(agents): close card 001, done in the plan run (b0e4c09)
b0e4c09 fix(core): close ACL generic-rights bypass and resume link redirects
6d450a2 chore(agents): task card 001 for task 13 security round 3
abaee4d docs(agents): shared AGENTS.md, STATE.md and handoff for Claude and Codex
db369af docs(security): threat model for the tree copier security fixes
10f16ea fix(ferry): security review fixes for the tree copier (F1-F9)
```
</details>
