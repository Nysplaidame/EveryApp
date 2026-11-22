# EveryApp — HANDOFF (recent entries only)

Newest at the top. Each entry: `## <what> — <date> (<agent>)`, ≤10 lines, ending with a `Touches:` line.
Keep under ~300 lines; move older entries to `docs/archive/HANDOFF-history-<from>-<to>.md` in the same commit.
Live state belongs in `STATE.md`; plan-execution detail in the superpowers progress log; decisions in `docs/adr/`.

## Task 16 measured: speed gate not met — 22 November 2026 (Claude)

Card 007 re-run twice (stale base, then Codex sandbox with no NuGet TLS or disk access), then done in-session. Built `bench/Ferry.Bench` (micro-benchmarks, data generator, tuning sweep, standby-cache flush) and `bench.ps1` (FastCopy vs `ferry copy`, alternating). Profiling found two small-file costs: the per-file flush (~80%) and a 5–7 ms antivirus wait when verify reopened a just-written file. User chose "skip the flush, recheck on resume". Verify now reads small files back through the open unbuffered handle; part files lost write-through. Security-auditor findings M1, M2, L1–L3 fixed and tested. Results (NVMe only, HDD offline): large verify off 0.52× FastCopy; large verify on 1.17×; mixed 2.02× / 7.61×. Gate not met; the options are in `docs/perf/results.md`, pending the user. Large-set sweep skipped to spare the drives; defaults unchanged. Full `./build.ps1`: 729 tests, 728 passed, 1 skipped.
Touches: `bench/`, `bench.ps1`, `bench.example.json`, `src/Ferry.Engine/{FileCopier,TreeJob,ResumeRecovery,CopyProgress,JobOutcome,RetryHandles,CopierTuning}.cs`, `src/EveryApp.Core/{Jobs/JobJournal,FileSystem/*}.cs`, tests, `build.ps1`, `.github/workflows/release.yml`, `docs/security/threat-model.md`, `docs/perf/results.md`, `STATE.md`.

## Cards 004 and 006 merged; card 007 held — 18 November 2026 (Claude)

Card 006 (task 15, `ferry` CLI by Codex) reviewed and merged: strict option allow-list, response files off, scrubbed output, previews + confirmation for moves/replacements, Ctrl+C and kill/resume tests. Codex's four notes were blocked retries (no NuGet, sandbox pipes); it hand-wrote the System.CommandLine 2.0.2 lock entries, which the locked restore here validated. Fix-ups: `--rescue` enabled (written before task 14), `--if-unreadable ask` still refused (engine has no prompt until M2), `ferry verify` compares sizes before hashing; tests updated (rescue copy, same-size-different-bytes). Card 004's M2 Ctrl+V risk note merged. Card 007 (task 16) held: no harness or measurements, seven "blocked" notes, stale premises. Full `./build.ps1`: 703 tests, 702 passed, 1 skipped (ReFS).
Touches: `src/Ferry.Cli/FolderVerifier.cs`, `src/Ferry.Cli/CliApplication.cs`, `tests/Ferry.Cli.Tests/CliTests.cs`, `docs/security/threat-model.md`, `STATE.md`, `HANDOFF.md`.

## Task 14 accepted and merged — 18 November 2026 (Claude)

Card 002's branch (`agent/002-task14-rescue`, Codex/Qwen work, last `838732e`) passed the full `./build.ps1` run here: locked restore, format check, build with 0 warnings, 671 tests (670 passed, 1 skipped: no ReFS drive). Codex's eight 12 Nov entries were all "acceptance blocked by sandbox" (named pipes, git locks); condensed here. Their substance: rescue bisects bad blocks down to sectors (2 retries, zero-fill, per-file cap `MaxLostSectors`), writes `<dest>.ferry-lost.txt`, and rescued files count as warnings, never "verified"; rescued moves keep the source. Network/sharing retries back off from 1 s up to 30 s (5 min budget), then reopen handles. A reopened source must match its file ID, size and timestamp. A reopened part file must resolve to the same final path, with no links followed. Any reopened write is hash-checked even with Verify off. Known limits: resume recopies rescued files, and an existing lost report gives a report-not-saved warning. Claude reviewed the code and found no issues. Threat model updated by Codex.
Touches: `src/Ferry.Engine/`, `src/EveryApp.Core/FileSystem/`, tests, `docs/security/threat-model.md`, `STATE.md`, `HANDOFF.md`.

## Explorer tab paste bug = Ultracopier — 11 November 2026 (Claude)

Ctrl+V pasted into the left-most tab of a multi-tab Explorer window. PasteCatcher's live log showed it matched the right tab and passed the key through; the misroute also happened with PasteCatcher off, and only on Ctrl+V (right-click paste fine). Quitting Ultracopier (startup item, takes over paste) fixed it. Speculative PasteCatcher hardening was reverted (it did not fix anything). Added a note to MANUAL-TEST step 11. Open: Ferry's M2 paste hook will compete with such tools; not yet in the spec.
Touches: `spikes/explorer-interception/MANUAL-TEST.md` (commit `7f1bb7f`), `STATE.md`, `HANDOFF.md`. No code.

## Agent setup aligned to agent-ops — 8 November 2026 (Claude)

Codex had no instructions in this repo (only `CLAUDE.md` existed). Shared rules now live in `AGENTS.md` (Codex reads it; Claude imports it). `STATE.md` is the start-of-session read and points into the spec, plan and progress log rather than copying them. `comprehensive-review` plugin enabled for this repo only (its security-auditor agent does the milestone security reviews; it's disabled globally).
Touches: `AGENTS.md`, `CLAUDE.md`, `STATE.md`, `HANDOFF.md`, `.claude/settings.json`, `.agents/`. No code.
