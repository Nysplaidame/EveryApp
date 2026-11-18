# EveryApp — HANDOFF (recent entries only)

Newest at the top. Each entry: `## <what> — <date> (<agent>)`, ≤10 lines, ending with a `Touches:` line.
Keep under ~300 lines; move older entries to `docs/archive/HANDOFF-history-<from>-<to>.md` in the same commit.
Live state belongs in `STATE.md`; plan-execution detail in the superpowers progress log; decisions in `docs/adr/`.

## Task 14 accepted and merged — 18 November 2026 (Claude)

Card 002's branch (`agent/002-task14-rescue`, Codex/Qwen work, last `838732e`) passed the full `./build.ps1` run here: locked restore, format check, build with 0 warnings, 671 tests (670 passed, 1 skipped: no ReFS drive). Codex's eight 12 Nov entries were all "acceptance blocked by sandbox" (named pipes, git locks); condensed here. Their substance: rescue bisects bad blocks down to sectors (2 retries, zero-fill, per-file cap `MaxLostSectors`), writes `<dest>.ferry-lost.txt`, and rescued files count as warnings, never "verified"; rescued moves keep the source. Network/sharing retries back off from 1 s up to 30 s (5 min budget), then reopen handles. A reopened source must match its file ID, size and timestamp. A reopened part file must resolve to the same final path, with no links followed. Any reopened write is hash-checked even with Verify off. Known limits: resume recopies rescued files, and an existing lost report gives a report-not-saved warning. Claude reviewed the code and found no issues. Threat model updated by Codex.
Touches: `src/Ferry.Engine/`, `src/EveryApp.Core/FileSystem/`, tests, `docs/security/threat-model.md`, `STATE.md`, `HANDOFF.md`.

## Explorer tab paste bug = Ultracopier — 11 November 2026 (Claude)

Ctrl+V pasted into the left-most tab of a multi-tab Explorer window. PasteCatcher's live log showed it matched the right tab and passed the key through; the misroute also happened with PasteCatcher off, and only on Ctrl+V (right-click paste fine). Quitting Ultracopier (startup item, takes over paste) fixed it. Speculative PasteCatcher hardening was reverted (it did not fix anything). Added a note to MANUAL-TEST step 11. Open: Ferry's M2 paste hook will compete with such tools; not yet in the spec.
Touches: `spikes/explorer-interception/MANUAL-TEST.md` (commit `7f1bb7f`), `STATE.md`, `HANDOFF.md`. No code.

## Agent setup aligned to agent-ops — 8 November 2026 (Claude)

Codex had no instructions in this repo (only `CLAUDE.md` existed). Shared rules now live in `AGENTS.md` (Codex reads it; Claude imports it). `STATE.md` is the start-of-session read and points into the spec, plan and progress log rather than copying them. `comprehensive-review` plugin enabled for this repo only (its security-auditor agent does the milestone security reviews; it's disabled globally).
Touches: `AGENTS.md`, `CLAUDE.md`, `STATE.md`, `HANDOFF.md`, `.claude/settings.json`, `.agents/`. No code.
