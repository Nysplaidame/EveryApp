# EveryApp Milestone 1 — Foundation, Core & Ferry Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A secure, CI-built repo with the shared core (file system, hashing, preflight, scheduler, journal, settings, logging) and Ferry's copy engine + CLI, proven stable under fault injection and within 10 % of FastCopy.

**Architecture:** Class libraries `EveryApp.Core` (shared, app-agnostic) and `Ferry.Engine` (copy logic) sit behind a narrow `IFileSystem` abstraction so every failure mode can be injected in tests. `Ferry.Cli` is a thin console front-end over the engine and the main end-to-end test surface. No UI, service or Explorer code ships in this milestone. The Explorer interception approach is decided by a throwaway spike recorded in an ADR.

**Tech Stack:** C# / .NET 10 (x64, Windows only); xUnit v3; System.IO.Hashing (XxHash3); System.CommandLine 2.x; Serilog (file sink); BenchmarkDotNet; GitHub Actions; CodeQL; CycloneDX; signtool.

**Spec:** `docs/superpowers/specs/2026-11-06-ferry-suite-v1-design.md` (read §2, §5.1, §5.2, §6, §7, §11, §12, §14 before starting). Also read `CLAUDE.md`.

## Global Constraints

- Target `net10.0-windows`, `win-x64` only. `global.json` pins SDK `10.0.102` with `rollForward: latestFeature`.
- `TreatWarningsAsErrors=true`, `Nullable=enable`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`, deterministic builds.
- Central package management (`Directory.Packages.props`) with `RestorePackagesWithLockFile=true`; CI restores with `--locked-mode`. Every new dependency is justified in its commit message.
- No telemetry. Logs are local only and scrubbed of secrets and user-profile paths.
- Public repo: no secrets, personal paths, drive letters or machine names in committed code/tests/config. Machine-specific values live in git-ignored `*.local.json` files with a committed `*.example.json`.
- GitHub Actions pinned by full commit SHA; workflow `permissions:` default `contents: read`.
- Conventional Commits; one commit per task step marked "Commit".
- Speed gate: Ferry within **10 %** of FastCopy (default "Copy" mode, default buffer; verify off and on) on: HDD→NVMe large files, HDD→NVMe 10,000+ small files, NVMe→NVMe large files, NVMe→NVMe mixed. SMB measured, not gated.
- Destructive operations never delete or overwrite user data without an explicit option. Copies write to a temporary name and rename on success, so a cancelled or crashed copy never leaves a half-written file under the real name.
- Never follow a reparse point (junction/symlink) out of the selected tree unless `FollowReparsePoints` is explicitly set.
- User-facing strings (CLI messages included) are plain language, no jargon.

## Review Focus

1. **Source file changes while being copied** (e.g. a download still in progress): expected to be detected (size/last-write before vs after) and reported as a problem, never silently "verified". Test: Task 12 `Copy_SourceModifiedDuringCopy_ReportsProblem`.
2. **Destination fills up mid-file:** expected to stop that file cleanly, delete the temp part-file, report "not enough space on K:", and leave earlier files intact. Test: Task 13 `Copy_DiskFullMidFile_RemovesPartFileAndReports`.
3. **NAS/network drops mid-file:** expected to wait and retry (with backoff) up to the configured limit, then resume the file. Test: Task 14 `Copy_NetworkDropsThenReturns_ResumesAndCompletes`.
4. **Junction loops and reparse points** (e.g. `Application Data` loops in user profiles): expected to be skipped and listed, never infinite recursion or escaping the tree. Test: Task 8 `Enumerate_JunctionLoop_SkipsAndReports`.
5. **Crash or power loss mid-job, then resume:** expected to skip finished files, redo the interrupted one, and never trust a partially written journal line. Test: Task 10 `Journal_TruncatedLastLine_IsIgnoredOnResume` and Task 15 `Cli_ResumeAfterKill_CompletesWithoutDuplicates`.

---

## File Structure

```
EveryApp.slnx
global.json · Directory.Build.props · Directory.Packages.props · .editorconfig · .gitattributes
build.ps1                       # restore → format check → build → test (same as CI)
bench.ps1 · soak.ps1            # local-only perf and stability runs
bench.example.json              # committed template; bench.local.json is git-ignored
README.md · SECURITY.md
.github/workflows/ci.yml · codeql.yml · release.yml
.github/dependabot.yml
scripts/New-SigningCert.ps1     # creates/installs/rotates the self-signed cert
docs/adr/0001-explorer-interception.md
docs/security/threat-model.md
docs/perf/results.md
spikes/explorer-interception/   # throwaway; excluded from solution and CI
src/EveryApp.Core/
  FileSystem/ IFileSystem.cs · FileEntry.cs · WindowsFileSystem.cs · ErrorKinds.cs
  Hashing/    HashAlgorithmKind.cs · StreamingHasher.cs · PartialHash.cs
  Paths/      PathSafety.cs · TreeWalker.cs
  Preflight/  PreflightCheck.cs · PreflightIssue.cs · PreflightRunner.cs
  Scheduling/ DeviceResolver.cs · DeviceSet.cs · DriveAwareScheduler.cs
  Jobs/       JobJournal.cs · JournalRecord.cs · PauseGate.cs
  Settings/   SettingsStore.cs · AppPaths.cs
  Logging/    LogSetup.cs · SecretScrubber.cs
src/Ferry.Engine/
  CopyOptions.cs · CopyProgress.cs · CopyResult.cs · Problem.cs
  FileCopier.cs · BufferPool.cs · TreeCopier.cs · Verifier.cs · RescueReader.cs · RetryPolicy.cs
src/Ferry.Cli/
  Program.cs · CopyCommand.cs · VerifyCommand.cs · ProgressPrinter.cs · ExitCodes.cs
tests/EveryApp.Core.Tests/      # + Fakes/InMemoryFileSystem.cs · Fakes/FaultInjectingFileSystem.cs
tests/Ferry.Engine.Tests/
tests/Ferry.Cli.Tests/          # end-to-end via the built exe on temp folders
bench/Ferry.Bench/              # BenchmarkDotNet micro-benchmarks
```

Test fakes live in `tests/EveryApp.Core.Tests/Fakes` and are shared to other test projects via a `<Compile Include>` link.

---

### Task 1: Repo foundation and `build.ps1`

**Files:**
- Create: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `.gitattributes`, `EveryApp.slnx`, `build.ps1`, `README.md`, the three `src/` projects, the three `tests/` projects (empty, one smoke test each)
- Modify: `.gitignore` (add `bin/`, `obj/`, `*.local.json`, `artifacts/`, `TestResults/`)

**Interfaces:**
- Produces: `build.ps1 [-Configuration Release|Debug] [-SkipTests] [-Sign]`, which exits non-zero on any failure. CI calls exactly this.

- [ ] **Step 1:** Create projects with `dotnet new classlib|console|xunit3`, add them to `EveryApp.slnx`, and set `<TargetFramework>net10.0-windows</TargetFramework>`, `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` and the Global Constraints properties in `Directory.Build.props`. Line endings: `.gitattributes` `* text=auto eol=crlf`, `*.sh eol=lf`.
- [ ] **Step 2:** Add smoke test `Smoke_AssemblyLoads` in each test project (`Assert.NotNull(typeof(<a type in the project>).Assembly)`).
- [ ] **Step 3:** Write `build.ps1`: `dotnet restore --locked-mode` (on first run generate the lock files without `--locked-mode`, then commit them) → `dotnet format --verify-no-changes` → `dotnet build -c $Configuration --no-restore` → `dotnet test --no-build`. Use `Set-StrictMode -Version Latest` and `$ErrorActionPreference='Stop'`, and check `$LASTEXITCODE` after each call.
- [ ] **Step 4:** Run `./build.ps1`. Expected: exit 0, 3 tests passed, 0 warnings.
- [ ] **Step 5:** Commit `build: add solution skeleton and build script`.

### Task 2: CI, security baseline and threat model

**Files:**
- Create: `.github/workflows/ci.yml`, `.github/workflows/codeql.yml`, `.github/dependabot.yml`, `SECURITY.md`, `docs/security/threat-model.md`

- [ ] **Step 1:** `ci.yml`: on push/PR to `main`; `runs-on: windows-latest`; `permissions: contents: read`; steps checkout → setup-dotnet (from `global.json`) → `./build.ps1 -Configuration Release` → upload test results. Resolve each action's SHA with `gh api repos/<owner>/<action>/commits/<tag> --jq .sha` and pin it as `uses: actions/checkout@<sha> # v4`.
- [ ] **Step 2:** `codeql.yml`: C# analysis on push/PR plus weekly, `build-mode: manual` running `./build.ps1 -SkipTests`, `permissions: security-events: write` on that job only.
- [ ] **Step 3:** `dependabot.yml`: `nuget` and `github-actions`, weekly, grouped minor/patch.
- [ ] **Step 4:** `threat-model.md`: STRIDE table per component in spec §14 (apps, helper service, named pipes, Explorer extension, elevated copy worker, updater, alert integrations, feed downloads). For M1 components (Core, Engine, CLI) fill in threats and mitigations in full. Mark the others "designed in M2+", with the mitigations from §14 copied in. Include trust boundaries: user ↔ file system (untrusted names/content), CLI args, journal files.
- [ ] **Step 5:** `SECURITY.md`: how to report privately (GitHub private vulnerability reporting), supported versions = latest.
- [ ] **Step 6:** **Ask the user before creating the public GitHub repo.** On approval:
  - `gh repo create EveryApp --public --source . --push`
  - enable secret scanning + push protection, private vulnerability reporting, and branch protection on `main` (PR required, CI + CodeQL required, no force-push) via `gh api`
- [ ] **Step 7:** Push a branch and open a PR. Expected: CI and CodeQL both green.
- [ ] **Step 8:** Commit `ci: add CI, CodeQL, Dependabot and security docs`.

### Task 3: Signing and release skeleton

**Files:**
- Create: `scripts/New-SigningCert.ps1`, `.github/workflows/release.yml`
- Modify: `build.ps1` (add `-Sign`)

**Interfaces:**
- Produces: `build.ps1 -Sign` signs every `EveryApp.*.dll`, `Ferry.*.dll` and `ferry.exe` in `artifacts/` with the cert whose thumbprint is in `$env:EVERYAPP_SIGN_THUMBPRINT` (local) or from the PFX in `$env:EVERYAPP_SIGN_PFX_BASE64` + `$env:EVERYAPP_SIGN_PFX_PASSWORD` (CI).

- [ ] **Step 1:** `New-SigningCert.ps1 [-Rotate]`:
  - `New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=EveryApp (self-signed)" -KeyAlgorithm RSA -KeyLength 3072 -NotAfter (Get-Date).AddYears(3) -CertStoreLocation Cert:\CurrentUser\My`
  - import the public cert into `CurrentUser\Root` and `CurrentUser\TrustedPublisher`
  - export a password-protected PFX to a git-ignored path
  - print the `gh secret set` commands for the user to run. **The script never uploads secrets itself.**
- [ ] **Step 2:** `build.ps1 -Sign`: `signtool sign /fd SHA256 /td SHA256 /tr http://timestamp.digicert.com /sha1 <thumbprint>`; then `Get-AuthenticodeSignature` on each file must be `Valid`, or the build fails.
- [ ] **Step 3:** `release.yml`: on tag `v*`, with job permissions `contents: write`, `id-token: write`, `attestations: write`:
  - `./build.ps1 -Configuration Release -Sign`
  - `dotnet publish src/Ferry.Cli -c Release -r win-x64 --self-contained false`
  - CycloneDX SBOM (`dotnet CycloneDX EveryApp.slnx -o artifacts/sbom`)
  - `actions/attest-build-provenance` on the zip
  - GitHub Release with the zip + SBOM
- [ ] **Step 4:** Run `./scripts/New-SigningCert.ps1` then `./build.ps1 -Sign` locally. Expected: all binaries `Valid`.
- [ ] **Step 5:** Commit `build: add self-signed code signing and release workflow`.

### Task 4: Explorer interception spike (throwaway) → ADR 0001

**Files:**
- Create: `spikes/explorer-interception/` (not in solution or CI), `docs/adr/0001-explorer-interception.md`

Goal: choose how Ferry catches **Ctrl+V** and **left drag-drop** copies/moves in Explorer on this PC (Win11 24H2), and confirm **Ctrl+Shift+V** is free in Explorer. Candidates to try, in this order:
- **(A) Out-of-process paste catcher.** A low-level keyboard hook in the (future) tray helper. On Ctrl+V / Ctrl+Shift+V while an Explorer window has focus, read `CF_HDROP` + `Preferred DropEffect` from the clipboard and get the target folder via `IShellWindows`. Swallow the key only if Ferry accepts the job. Nothing runs inside Explorer.
- **(B) In-process drop handling** for left-drag, via a minimal native shell extension or hooking `IFileOperation::PerformOperations`, which hands off to a pipe and falls back to the stock operation on any failure.
- **(C) Right-drag `DragDropHandler`** shell extension (documented API) as a fallback for drag.

- [ ] **Step 1:** Build the smallest proof for (A). Record: works for Ctrl+V/Ctrl+Shift+V in File Explorer windows and the desktop? Cut vs copy detected? Latency? Behaviour when the helper is killed (stock paste must still work)?
- [ ] **Step 2:** Build the smallest proof for drag (B, then C if B is unsafe). Record: Explorer stability over 200 drag operations, fallback when the pipe is down, and the mitigations needed.
- [ ] **Step 3:** Write ADR 0001: chosen approach for paste and for drag, rejected options with reasons, risks, and the stability test the production version must pass.
- [ ] **Step 4:** **Stop and report to the user** (summary + ADR link). If no safe left-drag approach exists, propose "Ctrl+V auto, drag via right-drag menu" and wait for their decision before M2.
- [ ] **Step 5:** Commit `docs: ADR 0001 explorer interception (spike result)`. The spike code stays in `spikes/` and isn't built by CI.

### Task 5: `IFileSystem` abstraction, Windows implementation and test fakes

**Files:**
- Create: `src/EveryApp.Core/FileSystem/*.cs`, `tests/EveryApp.Core.Tests/Fakes/InMemoryFileSystem.cs`, `tests/EveryApp.Core.Tests/Fakes/FaultInjectingFileSystem.cs`, `tests/EveryApp.Core.Tests/FileSystem/WindowsFileSystemTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public enum IoErrorKind { None, NotFound, AccessDenied, SharingViolation, DiskFull, BadBlock, NetworkLost, PathTooLong, InvalidName, Other }
  public sealed record FileEntry(string FullPath, long Length, FileAttributes Attributes, DateTime CreationTimeUtc, DateTime LastWriteTimeUtc, bool IsReparsePoint);
  public interface IReadHandle : IAsyncDisposable { long Length { get; } ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken ct); }
  public interface IWriteHandle : IAsyncDisposable { ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken ct); ValueTask SetLengthAsync(long length); ValueTask FlushAsync(); }
  public interface IFileSystem {
      IReadHandle OpenRead(string path, bool unbuffered);
      IWriteHandle CreateWrite(string path, long preallocate, bool unbuffered);
      FileEntry GetEntry(string path);
      IEnumerable<FileEntry> EnumerateChildren(string directory);
      void CreateDirectory(string path);
      void Move(string from, string to, bool overwrite);
      void Delete(string path);
      void SetMetadata(string path, FileAttributes attributes, DateTime creationUtc, DateTime lastWriteUtc);
      long GetFreeSpace(string path);
      int GetSectorSize(string path);
  }
  public sealed class FileSystemException(string path, IoErrorKind kind, Exception? inner) : IOException;
  public static class ErrorKinds { public static IoErrorKind FromHResult(int hresult); }
  ```
- `FaultInjectingFileSystem(IFileSystem inner)` exposes `void FailRead(string path, long atOffset, IoErrorKind kind, int times = int.MaxValue)`, `void FailWrite(...)`, `void SetFreeSpace(string root, long bytes)`, `void MutateDuringRead(string path, long atOffset, Action mutate)`.

- [ ] **Step 1: Write failing tests:**
  - `ErrorKinds_MapsWin32Codes`: 2→NotFound, 5→AccessDenied, 32→SharingViolation, 112→DiskFull, 23→BadBlock, 64→NetworkLost, 59→NetworkLost, 206→PathTooLong, 123→InvalidName
  - `WindowsFileSystem_UnbufferedReadWrite_RoundTrips` (temp dir, 3 MiB + 17 bytes)
  - `WindowsFileSystem_LongPath_Works` (path length 400, `\\?\` handled internally)
  - `FaultInjecting_FailRead_ThrowsWithKind`
- [ ] **Step 2:** Run `dotnet test tests/EveryApp.Core.Tests`. Expected: FAIL (types missing).
- [ ] **Step 3:** Implement `WindowsFileSystem`:
  - use `File.OpenHandle` + `RandomAccess`
  - unbuffered = `FileOptions.WriteThrough | (FileOptions)0x20000000` (`FILE_FLAG_NO_BUFFERING`), with offsets and lengths aligned to `GetSectorSize`
  - preallocate via `SetLengthAsync`
  - translate every `IOException`/`UnauthorizedAccessException` through `ErrorKinds`
  - prefix paths with `\\?\` internally when longer than 259

  `InMemoryFileSystem` is a dictionary-backed equivalent.
- [ ] **Step 4:** Run tests. Expected: PASS.
- [ ] **Step 5:** Commit `feat(core): file system abstraction with fault injection`.

### Task 6: Hashing

**Files:**
- Create: `src/EveryApp.Core/Hashing/*.cs`, `tests/EveryApp.Core.Tests/Hashing/HashingTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public enum HashAlgorithmKind { XxHash3, Sha256, Md5 }
  public sealed class StreamingHasher : IDisposable { public StreamingHasher(HashAlgorithmKind kind); public void Append(ReadOnlySpan<byte> data); public byte[] Finish(); }
  public static class PartialHash { public static ValueTask<ulong> ComputeAsync(IFileSystem fs, string path, int headTailBytes = 65536, CancellationToken ct = default); }
  ```

- [ ] **Step 1: Write failing tests:**
  - `XxHash3_KnownVector`: empty input → `2D06800538D394C2` (64-bit, seed 0)
  - `Sha256_KnownVector`: "abc" → `BA7816BF…F20015AD`
  - `StreamingHasher_ChunkedEqualsWhole` (1 MiB random data in 7 uneven chunks)
  - `PartialHash_FilesDifferingOnlyInMiddle_Equal`
  - `PartialHash_FilesDifferingInTail_Differ`
- [ ] **Step 2:** Run tests. Expected: FAIL.
- [ ] **Step 3:** Implement using `System.IO.Hashing.XxHash3` and `IncrementalHash`. `PartialHash` hashes the length (8 bytes LE), head bytes and tail bytes; files ≤ 2×headTailBytes are hashed whole.
- [ ] **Step 4:** Run tests. Expected: PASS.
- [ ] **Step 5:** Commit `feat(core): streaming and partial hashing`.

### Task 7: Logging, settings and app paths

**Files:**
- Create: `src/EveryApp.Core/Logging/*.cs`, `src/EveryApp.Core/Settings/*.cs`, tests in `tests/EveryApp.Core.Tests/Settings/` and `/Logging/`

**Interfaces:**
- Produces:
  ```csharp
  public static class AppPaths { public static bool IsPortable { get; } public static string DataRoot { get; } public static string Logs { get; } public static string Jobs { get; } }
  public sealed class SettingsStore<T> where T : class, new() { public SettingsStore(string fileName); public T Load(); public void Save(T value); }
  public static class SecretScrubber { public static string Scrub(string message); }
  public static class LogSetup { public static ILogger Create(string appName); }
  ```
- Portable mode: a file `portable.flag` next to the executable. Then `DataRoot` = `<exe dir>\data`, otherwise `%LocalAppData%\EveryApp`.

- [ ] **Step 1: Write failing tests:**
  - `Settings_SaveLoad_RoundTrips`
  - `Settings_CorruptFile_ReturnsDefaultsAndKeepsBackup` (corrupt file renamed `*.corrupt-<timestamp>.json`)
  - `Settings_Save_IsAtomic` (write temp + `File.Replace`)
  - `Scrubber_RedactsUserProfile`: `C:\Users\Alice\x` → `%USERPROFILE%\x`
  - `Scrubber_RedactsTokens`: values after `token=`, `password=`, `Authorization: Bearer ` → `***`
  - `AppPaths_PortableFlag_UsesExeFolder`
- [ ] **Step 2:** Run tests. Expected: FAIL.
- [ ] **Step 3:** Implement with `System.Text.Json` source generation. Settings files carry `"schemaVersion": 1`. Serilog: rolling file per app per day, 14 files kept, scrubber applied via an enricher/formatter.
- [ ] **Step 4:** Run tests. Expected: PASS.
- [ ] **Step 5:** Commit `feat(core): settings, portable mode and scrubbed logging`.

### Task 8: Path safety and tree walking

**Files:**
- Create: `src/EveryApp.Core/Paths/*.cs`, `tests/EveryApp.Core.Tests/Paths/*.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static class PathSafety { public static bool IsWithin(string root, string candidate); public static string Normalize(string path); }
  public sealed record WalkItem(FileEntry Entry, string RelativePath);
  public sealed record WalkSkip(string Path, string Reason);
  public sealed class TreeWalker { public TreeWalker(IFileSystem fs, bool followReparsePoints, IReadOnlyList<string> excludeGlobs); public IEnumerable<WalkItem> Walk(string root, Action<WalkSkip> onSkip); }
  ```

- [ ] **Step 1: Write failing tests:**
  - `IsWithin_RejectsDotDotEscape`: root `C:\a`, candidate `C:\a\..\b` → false
  - `IsWithin_RejectsPrefixTrick`: root `C:\a`, candidate `C:\ab\x` → false
  - `Walk_SkipsReparsePointsByDefault`
  - `Enumerate_JunctionLoop_SkipsAndReports` (real junction in temp dir pointing at its parent; walk terminates; one `WalkSkip` with reason containing "link")
  - `Walk_ExcludeGlobs_Applied` (`Thumbs.db`, `**/node_modules/**`)
  - `Walk_AccessDeniedFolder_ReportsAndContinues`
- [ ] **Step 2:** Run tests. Expected: FAIL.
- [ ] **Step 3:** Implement an iterative (stack-based, not recursive) walk. When following reparse points, track visited `(volume serial, file ID)` pairs to break loops. Globs via `Microsoft.Extensions.FileSystemGlobbing`.
- [ ] **Step 4:** Run tests. Expected: PASS.
- [ ] **Step 5:** Commit `feat(core): safe tree walker`.

### Task 9: Preflight checks

**Files:**
- Create: `src/EveryApp.Core/Preflight/*.cs`, `tests/EveryApp.Core.Tests/Preflight/PreflightTests.cs`

**Interfaces:**
- Consumes: `IFileSystem`, `WalkItem`
- Produces:
  ```csharp
  public enum PreflightCode { PathTooLong, ReservedName, TrailingDotOrSpace, IllegalCharForDestination, CaseCollision, TooLargeForFat32, NotEnoughSpace, Locked }
  public sealed record PreflightIssue(PreflightCode Code, string Path, string Message);   // Message is plain language
  public sealed class PreflightRunner { public PreflightRunner(IFileSystem fs); public IReadOnlyList<PreflightIssue> Check(IReadOnlyList<WalkItem> items, string destinationRoot, string destinationFileSystem /* "NTFS","FAT32","exFAT","ReFS","SMB" */, bool longPathsEnabled); }
  ```

- [ ] **Step 1: Write failing tests**, one per code:
  - `PathTooLong` when destination full path > 259 and `longPathsEnabled == false`
  - `ReservedName` for `CON`, `nul.txt`, `COM1`, `LPT9` (case-insensitive, with or without extension)
  - `TrailingDotOrSpace` for `"a."`, `"b "`
  - `IllegalCharForDestination` for `:` `*` `?` `"` `<` `>` `|` when destination is `"SMB"`
  - `CaseCollision` for `A.txt` + `a.txt` in the same folder
  - `TooLargeForFat32` for a 4 GiB file (4,294,967,296 bytes) to `"FAT32"`
  - `NotEnoughSpace` when total > free − 1 % margin
  - `Messages_ArePlainLanguage`: assert no message contains "MAX_PATH", "HRESULT", "errno" or "0x"
- [ ] **Step 2:** Run tests. Expected: FAIL.
- [ ] **Step 3:** Implement. `Locked` is checked lazily by the copier (not here) and reported via the same type. Message examples: "This path is too long for Windows (312 characters; the limit is 260)."
- [ ] **Step 4:** Run tests. Expected: PASS.
- [ ] **Step 5:** Commit `feat(core): preflight checks`.

### Task 10: Job journal and pause gate

**Files:**
- Create: `src/EveryApp.Core/Jobs/*.cs`, `tests/EveryApp.Core.Tests/Jobs/*.cs`

**Interfaces:**
- Produces:
  ```csharp
  public enum JournalRecordKind { JobStarted, FileDone, FileSkipped, FileFailed, JobFinished }
  public sealed record JournalRecord(JournalRecordKind Kind, string? RelativePath, long Length, DateTime SourceLastWriteUtc, string? Hash, string? Detail);
  public sealed class JobJournal : IDisposable {
      public static JobJournal Create(string jobsDir, Guid jobId, string optionsJson);
      public static JobJournal Open(string jobsDir, Guid jobId);   // for resume
      public void Append(JournalRecord record);                    // durable per checkpoint policy
      public IReadOnlySet<string> CompletedPaths { get; }
      public string OptionsJson { get; }
      public bool IsFinished { get; }
  }
  public sealed class PauseGate { public void Pause(); public void Resume(); public bool IsPaused { get; } public ValueTask WaitIfPausedAsync(CancellationToken ct); }
  ```

- [ ] **Step 1: Write failing tests:**
  - `Journal_AppendThenOpen_RestoresCompleted`
  - `Journal_TruncatedLastLine_IsIgnoredOnResume` (append 3 records, cut the file mid-line, reopen → 2 completed, no exception)
  - `Journal_CorruptMiddleLine_StopsAtCorruption` (trust only lines before it)
  - `PauseGate_BlocksUntilResume`
  - `PauseGate_CancelWhilePaused_Throws`
- [ ] **Step 2:** Run tests. Expected: FAIL.
- [ ] **Step 3:** Implement as JSON Lines with a CRC32 per line (`{"crc":...,"r":{...}}`). Flush with `FileStream.Flush(true)` every 64 records or 2 seconds, whichever comes first, and always on `JobFinished`/`FileFailed`.
- [ ] **Step 4:** Run tests. Expected: PASS.
- [ ] **Step 5:** Commit `feat(core): crash-safe job journal and pause gate`.

### Task 11: Device resolver and drive-aware scheduler

**Files:**
- Create: `src/EveryApp.Core/Scheduling/*.cs`, `tests/EveryApp.Core.Tests/Scheduling/*.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record DeviceSet(IReadOnlySet<string> Ids) { public bool Overlaps(DeviceSet other); }  // ids like "disk:3", "smb:nas"
  public interface IDeviceResolver { DeviceSet Resolve(string path); }
  public sealed class WindowsDeviceResolver : IDeviceResolver { }
  public sealed class DriveAwareScheduler {
      public DriveAwareScheduler(IDeviceResolver resolver);
      public Task<IAsyncDisposable> AcquireAsync(string source, string destination, CancellationToken ct); // waits while an active job overlaps
      public event Action<string /*jobLabel*/, string /*waitingReason*/>? Waiting;
  }
  ```

- [ ] **Step 1: Write failing tests** (fake resolver):
  - `SameSourceDisk_SecondJobWaits`
  - `DifferentDisks_RunInParallel`
  - `SameSmbServer_Serialized`
  - `StorageSpace_MapsToAllMemberDisks` (fake returns `disk:9,disk:6,disk:7` and overlaps `disk:6`)
  - `ReleaseOnDispose_StartsWaitingJob`
  - `WaitingReason_IsPlainLanguage`: "Waiting: uses the same drive as another copy"
- [ ] **Step 2:** Run tests. Expected: FAIL.
- [ ] **Step 3:** Implement `WindowsDeviceResolver` with:
  - `GetVolumePathName` → `GetVolumeNameForVolumeMountPoint` → `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` → `disk:N`
  - for Storage Spaces virtual disks, also add the member physical disks via `MSFT_PhysicalDisk` / `MSFT_VirtualDisk` association (WMI `root\Microsoft\Windows\Storage`); if that fails, keep `disk:N` only
  - UNC paths → `smb:<server lowercased>`

  Use P/Invoke via `Microsoft.Windows.CsWin32` source generator. The scheduler is FIFO with a `SemaphoreSlim`-guarded active list.
- [ ] **Step 4:** Run tests. Then run a manual check `dotnet test --filter Category=Machine`, which prints the resolved sets for every local drive; compare with Disk Management.
- [ ] **Step 5:** Commit `feat(core): drive-aware job scheduler`.

### Task 12: Single-file copier (pipelined, unbuffered, pause/cancel)

**Files:**
- Create: `src/Ferry.Engine/{CopyOptions,CopyProgress,Problem,BufferPool,FileCopier}.cs`, `tests/Ferry.Engine.Tests/FileCopierTests.cs`

**Interfaces:**
- Consumes: `IFileSystem`, `StreamingHasher`, `PauseGate`
- Produces:
  ```csharp
  public sealed record CopyOptions {
      public bool Verify { get; init; } = true;
      public bool KeepDatesAndAttributes { get; init; } = true;
      public bool CopyPermissions { get; init; }
      public ConflictRule IfExists { get; init; } = ConflictRule.Ask;
      public FailureRule IfUnreadable { get; init; } = FailureRule.RetryThenSkipAndList;
      public bool RescueMode { get; init; }
      public bool FollowReparsePoints { get; init; }
      public long? SpeedLimitBytesPerSecond { get; init; }
      public IReadOnlyList<string> Exclude { get; init; } = [];
      public bool DryRun { get; init; }
      public HashAlgorithmKind HashAlgorithm { get; init; } = HashAlgorithmKind.XxHash3;
      public int BufferSizeBytes { get; init; } = 0;   // 0 = auto (set from Task 16 benchmarks)
      public bool CopyAlternateStreams { get; init; }  // NTFS extra data streams, opt-in
      public bool ExportChecksums { get; init; }       // writes <dest>\ferry-checksums.<algo> (one "hash  relative-path" line per file)
  }
  public enum ConflictRule { Ask, Overwrite, OverwriteIfNewer, Skip, Rename, KeepBoth }
  public enum FailureRule { RetryThenSkipAndList, Ask, Stop }
  public sealed record FileProgress(string RelativePath, long Done, long Total);
  public sealed record FileCopyOutcome(bool Success, byte[]? SourceHash, Problem? Problem);
  public sealed record Problem(string Path, IoErrorKind Kind, string Message);
  public sealed class FileCopier {
      public FileCopier(IFileSystem fs, BufferPool pool);
      public Task<FileCopyOutcome> CopyAsync(string source, string destination, CopyOptions options, PauseGate pause, IProgress<FileProgress> progress, CancellationToken ct);
  }
  ```

- [ ] **Step 1: Write failing tests** (InMemory + real temp):
  - `Copy_IsByteIdentical` for sizes 0, 1, 4095, 4096, 4097, 1 MiB, 64 MiB + 1
  - `Copy_WritesToPartFileThenRenames` (while copying, only `<name>.ferrypart` exists)
  - `Cancel_RemovesPartFile`
  - `Pause_StopsProgressUntilResume`
  - `Copy_PreservesTimesAndAttributes`
  - `Copy_ReturnsSourceHash`
  - `Copy_SourceModifiedDuringCopy_ReportsProblem` (`MutateDuringRead`; outcome `Success == false`, message "The file changed while it was being copied")
  - `SpeedLimit_IsRespected` (10 MiB at 5 MiB/s takes ≥ 1.8 s)
- [ ] **Step 2:** Run tests. Expected: FAIL.
- [ ] **Step 3:** Implement:
  - **Pipeline:** a reader loop fills pooled aligned buffers (`NativeMemory.AlignedAlloc`, alignment = sector size) into a bounded `Channel<Chunk>` (capacity 4). A writer loop drains it. Hashing happens on the reader side.
  - **Buffered vs unbuffered:** unbuffered for files ≥ 8 MiB, buffered below (tuned in Task 16).
  - **Unaligned tail:** write the aligned-up final chunk, then `SetLengthAsync(realLength)` via a buffered handle.
  - **Changed-source check:** compare source `Length`/`LastWriteTimeUtc` before and after.
  - **Finish:** rename the part file to the final name.
  - **Speed limit:** token bucket.
- [ ] **Step 4:** Run tests. Expected: PASS.
- [ ] **Step 5:** Commit `feat(ferry): pipelined single-file copier`.

### Task 13: Tree copy job (conflicts, failures, verify, dry run, moves)

**Files:**
- Create: `src/Ferry.Engine/{TreeCopier,Verifier,CopyResult}.cs`, `tests/Ferry.Engine.Tests/TreeCopierTests.cs`

**Interfaces:**
- Consumes: `TreeWalker`, `PreflightRunner`, `JobJournal`, `DriveAwareScheduler`, `FileCopier`
- Produces:
  ```csharp
  public enum CopyMode { Copy, Move }
  public sealed record JobProgress(long BytesDone, long BytesTotal, int FilesDone, int FilesTotal, double BytesPerSecond, TimeSpan? Remaining, string? CurrentFile, string Phase /* "Checking", "Copying", "Verifying", "Waiting" */);
  public sealed record CopyResult(int Copied, int Skipped, int Failed, IReadOnlyList<Problem> Problems, IReadOnlyList<PreflightIssue> PreflightIssues, bool AllVerified);
  public interface IConflictResolver { ValueTask<ConflictRule> ResolveAsync(FileEntry source, FileEntry existing, CancellationToken ct); }
  public sealed class TreeCopier {
      public TreeCopier(IFileSystem fs, DriveAwareScheduler scheduler, IConflictResolver resolver, string jobsDir);
      public Task<CopyResult> RunAsync(Guid jobId, IReadOnlyList<string> sources, string destination, CopyMode mode, CopyOptions options, PauseGate pause, IProgress<JobProgress> progress, CancellationToken ct);
      public Task<CopyResult> ResumeAsync(Guid jobId, PauseGate pause, IProgress<JobProgress> progress, CancellationToken ct);
  }
  public static class Verifier { public static Task<bool> MatchesAsync(IFileSystem fs, string destination, byte[] expectedHash, HashAlgorithmKind kind, CancellationToken ct); } // reads destination UNBUFFERED so the cache can't fake a match
  ```

- [ ] **Step 1: Write failing tests:**
  - `Run_CopiesTree_AllVerified`
  - `Conflict_*`: one test per `ConflictRule`, e.g. `Rename` yields `name (2).ext`, and `OverwriteIfNewer` keeps the newer one
  - `Failure_RetryThenSkip_ListsProblemAndContinues` (BadBlock on one file of 5 → 4 copied, 1 problem)
  - `Failure_Stop_StopsAndKeepsCompleted`
  - `Verify_CorruptedDestination_Detected` (flip a byte after write via the fake → `AllVerified == false`, problem listed)
  - `DryRun_WritesNothing_ReportsPlan`
  - `Move_SameVolume_UsesRenameNotCopy` (fake records zero `CreateWrite` calls)
  - `Move_CrossVolume_DeletesSourceOnlyAfterVerify`
  - `Copy_DiskFullMidFile_RemovesPartFileAndReports` (message names the destination drive and says there isn't enough space)
  - `Preflight_IssuesReturnedBeforeAnyWrite`
  - `Progress_Phases_InOrder`
  - `Resume_SkipsCompletedFiles`
  - `ExportChecksums_WritesOneLinePerFile_MatchingHashes`
  - `CopyPermissions_On_CopiesAcl` and `CopyPermissions_Off_InheritsDestinationAcl` (real temp dirs)
  - `AlternateStreams_OptIn_Copied`
  - `ReFS_SameVolume_UsesBlockClone` (`Category=Machine`; skipped when no ReFS volume)
  - `Run_PreventsSleepWhileActive` (seam: `ISleepBlocker` fake records `Acquire`/`Release`)
- [ ] **Step 2:** Run tests. Expected: FAIL.
- [ ] **Step 3:** Implement:
  - order: walk → preflight → scheduler acquire → journal create → copy files in walk order (small files first within each directory) → verify → finish
  - while a job runs, block sleep (not display-off) via `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)` behind `ISleepBlocker`
  - on ReFS, same-volume copies use `FSCTL_DUPLICATE_EXTENTS_TO_FILE` (block cloning), falling back to a normal copy on any error
  - ACLs via `FileSecurity`; when that needs privileges the user doesn't have, report "Needs administrator: restart this job as administrator" (the elevated worker itself comes in M2)
  - verify runs per file right after its copy, so the source is still in the page cache and the destination is read unbuffered
  - ETA uses a moving average over 10 s
  - **Move:** same volume → `IFileSystem.Move`; otherwise copy + verify, then delete source
- [ ] **Step 4:** Run tests. Expected: PASS.
- [ ] **Step 5:** Commit `feat(ferry): tree copy with conflicts, verify, dry run and moves`.

### Task 14: Rescue mode (failing drives) and network resilience

**Files:**
- Create: `src/Ferry.Engine/{RescueReader,RetryPolicy}.cs`, `tests/Ferry.Engine.Tests/{RescueReaderTests,RetryPolicyTests}.cs`
- Modify: `src/Ferry.Engine/FileCopier.cs` (use both)

**Interfaces:**
- Produces:
  ```csharp
  public sealed record LostRange(long Offset, int Length);
  public sealed class RescueReader { public RescueReader(IFileSystem fs, int sectorSize); public Task<IReadOnlyList<LostRange>> ReadChunkAsync(IReadHandle h, Memory<byte> buffer, long offset, CancellationToken ct); }
  public sealed class RetryPolicy { public RetryPolicy(int maxAttempts = 30, TimeSpan? maxWait = null /*default 5 min total*/); public Task<T> RunAsync<T>(Func<Task<T>> op, Func<IoErrorKind, bool> isTransient, IProgress<string>? status, CancellationToken ct); }
  ```

- [ ] **Step 1: Write failing tests:**
  - `Rescue_BadSectorInMiddle_RecoversRestAndReportsRange` (fake fails reads touching bytes 8192..12287 of a 1 MiB file → destination equals source except that range zero-filled; one `LostRange(8192, 4096)`)
  - `Rescue_Off_BadBlockFailsFile`
  - `Copy_NetworkDropsThenReturns_ResumesAndCompletes` (fake fails writes with NetworkLost 3 times then succeeds → file byte-identical, status shows "Waiting for the network drive…")
  - `Retry_GivesUpAfterMaxWait_ReportsProblem`
  - `Rescue_WritesLostReport` (`<dest>.ferry-lost.txt` lists ranges in plain words)
- [ ] **Step 2:** Run tests. Expected: FAIL.
- [ ] **Step 3:** Implement:
  - **Rescue:** on a BadBlock read, bisect the chunk down to `sectorSize`, retry each sector twice, zero-fill unreadable sectors, and record ranges. Rescue is used only when `CopyOptions.RescueMode` is on.
  - **Retry policy:** exponential backoff 1 s → 30 s cap. Transient = `NetworkLost` and `SharingViolation`.
- [ ] **Step 4:** Run tests. Expected: PASS.
- [ ] **Step 5:** Commit `feat(ferry): rescue mode and network retry`.

### Task 15: `ferry` CLI

**Files:**
- Create: `src/Ferry.Cli/*.cs`, `tests/Ferry.Cli.Tests/CliTests.cs`

**Interfaces:**
- Consumes: `TreeCopier`, `Verifier`, `AppPaths`, `LogSetup`
- Produces the command surface (later UI and helper call the same engine):
  ```
  ferry copy <source>... <destination> [--move] [--no-verify] [--if-exists ask|overwrite|newer|skip|rename|keep-both]
             [--if-unreadable retry-skip|ask|stop] [--rescue] [--limit 50MB/s] [--exclude <glob>]... [--dry-run] [--json]
  ferry resume <job-id> [--json]
  ferry verify <folderA> <folderB> [--json]
  ferry jobs                                   # history: id, date, source → dest, result
  ```
  `ExitCodes`: 0 success · 1 completed with problems · 2 preflight blocked · 3 cancelled · 4 invalid arguments · 10 internal error.
  `--if-exists ask` in a non-interactive console prompts "Replace / Skip / Keep both / Replace if newer, apply to all? [y/N]"; with `--json`, `ask` is rejected (exit 4).
  `--json` prints one JSON object per line: `{"type":"progress",...}`, `{"type":"problem",...}`, `{"type":"result",...}`.

- [ ] **Step 1: Write failing end-to-end tests** (run the built `ferry.exe` against temp folders):
  - `Cli_Copy_Succeeds_Exit0`
  - `Cli_JsonOutput_IsValidJsonLines`
  - `Cli_PreflightBlocked_Exit2_PlainMessage` (reserved name)
  - `Cli_InvalidArgs_Exit4`
  - `Cli_CtrlC_Exit3_NoPartFilesLeft`
  - `Cli_ResumeAfterKill_CompletesWithoutDuplicates` (start a 2,000-file copy, kill the process at ~50 %, `ferry resume <id>` → all files byte-identical, journal lists each once)
  - `Cli_Verify_ReportsDifferences`
- [ ] **Step 2:** Run tests. Expected: FAIL.
- [ ] **Step 3:** Implement with System.CommandLine. Ctrl+C triggers graceful cancel; a second Ctrl+C within 2 s exits immediately (part files are still cleaned up on next run via the journal). Console progress shows one updating line: `42% · 61.0 of 146.0 GB · 182 MB/s · about 8 min left`.
- [ ] **Step 4:** Run `./build.ps1`. Expected: all tests PASS.
- [ ] **Step 5:** Commit `feat(ferry): command line interface`.

### Task 16: Benchmarks, FastCopy comparison and tuning

**Files:**
- Create: `bench/Ferry.Bench/*`, `bench.ps1`, `bench.example.json`, `docs/perf/results.md`
- Modify: `src/Ferry.Engine/FileCopier.cs` (tuned constants only)

**Interfaces:**
- `bench.example.json`:
  ```json
  { "fastCopyExe": "C:\\path\\to\\FastCopy.exe",
    "sets": [ { "name": "hdd-to-nvme-large", "source": "X:\\bench\\large", "dest": "Y:\\bench-out" } ] }
  ```
  Real values go in the git-ignored `bench.local.json`.

- [ ] **Step 1:** `bench.ps1 [-Generate]`:
  - `-Generate` creates the datasets: large = 20 × 1 GiB random; small = 10,000 × 1–64 KiB; mixed = 50 % of each by bytes
  - for each set, run 3 rounds alternating FastCopy (`/cmd=force_copy /auto_close /no_ui`, then again with `/verify`) and `ferry copy` (with and without `--no-verify`), clearing the destination and flushing the standby cache between runs (`EmptyStandbyList` if available, else note it in the results)
  - output a median table plus ratio, written to `docs/perf/results.md` (drive models only, no letters or paths)
- [ ] **Step 2:** BenchmarkDotNet micro-benchmarks: `BufferPool` rent/return, hashing throughput (XxHash3 ≥ 10 GB/s on one core expected), and the `FileCopier` pipeline on a RAM disk or temp NVMe.
- [ ] **Step 3:** Run `./bench.ps1`. Tune buffer size (try 1, 4, 8, 16 MiB), channel depth (2, 4, 8), the unbuffered threshold (1, 8, 32 MiB) and small-file ordering. Keep the fastest combination that meets the gate on all four sets.
- [ ] **Step 4:** Expected: ratio ≤ 1.10 on every gated set, verify on and off. If not met, record the gap and the cause in `results.md` and **tell the user before moving to M2**.
- [ ] **Step 5:** Commit `perf(ferry): benchmarks and tuned defaults`.

### Task 17: Soak test and milestone security review

**Files:**
- Create: `soak.ps1`
- Modify: `docs/security/threat-model.md` (findings), `README.md` (CLI usage)

- [ ] **Step 1:** `soak.ps1 -Hours 4` loops copy → verify → move → delete-output over the bench datasets with random pause/resume and random cancel+resume. It logs memory (working set) and handle count of `ferry.exe` every 30 s. Pass: zero failed verifications, no growth trend in working set or handles over the last 3 hours (slope < 1 % per hour), no leftover `.ferrypart` files.
- [ ] **Step 2:** Run `soak.ps1 -Hours 4`. Expected: PASS summary line.
- [ ] **Step 3:** Run `/security-review` on the milestone diff and a security-auditor review (`comprehensive-review:security-auditor`) against `docs/security/threat-model.md`. Fix every finding, or record it as an accepted risk with a reason in the threat model.
- [ ] **Step 4:** Run `./build.ps1`. Expected: green. Open a PR to `main`; CI + CodeQL green; merge.
- [ ] **Step 5:** Tag `v0.1.0`. Expected: the release workflow publishes a signed CLI zip, SBOM and provenance attestation.

---

## Later milestones (each gets its own plan when it starts)

- **M2 — Ferry UI + Helper + Explorer:**
  - the WPF Fluent copy window per `docs/design/*ferry-copy-window*` (tabs, collapsed states, conflict prompt, cancel confirm), tray and main app window, and settings
  - the Helper (service + tray) with authenticated named pipes; alerts hub (Windows/ntfy/Home Assistant)
  - production Explorer integration per ADR 0001, including Ctrl+Shift+V quick paste and the right-click submenu
  - the elevated per-job copy worker
  - first step: a startup/memory measurement of WPF vs WinUI 3 → ADR 0002
- **M3 — Hullwatch** (drive health): SMART/NVMe via the service, event-log watcher, history, alerts, Home Assistant MQTT sensors, speed test.
- **M4 — Stowage** (duplicate finder): size → partial hash → full hash pipeline on the M1 core, similar-images mode, auto-select rules, recycle/move/hard-link with undo centre, Inventory component interfaces (§5.6).
- **M5 — v1.0 release:** Velopack installer with per-app toggles, self-update with pinned-cert check, portable zip, final soak + full security audit.
- **Then:** Broadside, followed by the remaining apps, each with its own design pass.
