@AGENTS.md

# Claude-specific notes

- Plan execution uses superpowers (brainstorm → plan → subagent-driven development → review). Its progress log is `.superpowers/sdd/<plan>/progress.md` (git-ignored, local).
- **Keep implementer subagents small:** one task (or one fix round) per fresh implementer. Don't resume an implementer whose context is large — dispatch a fresh one with the brief and findings (see ruling R33 in the progress log).
- Trust-boundary changes: `/security-review`. Milestone security review: the `comprehensive-review:security-auditor` agent (plugin enabled for this repo only).
