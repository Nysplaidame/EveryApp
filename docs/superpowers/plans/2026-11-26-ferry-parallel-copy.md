# Ferry Parallel Copy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Ferry job copies several files at once when every drive involved is an SSD/NVMe, and checks each large file while the next one copies, without changing any result or safety rule.

**Architecture:** `TreeJob.ProcessRootAsync` stays the single dispatcher: it walks items in planned order and does every decision (resume checks, existence, conflict rule/question, free name) one file at a time, then hands the copy-and-after part (`CopyFileAsync`) to up to `Workers` concurrent tasks. Targets chosen for in-flight files are reserved so no two files aim at one name. Large files take turns for the transfer and for the check through two job-wide gates passed to `FileCopier` in its hooks.

**Tech Stack:** C# / .NET 10 (net10.0-windows), xUnit v3, CsWin32 (`NativeMethods.txt`), BenchmarkDotNet bench project.

**Spec:** `docs/superpowers/specs/2026-11-26-ferry-parallel-copy-design.md` (read it whole before Task 1; it is short).

## Global Constraints

- `./build.ps1` (locked restore, format check, build with TreatWarningsAsErrors, tests) passes after every task. CRLF line endings. Conventional Commits, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- No personal paths, drive letters or machine names in committed code or tests (test paths like `C:\src`, `D:\dst` on the in-memory fake are the existing convention and fine).
- Plain language in every user-facing string; new strings go in `CopyMessages`.
- Spec §14: no new trust boundary; part file → check → rename order, journal-before-part (R29), move deletes source only after a matching copy, links never followed, part must stay under `MustBeUnder`.
- Several workers ONLY when every physical disk of every source and the destination reports no seek penalty AND every path is a fixed local drive AND not rescue mode. Same-volume move roots always 1. Unknown → 1.
- No user-visible worker setting, CLI switch or environment variable.
- At `Workers = 1` the job must behave exactly as today (same order of operations, same journal order).
- Concurrency tests use deterministic gates (`TaskCompletionSource`), never sleeps or timing.
- Never stage `.agents/log.jsonl`; never commit `local/` or `bench.local.json`.

## Review Focus

1. **Cancel or "stop" rule while several copies run**: the job must not return, and the journal must not be disposed, while any copy task is still running; no file half-written under its real name. (Task 5 tests `Cancel_WithSeveralInFlight_…`, `StopRule_…`.)
2. **A worker throws something unexpected** (a bug, not an I/O error): the other copies are awaited, then the exception surfaces; nothing runs after the job ends. (Task 5 test `UnexpectedWorkerException_…`.)
3. **Name collisions on rename/keep-both rule** while the colliding file is still in flight: results identical to Workers 1. (Task 5 test `RenameCollision_…`.)
4. **Resume of a parallel job killed mid-way**: rechecks must not hash a file another worker is committing. (Task 5 rule "drain same-folder work before resume checks"; test `Resume_AfterKillWithSeveralInFlight_…`.)
5. **Speed limit with several workers**: job-wide total stays within the limit. (Task 2 test `SharedLimiter_FourWriters_StayWithinRate`.)

---

### Task 1: Drive kind probe (SSD or spinning)

**Files:**
- Create: `src/EveryApp.Core/Scheduling/DriveKind.cs`, `src/EveryApp.Core/Scheduling/WindowsDriveKindProbe.cs`
- Modify: `src/EveryApp.Core/Scheduling/IVolumeApi.cs`, `src/EveryApp.Core/Scheduling/NativeVolumeApi.cs`, `src/EveryApp.Core/NativeMethods.txt`
- Test: `tests/EveryApp.Core.Tests/Scheduling/DriveKindProbeTests.cs` (and the existing fake `IVolumeApi` in that folder gains the new member)

**Interfaces:**
- Produces:
  - `public enum DriveKind { Unknown, SolidState, Spinning }`
  - `public interface IDriveKindProbe { DriveKind KindOf(DeviceSet devices); }`
  - `public sealed class WindowsDriveKindProbe : IDriveKindProbe` with `public static WindowsDriveKindProbe Instance` and `internal WindowsDriveKindProbe(IVolumeApi api)`.
  - `IVolumeApi.HasSeekPenalty(uint diskNumber)` → `bool?` (null when the query fails).

Rule: `KindOf` returns `SolidState` only if the set is non-empty, every id is `disk:N`, and every disk's `HasSeekPenalty` is `false`; `Spinning` if any disk says `true`; otherwise `Unknown` (any `smb:`, `vol:`, `unresolved:` id, or any null answer). Answers cached per disk number in a `ConcurrentDictionary` (null answers are not cached).

- [ ] **Step 1: Write the failing tests** in `DriveKindProbeTests`:
  - `AllDisksNoSeekPenalty_IsSolidState` — fake says disk 0 and 1 → false; `KindOf(DeviceSet.Of("disk:0","disk:1"))` == `SolidState`.
  - `AnyDiskSpinning_IsSpinning` — disk 1 → true ⇒ `Spinning`.
  - `QueryFails_IsUnknown` — disk 1 → null ⇒ `Unknown`.
  - `NetworkOrVolumeId_IsUnknown` — `smb:server`, `vol:{…}`, `unresolved:x` ⇒ `Unknown`.
  - `Answers_AreCachedPerDisk` — two calls, fake query count for disk 0 == 1.
  - `RealSystemDrive_DoesNotThrow` — `WindowsDriveKindProbe.Instance.KindOf(new WindowsDeviceResolver().Resolve(Environment.SystemDirectory))` is any defined enum value.
  (Check how `DeviceSet` exposes its ids and build sets the same way existing scheduling tests do.)
- [ ] **Step 2: Run** `dotnet test tests/EveryApp.Core.Tests --filter DriveKindProbeTests` — expect compile failure.
- [ ] **Step 3: Implement.** `NativeVolumeApi.HasSeekPenalty`: open `\\.\PhysicalDrive{n}` with access 0 and full share (same pattern as `GetDiskNumbers`), `DeviceIoControl(IOCTL_STORAGE_QUERY_PROPERTY)` with `STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceSeekPenaltyProperty, QueryType = PropertyStandardQuery }`, read `DEVICE_SEEK_PENALTY_DESCRIPTOR.IncursSeekPenalty`; any failure → null. Add `IOCTL_STORAGE_QUERY_PROPERTY`, `STORAGE_PROPERTY_QUERY`, `DEVICE_SEEK_PENALTY_DESCRIPTOR` to `NativeMethods.txt`. Never throws.
- [ ] **Step 4: Run** the tests — PASS. Then `./build.ps1 -SkipTests` for format and warnings.
- [ ] **Step 5: Commit** `feat(core): tell SSDs from spinning drives for the copy engine`.

### Task 2: Job state safe for several copies at once

**Files:**
- Modify: `src/Ferry.Engine/JobOutcome.cs`, `src/Ferry.Engine/ChecksumWriter.cs`, `src/Ferry.Engine/SpeedLimiter.cs`, `src/Ferry.Engine/CopyProgress.cs` (hooks), `src/Ferry.Engine/FileCopier.cs` (use shared limiter), `src/Ferry.Engine/TreeJob.cs`, `src/Ferry.Engine/TreeMover.cs` (call sites only)
- Test: `tests/Ferry.Engine.Tests/JobOutcomeTests.cs` (new), `tests/Ferry.Engine.Tests/TreeCopierOptionsTests.cs`, `tests/Ferry.Engine.Tests/FileCopierTests.cs`

**Interfaces:**
- Produces:
  - `JobOutcome`: every mutation thread-safe under one private lock. Replace the public setters used as `x++` with methods: `AddCopied()`, `AddSkipped()`, `AddFailed(int n = 1)`, `AddVerified()`, `MarkVerifyFailed()`, `MarkStopped()`; getters stay (`Copied`, `Skipped`, `Failed`, `Verified`, `VerifyFailed`, `Stopped` read under the lock / `Volatile`). `ProblemList.Add/AddRange/Items/Count/More` thread-safe. Permission helpers already funnel through `Problems` and their `_warned` flags move under the lock.
  - `ChecksumWriter.Add` thread-safe; `Write` emits lines sorted by target key, `StringComparer.OrdinalIgnoreCase`, then ordinal as tiebreak.
  - `SpeedLimiter.WaitAsync` thread-safe (lock around `_due` update); `FileCopyHooks.SharedLimiter` (`SpeedLimiter?`, init): when set, `FileCopier` uses it instead of creating its own from `options.SpeedLimitBytesPerSecond`.
  - `TreeJob` creates one limiter per job when `SpeedLimitBytesPerSecond` is set and passes it in hooks.

- [ ] **Step 1: Write the failing tests:**
  - `JobOutcomeTests.ConcurrentAdds_AreAllCounted` — 8 tasks × 10,000 `AddCopied()` ⇒ `Copied == 80_000`; same for `Problems.Add` up to max (Count == 80_000, `Items.Count == DefaultMaxProblems` capped, `More` the rest).
  - `TreeCopierOptionsTests.ExportChecksums_LinesSortedByPath` — files `C:\src\b.txt`, `C:\src\A\z.txt`, `C:\src\a.txt` ⇒ lines' paths in order `A\z.txt`, `a.txt`, `b.txt` per the comparer above (adjust any existing test that asserted finish order).
  - `FileCopierTests.SharedLimiter_FourWriters_StayWithinRate` — fake time; one `SpeedLimiter(1 MiB/s)` shared by four concurrent `WaitAsync(256 KiB)` loops totalling 8 MiB ⇒ last due time ≥ 8 s (with the 1 ms tolerance).
- [ ] **Step 2: Run** `dotnet test tests/Ferry.Engine.Tests --filter "JobOutcomeTests|ExportChecksums_LinesSortedByPath|SharedLimiter"` — fail.
- [ ] **Step 3: Implement** as in Interfaces; update every call site (`_outcome.Copied++` → `_outcome.AddCopied()`, etc.). No behaviour change otherwise.
- [ ] **Step 4: Run** `./build.ps1` — all pass.
- [ ] **Step 5: Commit** `refactor(ferry): make job totals, checksums and speed limit safe to share`.

### Task 3: Progress with several files in flight

**Files:**
- Modify: `src/Ferry.Engine/ProgressTracker.cs`, `src/Ferry.Engine/CopyResult.cs` (`JobProgress`), `src/Ferry.Engine/TreeJob.cs`, `src/Ferry.Engine/TreeMover.cs` (call sites)
- Test: `tests/Ferry.Engine.Tests/TreeCopierProgressTests.cs`, new `tests/Ferry.Engine.Tests/ProgressTrackerTests.cs`

**Interfaces:**
- Produces:
  - `JobProgress` gains `public int InFlight { get; init; }` (init property, not positional: the CLI's construction is unaffected).
  - `ProgressTracker.StartFile(string key)` returns `FileSlot`; `internal sealed class FileSlot` (nested or same file) with `IProgress<FileProgress> Bytes`, `void Verifying()`, `void Reset()` (retry: this file's bytes to 0), `void End(long length)` (adds `length` to done, removes the slot; idempotent).
  - Snapshot: `BytesDone = finished + Σ in-flight bytes`; `CurrentFile` = key of the most recently started slot still in flight (null when none); `InFlight` = slot count; `Phase` = `"Verifying"` when at least one slot is in flight and every in-flight slot has called `Verifying()`, otherwise the last phase set with `SetPhase` (Status lines from `hooks.Status` still go through `SetPhase`).
  - `ProgressTracker.FileSink()`, `ResetCurrentFile()`, `EndFile()` are removed; `CompleteFiles` stays.
- `TreeJob` holds the slot per file and passes `slot.Bytes` / `slot.Verifying` / `slot.Reset` into the copier hooks; the per-file `_progress.SetPhase(CopyMessages.Copying)` after each copy is removed (phase is now derived).

- [ ] **Step 1: Write the failing tests** (`ProgressTrackerTests`, fake time and a list sink):
  - `TwoSlots_BytesAddUp` — slot a reports 100, b 50 ⇒ last `BytesDone == 150`, `InFlight == 2`, `CurrentFile == "b"`.
  - `EndOne_CurrentFileFallsBackToOneStillRunning` — end b ⇒ `CurrentFile == "a"`, `BytesDone == b.length + 100`.
  - `Reset_OnlyClearsThatSlot` — a 100, b 50, a.Reset() ⇒ `BytesDone == 50`.
  - `Phase_VerifyingOnlyWhenAllInFlightVerify` — a.Verifying() ⇒ phase "Copying"; b.Verifying() ⇒ "Verifying"; end a and b ⇒ "Copying".
  - `End_IsIdempotent` — end twice ⇒ `FilesDone` counts once.
  - Existing `TreeCopierProgressTests` keep passing (adjust only calls, not expectations).
- [ ] **Step 2: Run** `dotnet test tests/Ferry.Engine.Tests --filter "ProgressTracker|TreeCopierProgress"` — fail.
- [ ] **Step 3: Implement** with one lock; keyed by slot object, not by key string (two attempts or same key must not collide).
- [ ] **Step 4: Run** `./build.ps1` — pass.
- [ ] **Step 5: Commit** `refactor(ferry): track progress per file so several can run at once`.

### Task 4: Large-file gates in the copier (check behind the copy)

**Files:**
- Create: `src/Ferry.Engine/LargeFileGates.cs`
- Modify: `src/Ferry.Engine/CopyProgress.cs` (hooks), `src/Ferry.Engine/FileCopier.cs`
- Test: `tests/Ferry.Engine.Tests/FileCopierTests.cs`

**Interfaces:**
- Produces: `internal sealed class LargeFileGates : IDisposable` with `SemaphoreSlim Transfer` and `SemaphoreSlim Check` (both `(1, 1)`); `FileCopyHooks.LargeGates` (`LargeFileGates?`, init).
- Rule in `FileCopier.CopyAsync`, only for the normal unbuffered path (`length >= UnbufferedThresholdBytes`, not block clone, not rescue): `await Transfer.WaitAsync(ct)` before the part file is created; release it right after the part is flushed and closed (before `VerifyClosedPartAsync`); `await Check.WaitAsync(ct)` around `VerifyClosedPartAsync` only. Each is released exactly once on every path (success, failure, exception, cancel) — use `try/finally` with a "held" flag. Small files and `LargeGates == null` do nothing new.

- [ ] **Step 1: Write the failing tests** (in-memory fs; threshold via the internal `CopierTuning` constructor set to e.g. 64 KiB so "large" files are small in tests; a test write/read hook to hold operations — add a `Func<string, ValueTask>? BeforeWrite` and `BeforeReadBack`/`BeforeOpenRead` hook to the existing recording fake if none exists):
  - `LargeGates_SecondTransferWaitsForFirst` — two large copies sharing gates; hold A's write ⇒ B has not created its part (`PartChosen` not called for B); release ⇒ both succeed.
  - `LargeGates_CheckOverlapsNextTransfer` — hold A's check read ⇒ B's transfer completes (B's part written) while A is still checking.
  - `LargeGates_OnlyOneCheckAtATime` — hold A's check ⇒ B reaches its check and waits (B's check read not started) until A is released.
  - `LargeGates_ReleasedOnFailureAndCancel` — A fails mid-write (fault injection) ⇒ B still completes; cancel A while holding Transfer ⇒ `Transfer.CurrentCount == 1` afterwards.
  - `SmallFiles_IgnoreGates` — gates held by test (`Transfer.Wait()` taken) ⇒ a small copy still completes.
- [ ] **Step 2: Run** `dotnet test tests/Ferry.Engine.Tests --filter "LargeGates|SmallFiles_IgnoreGates"` — fail.
- [ ] **Step 3: Implement** per the rule.
- [ ] **Step 4: Run** `./build.ps1` — pass.
- [ ] **Step 5: Commit** `feat(ferry): large files take turns to copy and to check, so a check can run behind the next copy`.

### Task 5: Decide in order, copy in parallel (TreeJob)

**Files:**
- Modify: `src/Ferry.Engine/TreeJob.cs`, `src/Ferry.Engine/TreeCopier.cs`, `src/Ferry.Engine/NameAllocator.cs`
- Test: new `tests/Ferry.Engine.Tests/TreeCopierParallelTests.cs`; `tests/Ferry.Engine.Tests/TreeCopierTestRig.cs`

**Interfaces:**
- Consumes: Task 1 `IDriveKindProbe`/`DriveKind`; Task 2 `JobOutcome` methods, `SharedLimiter`; Task 3 `FileSlot`; Task 4 `LargeFileGates`.
- Produces:
  - `TreeCopier`: `internal const int DefaultWorkers = 4;` (Task 7 may change it); `internal IDriveKindProbe DriveKinds { get; }`; `internal int Workers { get; }`. The internal test constructor gains trailing optional parameters `IDriveKindProbe? driveKinds = null, int workers = DefaultWorkers`; public constructors use `WindowsDriveKindProbe.Instance` and `DefaultWorkers`. Update the class doc paragraph "Files are processed one at a time…" to describe the new rule.
  - `NameAllocator.NextFreeName(IFileSystem fs, string target, Func<string, bool>? reserved = null)` — a candidate is taken if on disk or `reserved(candidate)`.
  - Rig: `TreeCopierRig.DriveKinds` (`FakeDriveKindProbe { DriveKind Kind = SolidState }`) and `TreeCopierRig.Workers` (default `TreeCopier.DefaultWorkers`), passed by `NewCopier()`.

Job-level decision (once, after `DevicesOf`): `workers = Workers` if `DriveKinds.KindOf(devices) == SolidState` and `DestinationInfo.IsFixedLocalDrive` is true for the destination and every source and `!options.RescueMode`; else 1. Per root: `root.SameVolume` ⇒ 1 for that root.

Split `ProcessFileAsync`:
- **Dispatcher part** (runs inline, in planned order): done-already check; pause wait; `StartFile`; resume checks (`TryRecoverAsync`, `RecheckAsync`) — **before them, await every in-flight file whose target is in the same destination folder**; existence check — if the natural target is reserved, await that file's task, then check; conflict rule/question; `NextFreeName(..., reserved)`; same-volume move. Every non-copy outcome ends the slot there.
- **Worker part**: `CopyFileAsync(root, item, target, targetKey, replace, slot, clone, localDrives)` — values passed as arguments, never read from `_clone`/`_localDrives`/`_sameVolume`. Its `finally` ends the slot and releases the reservation.
- Reservations: `Dictionary<string, Task>` keyed by full target path, `StringComparer.OrdinalIgnoreCase`, touched only by the dispatcher (remove on completion via the dispatcher's own bookkeeping or under a lock — implementer's call, but it must be safe).
- Slots: `SemaphoreSlim(workers)` taken before starting a worker, released in its `finally`. With `workers == 1` the dispatcher awaits the copy inline (exact current order).
- Stop/cancel/exception: dispatcher checks `_outcome.Stopped` and `_ct` before each item; on any worker exception other than `OperationCanceledException`, it marks stopped, stops dispatching, awaits all, then rethrows the first. Every root ends with `await` of all in-flight tasks (inside `try/finally` so cancellation also waits); `FinishFolders`, checksums, `JobFinished` and journal disposal only run after that.
- One `LargeFileGates` per job, passed in hooks.

- [ ] **Step 1: Write the failing tests** in `TreeCopierParallelTests` (use a hold-able write hook as in Task 4; rig `Workers = 4`, `DriveKinds.Kind = SolidState` unless stated):
  - `SolidState_CopiesSeveralAtOnce` — 6 small files, hold all writes ⇒ 4 writes waiting, none of the other 2 started; release ⇒ all copied, `Copied == 6`.
  - `Spinning_OneAtATime` / `Unknown_OneAtATime` / `NotFixedLocal_OneAtATime` / `Rescue_OneAtATime` — hold first write ⇒ no second part created.
  - `SameResults_Workers1And4` (theory over copy, copy+verify, move, ExportChecksums, conflict rule Rename with pre-existing targets) — identical destination tree (names + bytes), `Copied/Skipped/Failed/Verified/AllVerified`, checksum file text.
  - `RenameCollision_MatchesSequential` — dest has `a.txt`; sources `a.txt` and `a (2).txt`; rule Rename; hold `a.txt`'s write ⇒ destination names, bytes and totals equal a run of a second rig with `Workers = 1`, and no two copies ever wrote the same part target.
  - `StopRule_NoNewFilesAfterFailure_InFlightFinish` — `IfUnreadable = Stop`, file 2 of 8 fails on read, files 1,3,4 held ⇒ after release: `Stopped`, files 1,3,4 copied, files 5–8 never opened (recording log), job returned after all.
  - `Cancel_WithSeveralInFlight_NoPartsLeftAndResumable` — cancel while 4 held ⇒ `OperationCanceledException`, `PartFiles(dst)` empty, no real name with partial data, `ResumeAsync` completes with all files equal to source.
  - `UnexpectedWorkerException_AwaitsOthersThenThrows` — fault fs throws `InvalidOperationException` for one file's write while others are held ⇒ job task not completed until the held ones are released; then throws that exception; journal file closed (can be opened for write by the test).
  - `ConflictAsk_OneQuestionAtATime_NoNewStartWhileOpen` — `ScriptedResolver` that blocks on a TCS; while blocked, no write of a later file has started; questions asked in planned order.
  - `Resume_AfterKillWithSeveralInFlight_CompletesWithSameFiles` — reuse the existing kill/resume pattern from `TreeCopierResumeTests` at Workers 4.
  - `JournalAndFolders_FinishAfterAllCopies` — held last file ⇒ folder dates not yet applied and no `JobFinished` record until released.
- [ ] **Step 2: Run** `dotnet test tests/Ferry.Engine.Tests --filter TreeCopierParallelTests` — fail.
- [ ] **Step 3: Implement** the decision, split, reservations, slots and shutdown rules above.
- [ ] **Step 4: Run** `./build.ps1`. The whole existing suite now runs with rig defaults Workers 4 + SolidState. Any existing test that depends on strict operation order (recording-log order, progress sequence) sets `_rig.Workers = 1` with a one-line comment saying why; no expectation is loosened otherwise. Run the suite 3 times (`dotnet test tests/Ferry.Engine.Tests` ×3) to catch flakiness — all green each time.
- [ ] **Step 5: Commit** `feat(ferry): copy several files at once on SSDs, decide names in order`.

### Task 6: Real-file check, security review, threat model

**Files:**
- Modify: `tests/Ferry.Engine.Tests/TreeCopierRealFileTests.cs`, `docs/security/threat-model.md`
- Test: as above

- [ ] **Step 1: Write the tests** in `TreeCopierRealFileTests` (real temp folder, `WindowsFileSystem`, forced `SolidState` probe, Workers 4): `Parallel_ManySmallAndSomeLarge_VerifyOn_AllMatch` — 300 files of 1–64 KiB + 3 files of 12 MiB, verify on ⇒ every destination file byte-equal, `AllVerified`, no `*.ferrypart` left; and the same with `CopyMode.Move` ⇒ sources gone, destinations equal.
- [ ] **Step 2: Run** them — PASS (they guard behaviour already built; if one fails, fix the code in Task 5's files).
- [ ] **Step 3: Security review.** Run `/security-review` on the branch diff (trust boundary: copy/move/delete logic). Fix every finding or record it as an accepted risk with a reason. Add to `docs/security/threat-model.md`: Engine rows for "parallel copies: target reservation, decisions in planned order, job waits for all copies before closing its journal", "shared speed limit", and a 2026-11-26 review-log row.
- [ ] **Step 4: Run** `./build.ps1` — pass.
- [ ] **Step 5: Commit** `test(ferry): real-disk parallel copy and move; security review of parallel copies`.

### Task 7: Pick the worker count, re-measure, record

**Files:**
- Modify: `bench/Ferry.Bench/Program.cs`, create `bench/Ferry.Bench/JobsProbe.cs`; `src/Ferry.Engine/TreeCopier.cs` (`DefaultWorkers` only if the probe says so); `docs/perf/results.md`; `STATE.md`; `HANDOFF.md`

**Interfaces:**
- Consumes: Task 5 internal `TreeCopier` constructor (`workers`, `driveKinds`).
- Produces: `Ferry.Bench jobs <source> <destRoot> [--workers 1,2,4,8] [--verify]` — for each worker count, one run of a whole copy job through `TreeCopier` (real `WindowsFileSystem`, `WindowsDeviceResolver`, real probe), into a fresh `.ferry-bench-out`-marked subfolder of `destRoot` (reuse `Sweep.Clear` guards), printing seconds per count. Refuses to run elevated (the engine refuses anyway; say so plainly).

- [ ] **Step 1: Implement** `JobsProbe` and wire `jobs` in `Program.cs`. Smoke-test on a 200-file temp folder: prints four timings.
- [ ] **Step 2: Probe (asks the user first: writes ~1.3 GB):** small set only, verify off and on, workers 1/2/4/8, one round each. Set `DefaultWorkers` to the fastest count (ties → the smaller). Note the numbers.
- [ ] **Step 3: Gate run (asks the user first: writes ~70 GB):** `./bench.ps1 -Only 'nvme-to-nvme-l*'` then `./bench.ps1 -Only 'nvme-to-nvme-m*'` (`-Only` is one wildcard; set names as in the local `bench.local.json`; 3 rounds, verify on and off).
- [ ] **Step 4: Record** in `docs/perf/results.md`: a "Parallel copies (2026-11-2x)" section with the probe table, the new gate table and ratios, what changed, and what is still open (HDD sets). Update `STATE.md` START HERE and add a `HANDOFF.md` entry with a `Touches:` line.
- [ ] **Step 5: Run** `./build.ps1`, then commit `perf(ferry): pick worker count from measurements; parallel copy results`.
