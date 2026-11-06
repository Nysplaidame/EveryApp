# EveryApp (working name "Ferry")

A suite of small, stable, lightweight Windows 11 utilities (Copy & Sync, Drive Health, Organise, then Space, System, Connect).
The source of truth for scope and architecture is `docs/superpowers/specs/2026-11-06-ferry-suite-v1-design.md`.

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
- Public repo: no secrets, personal paths, drive letters or machine names in committed code or tests. Local-only config goes in git-ignored files with a committed `.example`.
