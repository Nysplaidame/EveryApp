---
id: 004
title: Document M2 risk regarding Ctrl+V hook competition
status: done
tier: mechanical
agent: codex
reviewer: claude
base: feat/m1-foundation-ferry-engine
branch: agent/004-document-m2-risk-regarding-ctrl-v-hook-c
acceptance_cmd: powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
created: 2026-11-12
---

## Goal
The M2 risk about Ferry's Ctrl+V hook competing with copy tools like Ultracopier is documented in the spec or a relevant file for future handling.

## Read first
- STATE.md - details the M2 risk: 'Ferry's Ctrl+V hook will compete with copy tools like Ultracopier'
- spikes/explorer-interception/MANUAL-TEST.md - existing spike documentation location

## Do
1. Review `spikes/explorer-interception/MANUAL-TEST.md` or the main spec file for M2 sections
2. Add a note or section describing the M2 risk: 'Ferry's Ctrl+V hook will compete with copy tools like Ultracopier'
3. Include the instruction to 'Test with one running; decide detection/handling'
4. Note that this is 'Not yet in the spec'
5. Save the file

## Don't touch
- Do not implement detection logic
- Do not modify existing code
- Do not run tests
- Do not create new files unless necessary for the spec

## Acceptance
- [ ] `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1` passes
- [ ] The M2 risk is clearly documented in the relevant file
- [ ] The text mentions Ultracopier and the need for detection/handling decisions
- [ ] No code changes are made
- [ ] The documentation is consistent with the STATE.md entry

## Result
- Commit(s): `14bbf1b` — `docs: record M2 Ctrl+V hook competition risk`
- Acceptance output: **Fail** — `build.ps1` stopped at `restore (locked)` with exit code 1. `git diff --check` passed.
- Notes / follow-ups: Added the M2 risk, Ultracopier, the instruction to test with one running and decide detection/handling, and “Not yet in the spec” to `MANUAL-TEST.md`. No code changed. Git printed a `packed-refs.lock` permission warning during commit, but the commit completed.

**Dispatcher (2026-11-12):** status `done`; worktree `<worktree>`.

<details><summary>Acceptance output (tail)</summary>

```
  Ferry.Cli -> <worktree>\artifacts\bin\Ferry.Cli\release_win-x64\ferry.dll
  Ferry.Engine.Tests -> <worktree>\artifacts\bin\Ferry.Engine.Tests\release_win-x64\Ferry.Engine.Tests.dll
  Ferry.Cli.Tests -> <worktree>\artifacts\bin\Ferry.Cli.Tests\release_win-x64\Ferry.Cli.Tests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:11.03
==> test
Running tests from <worktree>\artifacts\bin\Ferry.Cli.Tests\release_win-x64\Ferry.Cli.Tests.dll (net10.0|x64)
Running tests from <worktree>\artifacts\bin\Ferry.Engine.Tests\release_win-x64\Ferry.Engine.Tests.dll (net10.0|x64)
Running tests from <worktree>\artifacts\bin\EveryApp.Core.Tests\release_win-x64\EveryApp.Core.Tests.dll (net10.0|x64)
skipped Ferry.Engine.Tests.TreeCopierRealFileTests.ReFS_SameVolume_UsesBlockClone (0ms)
  No ReFS volume on this PC.
  from <worktree>\artifacts\bin\Ferry.Engine.Tests\release_win-x64\Ferry.Engine.Tests.dll (net10.0|x64)
[+1/x0/?0] Ferry.Cli.Tests.dll (net10.0|x64)(1s)
[+143/x0/?1] Ferry.Engine.Tests.dll (net10.0|x64)(1s)
[+384/x0/?0] EveryApp.Core.Tests.dll (net10.0|x64)(1s)

<worktree>\artifacts\bin\Ferry.Cli.Tests\release_win-x64\Ferry.Cli.Tests.dll (net10.0|x64) passed (3s 351ms)
[+191/x0/?1] Ferry.Engine.Tests.dll (net10.0|x64)(4s)
[+422/x0/?0] EveryApp.Core.Tests.dll (net10.0|x64)(4s)

<worktree>\artifacts\bin\EveryApp.Core.Tests\release_win-x64\EveryApp.Core.Tests.dll (net10.0|x64) passed (7s 343ms)
[+192/x0/?1] Ferry.Engine.Tests.dll (net10.0|x64)(7s)

<worktree>\artifacts\bin\Ferry.Engine.Tests\release_win-x64\Ferry.Engine.Tests.dll (net10.0|x64) passed (8s 302ms)

Test run summary: Passed!
  <worktree>\artifacts\bin\Ferry.Engine.Tests\release_win-x64\Ferry.Engine.Tests.dll (net10.0|x64) passed (8s 302ms)
  <worktree>\artifacts\bin\Ferry.Cli.Tests\release_win-x64\Ferry.Cli.Tests.dll (net10.0|x64) passed (3s 351ms)
  <worktree>\artifacts\bin\EveryApp.Core.Tests\release_win-x64\EveryApp.Core.Tests.dll (net10.0|x64) passed (7s 343ms)

  total: 616
  failed: 0
  succeeded: 615
  skipped: 1
  duration: 9s 566ms
Build OK.
```
</details>

<details><summary>Review</summary>

The change meets the card. It is one docs-only commit (`14bbf1b`) that adds a short "M2 risk: Ctrl+V hook competition" section to `spikes/explorer-interception/MANUAL-TEST.md`.

1. **Do items:** All are done. The section sits in the spike's MANUAL-TEST.md, which the card names as an allowed location. It says:
   - Ferry's Ctrl+V hook will compete with copy tools like Ultracopier.
   - This is "Not yet in the spec".
   - During M2, "test with one running; decide detection/handling".
2. **Don't touch:** Nothing else changed. No code, no tests, no new files, and no detection logic.
3. **Acceptance:**
   - **Documentation:** The risk is documented and the text names Ultracopier and the detection/handling decision. This is a markdown-only change, so I don't expect it to break `build.ps1`. I didn't run the build because this review is read-only.
   - **STATE.md consistency:** I could not check this. STATE.md in this worktree has no entry mentioning Ctrl+V or Ultracopier, so there was nothing to compare against. The wording matches the card's quoted text.
4. **Correctness and security:** No issues. The note is easy to miss in a manual-test document. Once the spec covers this risk, it belongs there too, but the card didn't require it.

VERDICT: PASS
</details>

<details><summary>Branch log</summary>

```
14bbf1b docs: record M2 Ctrl+V hook competition risk
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
