# EveryApp threat model

Method: STRIDE per component (Spoofing, Tampering, Repudiation, Information disclosure, Denial of service, Elevation of privilege). Source of requirements: spec section 14 (`docs/superpowers/specs/2026-11-06-ferry-suite-v1-design.md`).

Status: M1 components (Core, Engine, CLI) are modelled in full. All other components are marked "designed in M2+": the mitigations from spec section 14 are recorded here as binding requirements and must be refined into threats and tests when the component is built.

Keep this file current whenever a component or trust boundary changes. Each milestone gets a security-auditor review against it. Findings are fixed or recorded under "Accepted risks".

## Trust boundaries

| ID | Boundary | Untrusted side | Notes |
|----|----------|----------------|-------|
| TB1 | User and file system | File and folder names, paths, file contents, attributes, reparse points, ADS | Every path and name is hostile input. Other local users and processes can change the tree while we work (TOCTOU). |
| TB2 | CLI arguments and environment | Argument strings, response files, environment | Treated as input from a possibly less-trusted caller (scripts, shortcuts). |
| TB3 | Journal and job files on disk | Journal contents, job files, settings files | A file the user can edit, another process can corrupt, or an attacker could plant. Parsed defensively. The job spec (sources, destination, mode, options) is untrusted: resume needs explicit confirmation (CLI prompt or `--yes`). |
| TB4 | App and helper service (named pipes) | Pipe messages, pipe clients | Designed in M2+. |
| TB5 | Explorer process and helper | Anything Explorer hands over | Designed in M2+. |
| TB6 | Non-elevated and elevated | Any request that asks for admin work | Designed in M2+. The M1 engine refuses to run elevated (TokenElevation check) and no message may advise running as administrator: a path-based copier running elevated over user-controlled paths is a planted-link privilege-escalation risk. Elevated work belongs to the M2 helper. |
| TB7 | Network (updates, feeds, alert services) | Everything received, plus the transport | Designed in M2+. |
| TB8 | Build and release pipeline | Dependencies, GitHub Actions, contributors | Covered by the supply chain controls below. |

## M1 components

### Ferry.Core (shared library: paths, journal format, results, logging)

| STRIDE | Threat | Mitigation |
|--------|--------|------------|
| S | A path is crafted to look like it is inside the selected tree but resolves elsewhere (traversal, `..`, alternate separators, 8.3 names, `\\?\` and device prefixes, trailing dots and spaces). | One path-normalisation and containment check in Core, used by every caller. Rejects device paths, reserved names and anything that does not stay under the root after full resolution. Property and unit tests cover the cases. |
| T | A journal or job file is edited or truncated so that a resume acts on false state. | Every journal line carries a CRC; reading stops at the first bad, unknown or rule-breaking line and the file is truncated there (only the good lines before it are trusted). It never guesses. Each record kind is checked by one validator shared by append and load (safe relative paths only; a part record names its part and its target, in the same folder). At every use site a journaled path is joined to the root and must still resolve inside it, and a part file must have Ferry's own name shape (`name.<8 hex>.ferrypart`); anything else is ignored as corruption. The job spec in the header is untrusted: it is validated, and resume runs only after the user confirms its sources, destination and mode. Writes are append-only and flushed at defined points; part-file records reach the OS before the part exists. |
| T | Time-of-check to time-of-use swap: a directory is replaced by a junction after validation. | Containment is checked at the moment of use on an opened handle, not only up front: a part file's final path (GetFinalPathNameByHandle) must be under the destination root's final path, and verified deletes compare id, volume, size, time and final path on the same handle that deletes. See Engine. |
| R | A destructive action happens and nobody can tell what or why. | The journal records each file done, skipped or failed and every part file. Overwrites and move-source deletes are not yet journaled as undo records and are not undoable in M1 (planned with the UI's undo in M2); the report lists every problem. |
| I | Logs or errors leak personal paths, user names, machine names or secrets. | Logging goes through one scrubber that masks the user profile path and known secret shapes. Secrets never go into messages, journals or settings. Exception messages shown to the user are plain and path-free where possible. |
| D | A huge or deeply nested path list, a very long path or a hostile journal exhausts memory or time. | Size, depth and length limits on every parser (journal lines are capped at 64 KB). Streaming reads. Limits are tested. A job refuses to start when the journal's drive has under 50 MB free, so the journal cannot fill a disk unnoticed. |
| E | A parser bug lets a crafted file run code. | No dynamic code loading, no reflection-based deserialisation of untrusted data, no BinaryFormatter. Parsers are fuzz-tested (journal format first). |

### Ferry.Engine (copy, move and verify engine)

| STRIDE | Threat | Mitigation |
|--------|--------|------------|
| S | A destination or source that is a symlink, junction or mount point redirects writes outside the intended tree. | Links (name-surrogate reparse points) to folders and to files are not followed unless the user asked; by default they are skipped and listed when copying, and a move never follows them (same volume: the link itself is renamed; another volume: left in place and listed). An existing destination folder that is a link is refused ("a shortcut to somewhere else") and nothing is written through it. Every part file's final path is checked through its own handle and, if it left the destination, deleted through that handle. Cloud and dedup reparse files are ordinary data. |
| T | TOCTOU between check and copy: a file or directory is swapped while the job runs. | Writes go to a random part name created with CREATE_NEW and delete access; its final path is checked on its handle before data is written. A move deletes its source only through `DeleteVerified`: opened without following links, file id, volume, size and last-write time must match what was copied, and its final path must be under the source root; deletion is by disposition on that handle (POSIX semantics where supported). Emptied source folders go only if empty and still the walked folder (same check). Same-volume moves are pure renames (MoveFileEx without COPY_ALLOWED); "same volume" is the volume GUID, and if Windows refuses the rename as crossing volumes the file is copied, verified, then deleted as above. |
| T | Overwrite or move destroys the only good copy if the job is interrupted. | Preview, then explicit confirmation. Move is copy, verify, then delete the source. Undo information is journalled. Interrupted jobs resume from the journal. |
| T | Silent corruption in transit. | Optional verification pass (hash compare). Failures are reported, and the source is never deleted after a failed verify. |
| R | Disputed outcome of a job. | Per-file result in the journal and the final report. |
| I | Copying leaks data via wrong permissions (new files more open than the source, or the destination readable by others), or loses the "downloaded from the internet" marker. | New files inherit the destination ACL by default. Copying permissions is opt-in, only between this PC's fixed drives, and filtered: entries for unknown or foreign accounts, OWNER RIGHTS, and every allow entry that gives a broad group (Everyone, Users, Authenticated Users, Anonymous, INTERACTIVE, Guests, or Domain Users, any domain's RID 513) any write or change right is dropped. That includes the generic rights GENERIC_ALL, GENERIC_WRITE and MAXIMUM_ALLOWED, which inherit-only folder entries often use (they are tested on the raw access mask, so they cannot slip past the specific-rights check); owner and list are written in one call; if the result would lock the user out it is reverted to inherited permissions, access is checked again after the revert, and if the user is still locked out (the folder itself passes down a deny) a plain warning is given. A same-volume move is a rename, so the item keeps its source ACL (as Explorer does). The mark-of-the-web stream (`Zone.Identifier`) is always copied; on a move, failing to copy it keeps the source. FAT and exFAT destinations, which cannot hold it, get one pre-flight warning. Other streams are opt-in, and only `:name:$DATA` names are accepted. |
| D | A tree with cycles, enormous file counts, sparse or special files, or a full disk hangs or fills the drive. | Cycle detection through reparse handling. Free-space check up front (destination and the journal's drive). Cancellation honoured at every stage, including stream copies. Temp files are cleaned up on failure. Special device files are skipped. Planning and copying are linear in the number of items (per-folder counts computed once, set lookups for skipped folders); a test plans 20,000 folders with 100,000 files in well under 2 seconds. The problem list keeps the first 10,000 entries and counts the rest. |
| E | Engine run by a privileged caller writes where the unprivileged user could not. | The engine itself never elevates and refuses to run elevated; the check it uses cannot be replaced through the public constructor. Elevated work (M2+) uses a per-job worker that revalidates every path against the requesting user's access. |

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
| S | A malicious or typosquatted dependency or action. | Central package management with lock files (`--locked-mode` in CI, and CI fails if a lock file is missing). Actions pinned by full commit SHA. Dependabot proposes updates for review. Each new dependency is justified in its PR. Every release ships a CycloneDX SBOM so the dependency set can be audited after the fact. |
| S | A release binary is replaced or forged after the build. | Release binaries are Authenticode-signed (SHA-256, timestamped) and `build.ps1 -Sign` fails unless every signature is `Valid`. The release zip carries a GitHub build provenance attestation (`actions/attest-build-provenance`) that ties it to the workflow run and commit. The signing key lives only in the `release` environment's secrets. It is exposed to a single workflow step (step-level `env`) that runs only `build.ps1 -SignOnly`: restore, build, test, publish, SBOM and every third-party action run without it. In that step the PFX is imported into the runner's user store and the public certificate is added to the runner's machine Root store so the strict `Valid` check passes. Both certificates and the PFX file are removed in a `finally` block. The runner is ephemeral and the machine-wide trust is refused outside GitHub Actions. |
| T | A compromised workflow alters the build or pushes code. | Workflow tokens are read-only by default. Write scopes are granted per job only where needed: `security-events` on the CodeQL job, and on the release job `contents`, `id-token` and `attestations` (write). `persist-credentials: false` on checkout. Branch protection on `main`: PR required, CI and CodeQL required, no force push or deletion. |
| E | Anyone with push access creates a `v*` tag on an unreviewed commit or branch and gets a signed release. | The release job first requires the tagged commit to be an ancestor of `origin/main` (reviewed PR path). The signing secrets sit in the `release` environment, whose deployment rule only admits `v*` tags. A tag ruleset on `v*` blocks deletion, update and non-fast-forward (no re-pointing a released tag). |
| S | The self-signed signing key leaks (repository secret, developer PC, PFX file), so an attacker can sign malware that this PC's users trust. | The PFX is password-protected (AES256), git-ignored, and secrets are only set by the user, never by script. Trust is limited to the current user on the dev PC. Recovery: run `New-SigningCert.ps1 -Rotate` (creates the new certificate first, removes the old from all stores last), update the local thumbprint and the `release` secrets, and publish a notice. Updaters (M2+) pin the certificate, so a rotation ships as a signed update from the new key or a manual reinstall. |
| I | Secrets committed to the public repo. | GitHub secret scanning and push protection. No secrets, personal paths or machine names in committed files. Local-only config is git-ignored. |
| D | Pipeline abuse for compute. | Timeouts on jobs. CI uses a concurrency group that cancels superseded runs; the release workflow uses a per-tag group that never cancels a running release. |
| E | A pull request from a fork runs with write access. | No `pull_request_target` triggers. Fork PRs get a read-only token. |

## Components designed in M2+

Each row is a binding requirement from spec section 14, to be expanded into full threats and tests when the component is built.

| Component | Status | Mitigations required (from spec section 14) |
|-----------|--------|---------------------------------------------|
| Apps (Ferry, Hullwatch, Stowage and later) | Designed in M2+ | Run non-elevated, never as admin "just in case". All inputs (file names, paths, contents, SMART data, event-log text, feeds, vendor pages, SSH servers, the hosts file, `pyvenv.cfg`) are untrusted and parsed defensively. The hosts-file and `pyvenv.cfg` parsers are fuzz-tested. Destructive actions need a preview and confirmation. Risky features ship off by default behind feature flags. Any network listener binds to localhost unless the user explicitly widens it. Logs are scrubbed of secrets and personal paths. |
| Helper service (SYSTEM) | Designed in M2+ | The only resident, privileged piece. Minimal, versioned, allow-listed command set. Every request is authenticated and validated. No generic "run this" command. Idle budget of about 0% CPU and 40 MB RAM total. |
| Named pipes (apps and helper) | Designed in M2+ | ACLs restricted to the interactive user and SYSTEM. Authenticated, versioned protocol. Pipe messages are untrusted and the protocol parser is fuzz-tested. Reject remote clients. |
| Explorer extension (C++) | Designed in M2+ | Minimal native code, with every mitigation on: CFG, /GS, ASLR and high-entropy, CET shadow stack compatible, Spectre mitigations. No parsing of untrusted data in-process. It hands the operation to the helper over a pipe and returns. Falls back to the stock Windows copy if the helper is unreachable or anything fails. Reviewed line by line. |
| Elevated copy worker | Designed in M2+ | Per-job and short-lived. Revalidates paths. Holds admin rights only for the duration of the job. Same path-safety rules as the Engine. |
| Updater | Designed in M2+ | Packages are signature-checked against the pinned certificate before install. Delta updates are verified the same way. Nothing downloaded is executed without the signature and hash checks the source supports, plus user confirmation. |
| Alert integrations (ntfy, Home Assistant, MQTT, others) | Designed in M2+ | Tokens and credentials stored with DPAPI or Credential Manager, never in plain settings files or logs. TLS for all transport. Alert payloads are scrubbed of personal paths and secrets. Outbound only. |
| Feed downloads (vulnerability feeds, vendor pages) | Designed in M2+ | Downloaded over TLS with integrity checks where the source provides them. Feed files, and local config files read during scans (hosts file, `pyvenv.cfg`), are parsed defensively and fuzz-tested. Vendor-page scraping never auto-installs anything: it only informs. |

## Accepted risks

| Risk | Why accepted |
|------|--------------|
| A process running as the same user swaps files or folders while a job runs (for example after the final-path check of a part file, or between a source's verify and its delete-by-handle mismatch check). | Same-user processes already have every right the job has; the handle-based checks stop redirection to places the user could not reach anyway, and verified deletes never remove a file that changed. |
| Part files are deleted by name in a few places: a failed copy's own part, and on resume the part names the journal recorded. | The names are random (`.<8 hex>.ferrypart`), created with CREATE_NEW by this job, checked to be inside the destination and of Ferry's shape; a collision with a user file is not realistic. Covered: on resume, a journaled part is deleted through a handle opened without following links, and only if its final path is under the destination's final path (a link in the destination or in the journaled path makes it skipped with a plain problem); a recovered target gets the same final-path check before its metadata or permissions are written. Not covered: a failed copy's own part is cleaned up by path (its name is fresh and random), and a same-user process that swaps a link in between the check and the delete (see the first row). The part-name shape and the journal's own folder-match rule are still the only guard against a user file with a name of that exact shape. |
| Block cloning (ReFS) creates the clone by path and the final-path check of a cloned part is done by path, not through a handle. | ReFS-only, same volume; the clone target is a fresh random part name; any failure falls back to the handle-checked copy. |
| Overwrites and move-source deletes are not undoable in M1. | Overwrite never deletes the old file before the new one is complete and verified; undo records come with the M2 UI. |

## Review log

| Date | Scope | Result |
|------|-------|--------|
| 2026-11-06 | Initial model (Task 2 of M1) | Written. Awaiting the M1 security-auditor review. |
| 2026-11-07 | Ferry tree copier security review (Task 13, fix round 2) | Findings F1 to F9: elevation advice and elevated runs, destination links, path-based deletes, serial-based same-volume check with copy-allowed renames, file links followed, mark-of-the-web, permission copying limits, tampered journal and spec, hostile-tree cost. All fixed (see Core and Engine rows, TB3, TB6, Accepted risks). |
| 2026-11-09 | Ferry tree copier security re-review (Task 13, fix round 3) | Findings N1 to N4. N1: generic rights (GENERIC_ALL, GENERIC_WRITE, MAXIMUM_ALLOWED) slipped past the permission filter; now treated as write rights. N2: resume deletes of journaled parts and writes to recovered targets followed links inside the destination; now handle-checked against the destination's final path. N3: the elevation check could be replaced through the public constructor; the seam is now internal. N4: no access re-check after the revert, and INTERACTIVE, Guests and Domain Users missing from the broad groups; both fixed. Accepted-risk row 2 narrowed to match. |
| 2026-11-06 | Signing and release pipeline (Task 3 of M1) | Review found: release could not pass the Valid check in CI, secrets exposed to all steps, tag trigger bypassed main protection, unsafe `-Rotate`. All fixed in fix round 1 (see rows above). Awaiting the M1 security-auditor review. |
