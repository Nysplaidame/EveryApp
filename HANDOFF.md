# EveryApp — HANDOFF (recent entries only)

Newest at the top. Each entry: `## <what> — <date> (<agent>)`, ≤10 lines, ending with a `Touches:` line.
Keep under ~300 lines; move older entries to `docs/archive/HANDOFF-history-<from>-<to>.md` in the same commit.
Live state belongs in `STATE.md`; plan-execution detail in the superpowers progress log; decisions in `docs/adr/`.

## Task 14 acceptance and commit retry - 12 November 2026 (Codex)

The required build again failed at locked restore; single-worker restore and Release build passed, while format was denied named-pipe access. Direct rescue/retry tests passed 48/48, including the combined warning regression. Git denied both the worktree index lock and commit metadata, so changes remain uncommitted. Run acceptance and make the requested commits in an environment with access.
Touches: `STATE.md`, `HANDOFF.md`.

## Task 14 warning test and acceptance retry - 12 November 2026 (Codex)

Updated the combined warning assertion for the lower-case clause; 53 focused rescue/retry tests passed. The exact acceptance command failed at locked restore, while single-worker locked restore and Release build passed; format check was denied named-pipe access. Git denied the worktree index lock, so changes remain uncommitted.
Touches: `tests/Ferry.Engine.Tests/RescueReaderTests.cs`, `STATE.md`, `HANDOFF.md`.

## Task 14 review follow-up - 12 November 2026 (Codex)

Lower-cased the combined rescue and metadata warning. Acceptance again failed at locked restore; single-worker locked restore and Release solution build passed, but format check was denied named-pipe access. Git could not create the worktree index lock, so no commits were possible. The task card is outside this worktree.
Touches: `src/Ferry.Engine/FileCopier.cs`, `STATE.md`, `HANDOFF.md`.
## Task 14 acceptance retry - 12 November 2026 (Codex)

The exact acceptance command failed at locked restore. Single-worker locked restore and Release solution build succeeded; format check was denied named-pipe access. Git denied the index lock and HEAD lock, so commits remain blocked. No implementation changes this session.
Touches: `STATE.md`, `HANDOFF.md`.

## Task 14 review fixes; acceptance blocked — 12 November 2026 (Codex)

Removed unconditional hashing; a reopened writer triggers an on-demand comparison including zero-filled lost ranges. Combined metadata and rescue warnings and used the copying message constant. Added regression tests for both cases.
Focused rescue/retry tests: 53 passed, none skipped. Release engine/test project build: 0 warnings or errors. Starting acceptance failed at locked restore; the original partial RED was recorded as 42/54 failing. Git staging is denied because shared worktree metadata is outside the writable workspace; no commits were possible.
Resume recopies rescued files, and an existing lost report causes a report-save warning. Run full acceptance in a permitted environment and commit, ending with `feat(ferry): rescue mode and network retry`.
Touches: `src/Ferry.Engine/FileCopier.cs`, `src/Ferry.Engine/TreeJob.cs`, `tests/Ferry.Engine.Tests/RescueReaderTests.cs`, `docs/security/threat-model.md`, `STATE.md`, `HANDOFF.md`.
## Task 14 implemented; acceptance and commits blocked — 12 November 2026 (Codex)

Integrated rescue, the configurable lost-sector cap and lost reports; network/sharing retries reopen handles and verify after a write reconnect. Rescued moves keep their sources and never record verified completion. Preserved the partial code's saved-with-warning outcome convention; shortened network status to the card's exact wording.
Focused tests: 60 passed, none skipped; Release build clean. Full direct engine tests: 243 passed, 2 access-denied failures, 1 ReFS skip; Core: 410 passed, 12 access-denied failures; CLI: 1 passed. Full acceptance still fails at locked restore; format/test launchers also face pipe access denial. Whitespace and diff checks pass.
Git staging and commit failed because shared-worktree metadata is outside the writable sandbox. Everything remains on this branch, uncommitted. Reviewer must rerun acceptance and make the requested Conventional Commits, final subject `feat(ferry): rescue mode and network retry`.
Security review covered reopened-path containment, lost-write verification, report collisions, bounded retries and move-source retention; threat model updated. No task 15–17 changes.
Touches: `src/Ferry.Engine/`, rescue/retry and option tests, `docs/security/threat-model.md`, `STATE.md`, `HANDOFF.md`.

## Task 14 remains incomplete — 12 November 2026 (Codex)

The acceptance command again failed at locked restore without diagnostics; single-process restore without NuGet audit succeeded. A direct solution test run encountered sandbox denial connecting to the test platform's named pipes. Running the engine test executable directly works and confirms `Copy_SourceBrieflyInUse_WaitsAndCompletes` fails. No rescue/retry integration was completed in this session.
Touches: `STATE.md`, `HANDOFF.md`.

## Task 14 remains incomplete — 12 November 2026 (Codex, prior session)

The rescue and retry integration remains to be implemented. Removed the duplicate source-change exception so the reader and copier share it. The required build failed at locked restore; see `STATE.md` for the local restore and build results.
Touches: `src/Ferry.Engine/FileCopier.cs`, `STATE.md`, `HANDOFF.md`.

## Agent setup aligned to agent-ops — 8 November 2026 (Claude)

Codex had no instructions in this repo (only `CLAUDE.md` existed). Shared rules now live in `AGENTS.md` (Codex reads it; Claude imports it). `STATE.md` is the start-of-session read and points into the spec, plan and progress log rather than copying them. `comprehensive-review` plugin enabled for this repo only (its security-auditor agent does the milestone security reviews; it's disabled globally).
Touches: `AGENTS.md`, `CLAUDE.md`, `STATE.md`, `HANDOFF.md`, `.claude/settings.json`, `.agents/`. No code.
