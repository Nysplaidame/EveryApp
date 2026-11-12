# EveryApp — HANDOFF (recent entries only)

Newest at the top. Each entry: `## <what> — <date> (<agent>)`, ≤10 lines, ending with a `Touches:` line.
Keep under ~300 lines; move older entries to `docs/archive/HANDOFF-history-<from>-<to>.md` in the same commit.
Live state belongs in `STATE.md`; plan-execution detail in the superpowers progress log; decisions in `docs/adr/`.

## Card 006 CLI implementation - 12 November 2026 (Codex)

Implemented copy/resume/verify/jobs, JSON lines, confirmation previews, conflict prompts, rate/exclude options, graceful/forced cancellation and read-only history. Task 14 remains untouched: rescue and unreadable Ask explicitly return exit 4.
Added 32 tests beyond the existing smoke test; all 33 CLI tests passed, including real Ctrl+C cleanup and killing/resuming a 2,000-file copy without duplicates. Solution compiled with zero warnings using a temporary SDK System.CommandLine reference, removed before commit.
Exact acceptance still fails at locked restore; serial restore reports NU1301/NU1900 (NuGet unavailable), and format/test IPC is denied. Standalone Core: 410 pass, 12 permission failures; Engine: 190 pass, 2 ACL failures, 1 ReFS skip. Whitespace and diff checks pass.
System.CommandLine 2.0.2 is centrally pinned; lock checksum/dependency metadata taken from the installed SDK's official package manifest because restore is unavailable. Reviewer must validate with normal locked restore and `build.ps1`; Task 15 is not yet accepted. Boundary review recorded in the threat model; milestone audit/soak untouched.
Touches: `src/Ferry.Cli/`, `tests/Ferry.Cli.Tests/`, Core job journal summaries, Engine saved-job/hash APIs, central packages and locks, `docs/security/threat-model.md`, `STATE.md`, `HANDOFF.md`.

## Card 006 acceptance retry - 12 November 2026 (Codex)

The exact acceptance command still fails at locked restore without a diagnostic; single-process locked restore succeeds. This checkout contains only the CLI stub, so the reported later build failure could not be reproduced. Task 15 remains blocked as recorded below.
Touches: `STATE.md`, `HANDOFF.md`.

## Card 006 blocked - 12 November 2026 (Codex)

Task 15 was not implemented. Task 14's rescue mode and unreadable-file Ask are incomplete; System.CommandLine is not cached and NuGet was unreachable. The acceptance command failed at locked restore without a diagnostic, although single-process restore succeeded. Resume after the prerequisites and build environment are available.
Touches: `STATE.md`, `HANDOFF.md`.

## Agent setup aligned to agent-ops — 8 November 2026 (Claude)

Codex had no instructions in this repo (only `CLAUDE.md` existed). Shared rules now live in `AGENTS.md` (Codex reads it; Claude imports it). `STATE.md` is the start-of-session read and points into the spec, plan and progress log rather than copying them. `comprehensive-review` plugin enabled for this repo only (its security-auditor agent does the milestone security reviews; it's disabled globally).
Touches: `AGENTS.md`, `CLAUDE.md`, `STATE.md`, `HANDOFF.md`, `.claude/settings.json`, `.agents/`. No code.
