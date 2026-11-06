# Ferry Suite — Version 1 Design

*Date: 2026-11-06 · Status: draft for approval · "Ferry" is a working name*

## 1. Intent

A suite of small, fast, stable Windows 11 utilities for one power user's PC. It replaces a patchwork of third-party tools (Ultracopier, FastCopy, TeraCopy, CrystalDiskInfo, TreeSize, PuTTY, Patch My PC and others), several of which lock useful features behind paywalls, explain their options poorly, or have clunky interfaces.

It came out of a real problem: a copy from an old hard drive crawled at 3–4 MB/s, Ultracopier's pause was paywalled, an SSD was silently logging hundreds of bad-block errors with no warning, and the PC's debloated Windows build ("Windows X-Lite") had turned off maintenance, disk diagnostics and updates without saying so. The suite should catch, explain and fix this kind of thing.

What makes it worth building, rather than installing the existing tools:
- **One suite, one look, one engine.** The tools share a core (fast I/O, hashing, scanning, jobs, alerts, undo), one design language and one way of explaining things.
- **Every useful feature from the tools it replaces, free and switchable**, plus the features those tools never got right.
- **Plain language everywhere.** Any option can be understood from its tooltip alone.

## 2. Goals, non-goals, anti-goals

**Goals**
- Stable: never crash Explorer, never lose or corrupt data, recover cleanly from crashes, reboots and unplugged drives.
- Lightweight: tools start only when opened. Only a tiny background helper is always running.
- Fast: copying matches FastCopy on the same hardware.
- Simple but comprehensive UI: progress first, options one click away, nothing unexplained.
- DevOps from day one: automated build, test, analysis, versioning, signed releases and self-update are set up before the first feature.

**Non-goals (v1)**
- Not a product for distribution: one user, one PC. No localisation, no telemetry, no store listing. (Keep the code clean enough that this could change later.)
- No server component. No Docker/Proxmox hosting in v1 (see §9).
- No cross-platform support. Windows 11 x64 only.

**Anti-goals — failure even if it technically works**
- Any instability or slowness. The user's own words: stability and speed problems would make them uninstall it.
- A clunky interface.
- Options that are poorly explained or use jargon.
- A heavy, always-running footprint.
- Doing anything destructive (delete, overwrite, mirror-delete, driver removal) without a clear preview and an explicit tick from the user.

## 3. Constraints

- The PC runs Windows 11 Pro 24H2 (a debloated "X-Lite" build). The suite must not assume default services are running (Windows Search, SysMain, Xbox services and others are disabled). It should detect missing dependencies and say so plainly, never fail mysteriously.
- Mixed hardware: NVMe SSDs, a SATA SSD, 5400 rpm hard drives, a Storage Spaces volume (G:, NTFS), a NAS over SMB. Everything must behave well on slow hard drives and network shares, not only on NVMe.
- The user works mostly through Explorer: Ctrl+C/Ctrl+V day to day, sometimes drag and drop.
- Development tools present: .NET SDK 10, git, GitHub CLI.
- Repository: `K:\Documents\Obsidian\Ferry`.

## 4. Decisions made with the user

- **Suite of separate apps**, not one monolith. Six apps eventually:

  | App | Contents | In v1? |
  |---|---|---|
  | **Copy & Sync** | copy engine, Explorer takeover, folder compare/sync, NAS backup scheduler, "verify folders match", hash checker | Copy engine + Explorer takeover + verify. Sync/backup later. |
  | **Drive Health** | SMART monitoring + history, Windows disk-error log watcher, plain alerts, drive speed test | Yes (speed test may slip) |
  | **Organise** | duplicate finder, bulk renamer, downloads tidier | Duplicate finder only |
  | **Space** | disk space map (TreeSize Free feature set, plus fast MFT scan and duplicates/old-downloads overlays), disk cleanup incl. old driver packages | Later |
  | **System** | app + driver + firmware update monitor & auto-updater, Windows tweaks auditor, startup/services manager, file-lock finder, USB/ISO writer | Later |
  | **Connect** | SSH/serial/telnet terminal (PuTTY feature set), SFTP browser, key manager + agent, port forwarding, network toolkit, Wake-on-LAN | Later |

  Clipboard history: keep only if it adds something over existing free tools. Decide when System is designed.
- **v1 = shared core + Copy & Sync (core copy) + Drive Health + Organise (duplicates).**
- **Explorer takeover is an optional module.** Ctrl+V and drag-drop copies are caught automatically when it's on. When it's off, the normal Windows dialog comes back. The right-click menu uses a **"Ferry" submenu**.
- **Copy window layout: "compact top, tabs below"** (mockup option C). Overall + current-file progress bars, Pause/Cancel and collapse are always visible. Tabs below: Options · Queue · Files · Problems · Log. Collapsible to the top section only.
- **Tooltips:** every option has an ⓘ tooltip in clear, simple language. One or two short sentences: what it does, when you'd want it. No jargon.
- **Alerts:** Windows notifications, plus optional **ntfy** and **Home Assistant** (webhook and/or MQTT sensors per drive).
- **Update checker (System app, later):** apps via winget plus a user watch list; drivers (GPU vendors + Windows Update driver catalogue); SSD firmware (detect + compare + link, never auto-flash); BIOS (detect + link, never auto-flash). **Users can manually set or override the vendor download page for any app/driver/firmware entry. This setting persists and can be changed at any time.**
- **DevOps principles baked in from the start** (§7).

## 5. Feature scope for v1

### 5.1 Shared core
- **I/O engine:** large aligned buffers, unbuffered I/O for large files, overlapped read-ahead/write-behind, a small-file fast path, cancellation and pause at any point.
- **Drive-aware scheduler:** jobs that share a physical source or destination device run one at a time; jobs on independent devices run in parallel. Resolve volumes to physical disks, including Storage Spaces and network shares (treat each SMB server as one device).
- **Hashing:** xxHash3 (default, fast), plus SHA-256 and MD5 for interop. Partial hashes (first/last N KB) for quick duplicate pre-filtering.
- **Job model:** every long operation is a job with a persistent journal, so it resumes after a crash or reboot, plus history and "run again".
- **Pre-flight checks** (shared by copy and organise): path length > 260, reserved names (`CON`, `NUL`, …), trailing dots/spaces, characters illegal on the destination (incl. SMB/NAS), case-only name collisions, FAT32 4 GB limit, free space, locked files, permissions.
- **Undo centre:** reversible operations (moves, renames, recycle-bin deletes, hard-link replacement) recorded with undo.
- **Alerts hub:** one place every tool sends alerts to. Delivers to Windows toasts, ntfy and Home Assistant according to user rules.
- **Settings:** per-tool and suite-wide, exportable/importable. **Portable mode** (runs from a folder, settings stored beside it).
- **CLI for every tool** (e.g. `ferry copy E:\ K:\backup --verify`). Same engine as the UI. Doubles as the main automated-test surface.
- **Logging:** structured local logs with rotation. Local crash dumps. No telemetry.

### 5.2 Copy & Sync (v1 part)
- Copy / Move with every option from the comparison table: pause/resume, queue, verify after copy (checksum), keep dates & attributes, copy permissions (optional), conflict rules (ask / overwrite / overwrite if newer / skip / rename / keep both + "apply to all"), failed-file handling (retry then skip & list / ask / stop) with "retry these", speed limit (incl. by time of day), when-done action (nothing / close / sleep / shut down), exclude filters, file list with status, detailed log.
- **Beyond the other tools:**
  - pre-flight check with fixes (e.g. enable long paths, shorten, see list)
  - **failing-drive mode** (smaller reads around bad areas, rescue readable parts, report exactly what was lost)
  - resume after crash/reboot
  - dry run
  - instant same-volume moves; ReFS block cloning when available
  - network resilience (wait and continue when the NAS blips)
  - prevent sleep while copying
  - checksum file export (`.sha256`/`.xxh` sidecar)
  - undo last move
  - job history
- **Explorer integration (optional module):** catches Ctrl+V and drag-drop copies/moves; "Ferry" right-click submenu (Copy to…, Move to…, Copy here with options…, Verify folders match…; Sync folder to… comes later). Turning it off restores the stock Windows behaviour completely.
- Copy window per §4. Plus a "file already exists" prompt (side-by-side compare: size, dates, which is newer; Replace / Skip / Keep both; apply to all; "replace only if newer").
- **Verify folders match** (standalone): compares two folders by content and lists differences.

### 5.3 Drive Health
- Read SMART / NVMe health for every drive (incl. drives behind Storage Spaces where possible). Store a history.
- Watch the Windows System log for disk errors (`disk` event 7 "bad block", 153, 157 surprise removal, NTFS errors) and map them to the physical drive.
- Plain-language status per drive ("Healthy", "Watch", "Back up now"), with the reason ("H: has logged 925 bad blocks since yesterday").
- Trends: reallocated/pending sectors, media errors, temperature, wear/percentage used, power-on hours.
- Alerts via the hub (Windows, ntfy, Home Assistant). Home Assistant: per-drive sensors over MQTT (health, temperature, error counts).
- Drive speed test (real-world: sequential, small-file, random), with results explained in plain words. May slip to v1.1.
- Shows firmware version per drive (feeds the later update checker).

### 5.4 Organise — duplicate finder
- Scan folders/drives (incl. NAS). Group by size → partial hash → full hash; only same-size files are ever read. Byte-identical regardless of name.
- Optional "similar images" mode (perceptual hash: resized/re-saved copies). Clearly labelled as near-duplicates.
- Filters: file types, size range, include/exclude folders, date-difference rules (e.g. only flag if modified dates differ by less than N days; prefer oldest/newest).
- Auto-select rules: keep oldest / newest / in a chosen folder / shortest path / highest resolution (images).
- Actions: recycle bin, move to a holding folder, replace with hard links (same volume only). All go through the undo centre, with a preview first.
- Image thumbnails / quick preview in results.

## 6. Architecture (builder's latitude within these lines)

- **Language/runtime:** C# on .NET 10 (LTS), x64. *(Claude's call.)*
- **UI:** a native Windows 11 look (Fluent: Mica, rounded corners, Segoe UI Variable, light/dark following the system). Recommended: WPF with .NET's built-in Fluent theme, which is lighter, mature and stable. WinUI 3 is acceptable if the builder judges it better, but justify the startup-time and memory cost. *(Claude's call; builder may revisit with evidence.)*
- **Process model:**
  - Each app is its own executable, started on demand.
  - **Ferry Helper**, the only resident piece: a small Windows service (for privileged work: SMART, event log, the driver store later, MFT scanning later) plus a per-user tray component (alerts, tray menu, receiving jobs from Explorer). Native AOT where practical. Budget: idle ≈0% CPU, ≤40 MB RAM total.
  - UI apps talk to the helper over local named pipes with an authenticated, versioned protocol. Apps run non-elevated; only the service holds admin rights and exposes a narrow, validated command set.
- **Explorer module:** native code (C++), as thin as possible. It only hands the operation (source list, destination, copy/move) to the helper over a pipe and returns. All logic stays out of Explorer's process. If the helper isn't reachable or anything fails, it falls back to the stock Windows copy immediately. The exact interception mechanism (copy hook vs drop handler vs paste/drag handler) is the builder's choice, and needs a **spike first** (§8) to prove it works for both Ctrl+V and drag-drop on Windows 11 24H2 without harming Explorer stability. The context submenu uses the Windows 11 modern menu (IExplorerCommand with package identity) and the classic menu.
- **Copy engine:** custom pipelined copier (not plain `CopyFileEx`) for large files; the builder may use `CopyFile2` for small files if it benchmarks faster. Must preserve timestamps, attributes, ADS (opt-in), sparse files, and NTFS permissions (opt-in).
- **Self-update:** suite updates itself from GitHub Releases with signed packages and delta updates where possible (Velopack recommended).
- **Installer:** lets the user pick which apps to install. Portable zip as well.

## 7. DevOps baseline (in place before the first feature)

- **Repo:** git, trunk-based on `main` with short-lived branches and PRs; Conventional Commits; protected `main` once on GitHub.
- **Solution layout:** `src/` (Core, Engine, Helper, each app, CLI, ExplorerExt), `tests/` (unit, integration, end-to-end, benchmarks), `build/`, `docs/`.
- **Build:** reproducible `dotnet build` and a single `build.ps1` that does what CI does. Central package management, locked dependency versions, warnings as errors, nullable enabled, .NET analyzers + StyleCop/format check, `.editorconfig`.
- **CI (GitHub Actions, windows runners):** on every push/PR:
  - restore → build → format check → analyzers → unit tests → integration tests → coverage report
  - CodeQL security scan
  - Dependabot for NuGet and Actions
- **Release pipeline:**
  - a version tag triggers the release
  - SemVer via git tags (MinVer or similar)
  - changelog generated from commits
  - signed binaries and installer
  - GitHub Release with Velopack packages
  - smoke test of the installed build
- **Testing strategy:**
  - unit tests for all core logic
  - file-system abstraction so the engine can be tested with **fault injection** (bad blocks, disconnects, full disk, locked files, long paths, illegal names)
  - integration tests on real temp folders and small VHDX volumes (NTFS, FAT32, ReFS) where the runner allows
  - CLI-driven end-to-end tests
  - Explorer module tested by a dedicated harness, not by unit tests alone
- **Performance as a test:**
  - BenchmarkDotNet micro-benchmarks
  - a repeatable copy benchmark (large files, many small files, mixed; SSD→SSD and HDD→SSD) compared against FastCopy on this PC
  - regressions flagged in CI where the hardware allows, otherwise by a local `bench.ps1` before release
- **Stability gates:**
  - long-running soak test (copy/verify loops) before each release
  - helper memory/CPU budget checked in tests
  - crash dumps and logs collected locally
- **Docs:** README, architecture notes (ADRs for big decisions: UI framework, Explorer mechanism, update system), user help generated from the same text as the tooltips.
- **Feature flags:** risky features (Explorer takeover, failing-drive mode, hard-link replacement) can be switched off in settings without reinstalling.

## 8. Build order (for the planner to refine)

1. Repo, solution skeleton, `build.ps1`, CI, analyzers, test projects, release pipeline producing an empty signed build.
2. **Spike (throwaway): Explorer interception** on Win11 24H2 for Ctrl+V and drag-drop, and its fallback. Write the outcome into an ADR. If it proves unworkable, tell the user before going further.
3. Core: file-system abstraction, I/O engine, hashing, job journal, pre-flight, drive-aware scheduler, logging, settings. CLI first.
4. Copy & Sync: CLI copy → benchmark vs FastCopy → copy window UI → conflict prompt → verify folders match.
5. Ferry Helper (service + tray) + alerts hub (Windows/ntfy/Home Assistant).
6. Explorer module (production version) + right-click submenu.
7. Drive Health.
8. Organise — duplicate finder.
9. Installer, self-update, soak test, v1.0 release.

## 9. Explicitly deferred

- Space, System, Connect apps, and the Copy & Sync sync/backup features: each gets its own short design pass before building.
- Optional self-hosted "hub" container (Docker on the user's Proxmox host) for multi-machine dashboards: only if the user later wants it. v1 sends alerts directly to ntfy/Home Assistant.

## 10. UI/UX rules (all apps)

- Progress and status first. Options one click away, never in the way.
- Colour only for meaning: blue = working, green = verifying/done, amber = paused/attention, red = failed.
- Every option has a plain-language ⓘ tooltip. Tooltip text lives in one resource file and is reviewed as a whole for tone.
- Destructive actions: preview → explicit confirmation → undo where possible.
- Every window works at 100–200 % scaling, light and dark, and keyboard-only.
- No window should take more than ~1 s to appear on this PC.
- Visual reference: the "Ferry" design-run prompt (eight screens: expanded and collapsed copy window, its states, the conflict prompt, the context submenu, main window, settings, tray). Outputs from that design run, once the user brings them back, override the mockups here where they differ.

## 11. Calls made by Claude after the builder check (user may override)

- **Packaging:** one suite package (Velopack) with a single SemVer for the whole suite. "Pick which apps to install" means per-app toggles inside the suite: shortcuts, Explorer menu entries and the helper features for unticked apps are not registered. The helper service and the Explorer DLL update via a staged swap: write new files beside the old ones, restart the service, and pick up the Explorer DLL on next Explorer restart or log-on. Never force-kill Explorer.
- **Portable mode:** apps + CLI only. No helper service, no Explorer takeover, no background Drive Health monitoring, no self-update. Drive Health still works as an on-demand scan when started elevated. Settings, journals and logs live beside the executables. Settings has an "Install helper" button to upgrade a portable copy to the full install.
- **Elevation:** apps run non-elevated. When a copy needs admin rights (copying permissions/ownership, reading protected files), the app offers "Restart this job as administrator" and launches an elevated copy worker for that job only, with a UAC prompt. The service is **not** used for general file I/O, so its command set stays narrow (SMART, event log, later the driver store and MFT scanning).
- **Speed target (release gate):** on this PC, within 10 % of FastCopy (its default "Copy" mode, default buffer, verify off, then again with verify on) for each of these sets:
  - HDD (E:) → NVMe: large files
  - HDD (E:) → NVMe: 10,000+ small files
  - NVMe → NVMe: large files
  - NVMe → NVMe: mixed files

  SMB/NAS throughput is measured and reported but not a release gate (the network varies too much).
- **UI reference:** the three layout mockups are saved at `docs/design/copy-window-layout-mockups.html` (the user chose option C). If the design-run output isn't in the repo when UI work starts, build from this spec plus the mockup and restyle later. The UI layer is kept separate from logic so restyling is cheap.

## 12. Decided by the user after the builder check

- **Code signing: self-signed certificate**, trusted only on the user's PC. The private key is stored as a GitHub Actions secret, and CI signs binaries, the installer, Velopack packages and the sparse MSIX for the modern context menu with it. The self-updater verifies packages against the pinned certificate thumbprint. Include a script to create, install (trusted root + trusted publisher on this PC) and rotate the cert. Nothing secret is committed to the repo.
- **GitHub repo: public.** Free Windows CI minutes, CodeQL, branch protection on `main`. Self-update reads public GitHub Releases with no token. Repo hygiene follows from being public: secret scanning on, no personal paths or machine names hard-coded in source or tests (benchmark configs that name drives such as E:/K: live in a git-ignored local file with a committed example).
- **Explorer takeover: always show the copy window** for Ctrl+V and drag-drop. **Plus a quick-paste shortcut, Ctrl+Shift+V by default and changeable in Settings:** Ferry does the copy with no window (tray progress only) and shows the window only if a conflict or problem comes up. The Explorer spike (§8 step 2) must confirm the chosen shortcut doesn't clash with anything Explorer already uses. Drag-drop has no quick variant, since Windows already uses its modifier keys for copy/move/link.

## 13. Left to the builder

- Exact libraries (MQTT client, SMART/NVMe access approach, perceptual hash algorithm, logging library), as long as they fit the lightweight/stable goals and are actively maintained.
- Buffer sizes, thread counts, and scheduler heuristics, chosen from benchmarks.
- Internal module boundaries beyond those named in §6.
- Exact CLI syntax, provided it's consistent across tools and documented.
