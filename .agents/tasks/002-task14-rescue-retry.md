---
id: 002
title: Ferry task 14 — rescue mode (failing drives) and network retry
status: proposed        # proposed → approved → running → done | blocked | paused | stopped | superseded
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

## Result (filled in by the executor)
- Commit(s):
- Acceptance output:
- Notes / follow-ups:
