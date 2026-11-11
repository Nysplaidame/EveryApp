---
id: 001
title: Ferry task 13 — security fix round 3 (N1–N4 + threat-model corrections)
status: done            # proposed → approved → running → done | blocked | paused | stopped | superseded
tier: executor
agent: codex
reviewer: claude
base: feat/m1-foundation-ferry-engine
branch: agent/001-task13-security-round3
copy_in:                # git-ignored files the dispatcher copies into the worktree
  - .superpowers/sdd/2026-11-06-everyapp-m1-foundation-and-ferry-engine/task-13-brief.md
  - .superpowers/sdd/2026-11-06-everyapp-m1-foundation-and-ferry-engine/task-13-report.md
acceptance_cmd: powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
created: 2026-11-09
---

## Goal
Close the four findings left open by the round-2 security re-review of the tree copier, and correct two overstated rows in the threat model, so task 13 can pass security re-review. Ruling R33: this is a narrow round for a fresh implementer.

**Findings as recorded (verbatim, progress log 2026-11-07):**
> Task 13: fix round 2/5 (F1-F6, F8, F9 addressed; F7 open via N1 Medium: GENERIC_ALL/GENERIC_WRITE ACEs bypass broad-write filter; new Low: N2 resume part delete/target metadata by path through junction; N3 elevation seam on public ctor; N4 no access recheck after revert + BroadGroups missing INTERACTIVE/Guests/Domain Users; threat model I row + accepted-risk row 2 overstate; commits 7b52e83..db369af)

The *Do* steps below are the planner's reading of those one-liners. If the code shows a finding means something different, fix what the finding actually describes and say so in *Result*.

## Read first
- `AGENTS.md` — Security section and working rules (spec §14 applies).
- `.superpowers/sdd/2026-11-06-everyapp-m1-foundation-and-ferry-engine/task-13-report.md` — section **"Fix round 2 (security review, ruling R32)"** only: what was changed for F1–F9 and how.
- `.superpowers/sdd/2026-11-06-everyapp-m1-foundation-and-ferry-engine/task-13-brief.md` — the original task brief, for constraints.
- `docs/security/threat-model.md` — the **I** (information disclosure) rows and **Accepted risks**.
- Code: `src/EveryApp.Core/FileSystem/WindowsFileSystem.Safety.cs`, `src/Ferry.Engine/ResumeRecovery.cs`, `src/Ferry.Engine/TreeCopier.cs` (constructor), `src/EveryApp.Core/Power/ProcessElevation.cs`. Find `BroadGroups` with search; don't read unrelated files.

## Do
1. **N1 (Medium) — F7 still open.** The CopySecurity broad-write filter misses ACEs that grant `GENERIC_ALL` or `GENERIC_WRITE` (generic rights not mapped to file-specific rights). Map generic rights to file rights before filtering (or treat any generic write/all as broad-write) so those ACEs are filtered like `FILE_WRITE_DATA` ones. Test: an ACE granting `GENERIC_ALL` / `GENERIC_WRITE` to a broad group is removed.
2. **N2 (Low).** Resume recovery deletes journaled part files and applies target metadata **by path**, so a junction swapped in between can redirect the delete/metadata write. Use the same handle-based identity checks round 2 introduced for F2/F3 (open by handle, verify with `GetFinalPathNameByHandle` + FileId, delete via `FileDispositionInfoEx` on the verified handle). Test: a junction substituted in the destination path makes recovery refuse, not follow.
3. **N3 (Low).** The elevation check (engine refuses to run when the token is elevated, R32/F1) can be bypassed through the `TreeCopier` public constructor seam. Make the check unbypassable from public API (e.g. internal seam for tests only via `InternalsVisibleTo`, public ctor always uses the real elevation probe). Test: the public ctor path refuses when elevated (fake probe through the internal seam).
4. **N4 (Low).** After reverting to inherited security (F7 fallback), there's no recheck that the owner still has access; and `BroadGroups` is missing **INTERACTIVE**, **Guests** and **Domain Users**. Add the post-revert access recheck (fail the file with a plain message if access is still wrong) and add the three SIDs (well-known SIDs, not names). Tests for both.
5. **Threat model:** correct the **I** row covering copy permissions and **Accepted risks row 2** — round-2 review found both overstate what the code guarantees. Make them describe exactly what the code now does after N1–N4; no new claims.
6. Commit in small Conventional Commits on the branch (`fix(ferry): …`, `docs(security): …`).

## Don't touch
- Anything outside the files listed above plus their test files and `docs/security/threat-model.md`.
- Public behaviour beyond N1–N4 (no refactors, no new features, no renames).
- Deferred minors listed in the progress log — they're for later.
- Don't push. Don't merge.

## Acceptance
- [ ] `build.ps1` passes: locked restore, format check, build, full test suite (the dispatcher runs this).
- [ ] One or more new tests per finding (N1, N2, N3, N4 ×2), each failing before the fix.
- [ ] Threat-model rows match the code exactly — reviewer checks each claim against the diff.
- [ ] No personal paths, drive letters or machine names added (public repo).

## Result
- **Done outside the dispatcher** (2026-11-09): the superpowers plan run dispatched a fresh implementer per ruling R33.
- Commit(s): `b0e4c09` fix(core): close ACL generic-rights bypass and resume link redirects (N1–N4 + threat-model corrections).
- Acceptance: per the plan's progress log, review and security review clean after 3 fix rounds; task 13 complete (`16257e6..b0e4c09`).
- Notes / follow-ups: deferred minors are in the progress log (review-log row order; Domain Users also matches local RID 513; DeletePart swallows access-denied).
