# EveryApp — agent instructions (Codex, Claude Code, any agent)

**Session start: read [`STATE.md`](STATE.md). That's the whole mandatory read.** Then use the loading policy below.
Tiers and models: `agent-ops/MODELS.md`; routing and task cards: `agent-ops/OPERATING-MODEL.md` (both in the owner's workspace, outside this repo).

A suite of small, stable, lightweight, secure Windows 11 utilities (C# / .NET 10). App names:
- **Ferry** (Copy & Sync), **Hullwatch** (Drive Health), **Stowage** (Organise): v1
- **Broadside** (vulnerability scanner): first after v1
- then **Hold** (Space), **Bosun** (System), **Hawser** (Connect), **Chartroom** (Dev), **Gunport** (exposure auditor), **Powderkeg** (secrets finder)

"Ferry" means the copy app only, never the suite. Design files named `*-ferry-*` are Ferry's screens.

## Loading policy

| Read | When | How much |
|---|---|---|
| `STATE.md` | Every session start | All |
| `docs/superpowers/specs/2026-11-06-ferry-suite-v1-design.md` | Scope or architecture questions; §14 on **every** task (security) | The relevant section |
| `docs/superpowers/plans/2026-11-06-everyapp-m1-foundation-and-ferry-engine.md` | Working an M1 task | That task's section only |
| `docs/security/threat-model.md` | Change touches a trust boundary (below), or a milestone review | Relevant rows |
| `docs/adr/` | Changing something an ADR decided | The named ADR |
| `docs/design/` | Any UI task | Only the files for the screens you touch (newest per screen wins) |
| `.superpowers/sdd/<plan>/progress.md` (local, git-ignored) | Resuming plan execution | The tail: latest task lines and rulings |

The spec is the source of truth for scope and architecture.

## Designs: check before any UI work

- All UI designs live in `docs/design/` (PNG screens, PDF, HTML). They come from Claude Design runs.
- **Before any UI task, list `docs/design/` and look at the files for the screens you're touching.** The newest file for a screen wins. Files are named `YYYY-MM-DD-<app>-<screen>-<variant>-<light|dark>.<ext>`.
- Designs override the spec's text and the early mockups (`copy-window-layout-mockups.html`) on look, layout and wording. The spec wins on behaviour, scope and architecture. If a design conflicts with the spec on behaviour, **ask the user before building**.
- Tooltip wording: `docs/design/*tooltip-copy-sheet*` is the reference for tone. All tooltip text goes in one resource file.
- You may update or add designs when needed (e.g. a screen the designs don't cover). Save them to `docs/design/` with the same naming, never overwrite an existing file (use `-v2`), and say so in the commit message.
- Don't read every design file up front (each image costs context). Open only the ones relevant to the current task.

## Working rules

- Stability and speed are the top priorities. Never trade either for features.
- Plain, simple language in every user-facing string. No jargon.
- Destructive actions: preview → explicit confirmation → undo where possible.
- DevOps: everything builds, tests and lints via `build.ps1` exactly as CI does. Conventional Commits. Short-lived branches + PRs into `main`.
- Public repo: no secrets, personal paths, drive letters or machine names in committed code or tests. Local-only config goes in git-ignored files with a committed `.example`. `local/` is git-ignored and holds the user's personal scripts. Never commit it.

## Security (user requirement: as secure as possible; they don't want the details)

- Follow spec §14 on every task. It covers threat model, least privilege, untrusted input, path safety, signed updates, DPAPI for secrets, and supply chain.
- Security-review any change that touches a trust boundary: helper service, named pipes, Explorer extension, elevation, updater, downloads/feeds, credential storage, file delete/move logic. (Claude: `/security-review`. Codex: a review pass against `docs/security/threat-model.md`.)
- Run a security-auditor review against `docs/security/threat-model.md` at each milestone. Fix findings or record them as accepted risks with a reason.
- Keep the threat model current when components or trust boundaries change.
- Cannon apps (Broadside, Gunport, Powderkeg) are defensive only: inspect and harden this PC, never scan or attack other machines.
- Don't explain security internals to the user unless they ask. Just do it properly and flag any decision that needs them.

## Closing out

Run `/handoff` (or by hand): replace the ▶ START HERE block in `STATE.md`, add a short entry at the top of `HANDOFF.md` with a `Touches:` line. Keep plan-execution detail in the superpowers progress file, not in `HANDOFF.md`.
