# EveryApp threat model

Method: STRIDE per component (Spoofing, Tampering, Repudiation, Information disclosure, Denial of service, Elevation of privilege). Source of requirements: spec section 14 (`docs/superpowers/specs/2026-11-06-ferry-suite-v1-design.md`).

Status: M1 components (Core, Engine, CLI) are modelled in full. All other components are marked "designed in M2+": the mitigations from spec section 14 are recorded here as binding requirements and must be refined into threats and tests when the component is built.

Keep this file current whenever a component or trust boundary changes. Each milestone gets a security-auditor review against it. Findings are fixed or recorded under "Accepted risks".

## Trust boundaries

| ID | Boundary | Untrusted side | Notes |
|----|----------|----------------|-------|
| TB1 | User and file system | File and folder names, paths, file contents, attributes, reparse points, ADS | Every path and name is hostile input. Other local users and processes can change the tree while we work (TOCTOU). |
| TB2 | CLI arguments and environment | Argument strings, response files, environment | Treated as input from a possibly less-trusted caller (scripts, shortcuts). |
| TB3 | Journal and job files on disk | Journal contents, job files, settings files | A file the user can edit, another process can corrupt, or an attacker could plant. Parsed defensively. |
| TB4 | App and helper service (named pipes) | Pipe messages, pipe clients | Designed in M2+. |
| TB5 | Explorer process and helper | Anything Explorer hands over | Designed in M2+. |
| TB6 | Non-elevated and elevated | Any request that asks for admin work | Designed in M2+. |
| TB7 | Network (updates, feeds, alert services) | Everything received, plus the transport | Designed in M2+. |
| TB8 | Build and release pipeline | Dependencies, GitHub Actions, contributors | Covered by the supply chain controls below. |

## M1 components

### Ferry.Core (shared library: paths, journal format, results, logging)

| STRIDE | Threat | Mitigation |
|--------|--------|------------|
| S | A path is crafted to look like it is inside the selected tree but resolves elsewhere (traversal, `..`, alternate separators, 8.3 names, `\\?\` and device prefixes, trailing dots and spaces). | One path-normalisation and containment check in Core, used by every caller. Rejects device paths, reserved names and anything that does not stay under the root after full resolution. Property and unit tests cover the cases. |
| T | A journal or job file is edited or truncated so that a resume acts on false state. | Journal records carry a version and an integrity check. A bad or unknown record stops the resume and asks the user. It never guesses. Writes are append-only and flushed at defined points. |
| T | Time-of-check to time-of-use swap: a directory is replaced by a junction after validation. | Containment is checked at the moment of use on an opened handle, not only up front. See Engine. |
| R | A destructive action happens and nobody can tell what or why. | Journal records every move, overwrite and delete with the source, destination and outcome. |
| I | Logs or errors leak personal paths, user names, machine names or secrets. | Logging goes through one scrubber that masks the user profile path and known secret shapes. Secrets never go into messages, journals or settings. Exception messages shown to the user are plain and path-free where possible. |
| D | A huge or deeply nested path list, a very long path or a hostile journal exhausts memory or time. | Size, depth and length limits on every parser. Streaming reads. Limits are tested. |
| E | A parser bug lets a crafted file run code. | No dynamic code loading, no reflection-based deserialisation of untrusted data, no BinaryFormatter. Parsers are fuzz-tested (journal format first). |

### Ferry.Engine (copy, move and verify engine)

| STRIDE | Threat | Mitigation |
|--------|--------|------------|
| S | A destination or source that is a symlink, junction or mount point redirects writes outside the intended tree. | Reparse points are detected with file attributes and are never followed out of the selected tree unless the user explicitly asked. Default is to copy the link itself or skip it, reported in the preview. |
| T | TOCTOU between check and copy: a file or directory is swapped while the job runs. | Open with handle-based checks, re-verify identity (volume serial and file ID) after opening, and open with no-follow semantics for the final component. Writes go to a temp name in the destination and are renamed on success. |
| T | Overwrite or move destroys the only good copy if the job is interrupted. | Preview, then explicit confirmation. Move is copy, verify, then delete the source. Undo information is journalled. Interrupted jobs resume from the journal. |
| T | Silent corruption in transit. | Optional verification pass (hash compare). Failures are reported, and the source is never deleted after a failed verify. |
| R | Disputed outcome of a job. | Per-file result in the journal and the final report. |
| I | Copying leaks data via wrong permissions (new files more open than the source, or the destination readable by others). | New files inherit the destination ACL by default. Copying permissions is opt-in and preserves them exactly. ADS and attributes are preserved only when opted in. |
| D | A tree with cycles, enormous file counts, sparse or special files, or a full disk hangs or fills the drive. | Cycle detection through reparse handling. Free-space check up front. Cancellation honoured at every stage. Temp files are cleaned up on failure. Special device files are skipped. |
| E | Engine run by a privileged caller writes where the unprivileged user could not. | The engine itself never elevates. Elevated work (M2+) uses a per-job worker that revalidates every path against the requesting user's access. |

### Ferry.Cli (command-line front end)

| STRIDE | Threat | Mitigation |
|--------|--------|------------|
| S | A script passes arguments that impersonate another operation, such as a delete flag appended through string concatenation. | Strict argument parser with an allow-list of options. Unknown options are errors. No shell invocation, and no argument is ever passed to a shell. |
| T | Argument injection through file names that begin with `-` or contain wildcards. | `--` end-of-options handling. Names are never expanded as patterns unless requested. All paths go through Core normalisation. |
| R | No record of what a scripted run did. | The CLI uses the same journal as the apps. Exit codes are stable and documented. |
| I | Verbose output or crash dumps leak paths or secrets. | Output goes through the Core scrubber. Stack traces are shown only in an explicit debug mode. |
| D | Pathological arguments or response files cause runaway memory or time. | Limits on argument count, length and response-file size. |
| E | The CLI is run elevated by habit and acts with more rights than needed. | The CLI refuses destructive operations when elevated unless explicitly forced, and prints a plain warning. |

### Build and release pipeline (TB8)

| STRIDE | Threat | Mitigation |
|--------|--------|------------|
| S | A malicious or typosquatted dependency or action. | Central package management with lock files (`--locked-mode` in CI, and CI fails if a lock file is missing). Actions pinned by full commit SHA. Dependabot proposes updates for review. Each new dependency is justified in its PR. |
| T | A compromised workflow alters the build or pushes code. | Workflow tokens are read-only by default. Write scopes (`security-events`) only on the CodeQL job. `persist-credentials: false` on checkout. Branch protection on `main`: PR required, CI and CodeQL required, no force push or deletion. |
| I | Secrets committed to the public repo. | GitHub secret scanning and push protection. No secrets, personal paths or machine names in committed files. Local-only config is git-ignored. |
| D | Pipeline abuse for compute. | Timeouts on jobs. Concurrency groups cancel superseded runs. |
| E | A pull request from a fork runs with write access. | No `pull_request_target` triggers. Fork PRs get a read-only token. |

## Components designed in M2+

Each row is a binding requirement from spec section 14, to be expanded into full threats and tests when the component is built.

| Component | Status | Mitigations required (from spec section 14) |
|-----------|--------|---------------------------------------------|
| Apps (Ferry, Hullwatch, Stowage and later) | Designed in M2+ | Run non-elevated, never as admin "just in case". All inputs (file names, paths, contents, SMART data, event-log text, feeds, vendor pages, SSH servers) are untrusted and parsed defensively. Destructive actions need a preview and confirmation. Risky features ship off by default behind feature flags. Any network listener binds to localhost unless the user explicitly widens it. Logs are scrubbed of secrets and personal paths. |
| Helper service (SYSTEM) | Designed in M2+ | The only resident, privileged piece. Minimal, versioned, allow-listed command set. Every request is authenticated and validated. No generic "run this" command. Idle budget of about 0% CPU and 40 MB RAM total. |
| Named pipes (apps and helper) | Designed in M2+ | ACLs restricted to the interactive user and SYSTEM. Authenticated, versioned protocol. Pipe messages are untrusted and the protocol parser is fuzz-tested. Reject remote clients. |
| Explorer extension (C++) | Designed in M2+ | Minimal native code, with every mitigation on: CFG, /GS, ASLR and high-entropy, CET shadow stack compatible, Spectre mitigations. No parsing of untrusted data in-process. It hands the operation to the helper over a pipe and returns. Falls back to the stock Windows copy if the helper is unreachable or anything fails. Reviewed line by line. |
| Elevated copy worker | Designed in M2+ | Per-job and short-lived. Revalidates paths. Holds admin rights only for the duration of the job. Same path-safety rules as the Engine. |
| Updater | Designed in M2+ | Packages are signature-checked against the pinned certificate before install. Delta updates are verified the same way. Nothing downloaded is executed without the signature and hash checks the source supports, plus user confirmation. |
| Alert integrations (ntfy, Home Assistant, MQTT, others) | Designed in M2+ | Tokens and credentials stored with DPAPI or Credential Manager, never in plain settings files or logs. TLS for all transport. Alert payloads are scrubbed of personal paths and secrets. Outbound only. |
| Feed downloads (vulnerability feeds, vendor pages) | Designed in M2+ | Downloaded over TLS with integrity checks where the source provides them. Feed files are parsed defensively and fuzz-tested. Vendor-page scraping never auto-installs anything: it only informs. |

## Accepted risks

None recorded yet.

## Review log

| Date | Scope | Result |
|------|-------|--------|
| 2026-11-06 | Initial model (Task 2 of M1) | Written. Awaiting the M1 security-auditor review. |
