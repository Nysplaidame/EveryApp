---
id: 006
title: Implement the Ferry CLI for Task 15
status: done
tier: executor
agent: codex
reviewer: claude
base: feat/m1-foundation-ferry-engine
base_commit: 7f1bb7fe782526ae1d49fa620d08c60379f71fdb
branch: agent/006-implement-the-ferry-cli-for-task-15
acceptance_cmd: powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
created: 2026-11-12
---

## Goal
Complete the Ferry CLI described in Task 15 and verify it against the project's build and test workflow.

## Read first
- STATE.md - current project status
- docs/superpowers/specs/2026-11-06-ferry-suite-v1-design.md - relevant scope and security requirements in §14
- docs/superpowers/plans/2026-11-06-everyapp-m1-foundation-and-ferry-engine.md - Task 15 requirements

## Do
1. Check the current branch and Task 15 prerequisites.
2. Implement the CLI according to Task 15 and the relevant specification.
3. Add or update tests for the required CLI behavior.
4. Run the applicable checks through build.ps1 and fix failures.
5. Update the START HERE block and add a short HANDOFF.md entry with a Touches line.

## Don't touch
- Task 14 rescue mode and network retry
- Benchmark tuning and the milestone soak

## Acceptance
- [ ] `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1` passes
- [ ] The Task 15 CLI behavior is implemented and covered by tests.
- [ ] The applicable build.ps1 checks pass.
- [ ] STATE.md and HANDOFF.md reflect the completed work.

## Result
- Commit(s): `9ecf813` — `docs(ferry): record line-ending repair checks`
- Acceptance output: **FAIL** — `restore (locked)` exited 1. Separate checks: CLI whitespace passed; build succeeded with 0 warnings/errors; CLI tests 33 passed.
- Notes / follow-ups: CRLF repaired locally; Git normalization produces no source diff. STATE/HANDOFF updated. Full formatter encounters denied named-pipe access; rerun acceptance in an unrestricted build environment. Task 14 untouched.

**Dispatcher (2026-11-12):** status `done`; worktree `<worktree>`.
- Notes: acceptance failed at tier executor

<details><summary>Acceptance output (tail)</summary>

```

<worktree>\artifacts\bin\EveryApp.Core.Tests\release_win-x64\EveryApp.Core.Tests.dll (net10.0|x64) passed (6s 047ms)
<worktree>\artifacts\bin\Ferry.Engine.Tests\release_win-x64\Ferry.Engine.Tests.dll (net10.0|x64) passed (7s 887ms)
[+11/x0/?0] Ferry.Cli.Tests.dll (net10.0|x64)(8s)

[+15/x0/?0] Ferry.Cli.Tests.dll (net10.0|x64)(11s)

[+18/x0/?0] Ferry.Cli.Tests.dll (net10.0|x64)(14s)

[+21/x0/?0] Ferry.Cli.Tests.dll (net10.0|x64)(17s)

[+24/x0/?0] Ferry.Cli.Tests.dll (net10.0|x64)(20s)

[+26/x0/?0] Ferry.Cli.Tests.dll (net10.0|x64)(23s)

[+26/x0/?0] Ferry.Cli.Tests.dll (net10.0|x64)(26s)

[+26/x0/?0] Ferry.Cli.Tests.dll (net10.0|x64)(29s)

[+26/x0/?0] Ferry.Cli.Tests.dll (net10.0|x64)(32s)

[+28/x0/?0] Ferry.Cli.Tests.dll (net10.0|x64)(35s)

[+31/x0/?0] Ferry.Cli.Tests.dll (net10.0|x64)(38s)

[+33/x0/?0] Ferry.Cli.Tests.dll (net10.0|x64)(41s)

<worktree>\artifacts\bin\Ferry.Cli.Tests\release_win-x64\Ferry.Cli.Tests.dll (net10.0|x64) passed (44s 303ms)

Test run summary: Passed!
  <worktree>\artifacts\bin\EveryApp.Core.Tests\release_win-x64\EveryApp.Core.Tests.dll (net10.0|x64) passed (6s 047ms)
  <worktree>\artifacts\bin\Ferry.Cli.Tests\release_win-x64\Ferry.Cli.Tests.dll (net10.0|x64) passed (44s 303ms)
  <worktree>\artifacts\bin\Ferry.Engine.Tests\release_win-x64\Ferry.Engine.Tests.dll (net10.0|x64) passed (7s 887ms)

  total: 648
  failed: 0
  succeeded: 647
  skipped: 1
  duration: 44s 713ms
Build OK.
```
</details>

<details><summary>Review</summary>

The Ferry CLI meets Task 15 and the card's acceptance items. The one gap is the two Task 14 options, which return a clear exit 4 by design.

- **Do items:** copy, resume, verify and jobs are implemented on System.CommandLine, matching the Task 15 command surface. The details are all in place:
  - exit codes 0, 1, 2, 3, 4 and 10;
  - JSON lines with `progress`, `problem` and `result` events;
  - `--if-exists ask` prompts, and `ask` is rejected with `--json`;
  - the one-line progress display;
  - Ctrl+C cancels and a second Ctrl+C within 2 s exits immediately.

  Moves and automatic replacements show a dry-run preview and need confirmation or `--yes`. Resume goes through the engine's confirmation callback. Jobs history reads the journal without modifying it. The threat model is updated to match.
- **Don't touch:** the diff has no rescue or retry work and nothing in benchmarks. Engine and Core changes are small additive APIs (`SavedJob`, `JobJournal.ReadSummary`, a public `Verifier.HashAsync`).
- **Acceptance:** I ran the exact acceptance command, `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1`, in this worktree. It exited 0 with "Build OK", and 647 of 648 tests passed, 1 skipped and 0 failed. The locked restore passed, so the hand-edited `packages.lock.json` entries are valid.
- **Tests:** all seven tests the card names exist and check the right things. There are about 15 more, covering conflict rules, `--dry-run` writing nothing, move confirmation, unknown options, response files and corrupt journals.
- **Security:** I found no problems. Unknown options are rejected, response files and parser directives are disabled, and input length is bounded. Output goes through the scrubber, with no stack traces on stdout. Prompts can be cancelled, and the engine still refuses to run elevated.

Two non-blocking notes:
- `--rescue` and `--if-unreadable ask` are rejected with exit 4 until Task 14 lands. The task card's Don't-touch list forbids touching Task 14, so this fits, and the handoff and usage text record it.
- `STATE.md` and `HANDOFF.md` still say acceptance is blocked on the executor's environment. In this environment it passes. The statement is accurate for what the executor saw, but the next session should update it.

VERDICT: PASS
</details>

<details><summary>Branch log</summary>

```
9ecf813 docs(ferry): record line-ending repair checks
d490fb7 docs(ferry): record CLI validation limits
4a7f281 test(ferry): cover CLI commands and recovery
8c0ed4b feat(ferry): add command line interface
244ae73 feat(ferry): expose history and hash APIs
0139112 docs: record acceptance retry for task 15
a9ab013 docs: record task 15 blockers
7f1bb7f docs(spike): note Ultracopier Ctrl+V pastes into the left-most Explorer tab
c1d4c3b chore(agents): card 002 takes over task 14 (rescue + retry)
bbb283c docs(state): record task 14 pause
```
</details>
