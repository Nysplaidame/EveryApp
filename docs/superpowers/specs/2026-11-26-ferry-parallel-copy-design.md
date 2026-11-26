# Ferry: several files at once, and checking large files behind the copy

Date: 2026-11-26. Status: draft for the user's approval. Follows task 16 (`docs/perf/results.md`).

## Why

The release gate says Ferry must be within 10% of FastCopy (ratio ≤ 1.10) on four sets. Task 16 measured on NVMe → NVMe:

| Set | Verify off | Verify on |
|---|---|---|
| Large, 20 × 1 GiB | 0.52 (pass) | 1.17 |
| Mixed, 64,602 files | 2.02 | 7.61 |

Two causes are left: every small file waits for the one before it (per-file work of about 0.8 ms more than FastCopy, plus several ms of drive time per file when verify is on), and a large file is read back for its check before the next large file starts. FastCopy hides both by keeping several files in flight and checking behind the copy. This change does the same, in one job, without weakening any safety rule.

## Goals

- Copy several files of one job at the same time when both ends are SSDs or NVMe drives.
- Check (verify) a large file in the background while the next large file copies.
- Bring the NVMe mixed and large-verify sets to ≤ 1.10 × FastCopy, or as close as these two changes get, measured with one short run.
- Keep every existing guarantee: part files and rename, journal-before-part (R29), verify before the real name appears, a move deletes its source only after a matching copy, resume by file name, links never followed, paths stay inside the destination.

## Non-goals

- Two lanes (small and large files in separate streams): considered and rejected (below).
- Several files at once on spinning drives, network shares, removable drives or in rescue mode. They stay one at a time until measured.
- Trimming per-file work (fewer opens and checks per file): a later option if the gate is still missed.
- Several jobs on the same drive at once: the drive-aware scheduler is unchanged.
- UI for the in-flight list: M2. This change only makes the engine report it.

## Anti-goals (a failure even if it is faster)

- Any new way to lose or corrupt data, to leave a file half-written under its real name, or to delete a source that was not verified.
- A job that hangs, or finishes while copies are still running (journal closed under them, totals wrong).
- Slower copies on a spinning drive.
- Nondeterministic results: the same tree must give the same files, names and totals at any worker count. Only the order of journal records and of problems may differ.
- Flaky tests.

## Constraints

- Spec: "jobs that share a physical source or destination device run one at a time" (line 77); "buffer sizes, thread counts, and scheduler heuristics, chosen from benchmarks" (line 285). §14 security on every task. Stability and speed over features.
- Drive health: re-measure with as few writes as possible (one run of the gated NVMe sets, plus a short worker-count probe on the small set).
- `build.ps1` exactly as CI; no personal paths or drive letters in committed code or tests.

## Approaches considered

1. **Several workers in the job (chosen by the user).** The job loop hands files to a fixed number of workers. Large files take turns for the copy itself (one at a time), and each one's check runs in the background while the next one copies, at most one check behind. Smallest change to how jobs work today; the worker count is one number to tune.
2. **Two lanes at once.** Small files in parallel in one lane, large files with check-behind in another, both running together. A little more overlap, but both lanes compete for the destination drive (the test destination has no DRAM cache and already struggles with many small writes), and stop, pause and progress need about twice the code.
3. **Batch small files per folder.** Dropped: saves less than either and still needs the same stop and progress changes.

## Design

### When the job uses several workers

The job decides once, before copying, from the drives of every source root and the destination:

- **Several workers** only when every drive is a fixed local drive (`IDestinationInfo.IsFixedLocalDrive`) and Windows reports **no seek penalty** for every physical disk under it (`IOCTL_STORAGE_QUERY_PROPERTY` with `StorageDeviceSeekPenaltyProperty`, through the disk numbers the device resolver already reads).
- **One at a time** (exactly as today) when any drive is a spinning disk, the answer is unknown or the query fails, any end is a network share or removable drive, the job is in rescue mode, or the job is a same-volume move (renames; nothing to gain).
- The answer is cached per physical disk for the process (the drive type does not change while it runs). The query must never touch the network and never block the UI thread (the engine runs off it already).

New seam: `IDriveKind` (or a method on `IDestinationInfo`) returning `SolidState`, `Spinning` or `Unknown` for a path, so tests can force either mode without real drives.

### Worker count

An internal job-level setting `Workers` (default chosen by a short benchmark of 1, 2, 4, 8 on the small set; expected 4), used only in the several-workers mode above. One at a time is `Workers = 1` and must run exactly today's order. `CopierTuning` is internal to the copier, so `TreeCopier` gets its own internal constructor parameter (like `FileCopier`'s tuning seam). **No user-visible setting, CLI switch or environment variable** (Claude's call: no new public surface, and nothing that could turn on several workers for a spinning drive). The probe is a new `Ferry.Bench jobs` mode that runs whole jobs through `TreeCopier` with Workers 1/2/4/8 (internals are already visible to the bench project); the gated comparison still runs the real `ferry copy` with the chosen default.

### The job loop

`ProcessRootAsync` stays the single **dispatcher**: it walks `root.Items` in planned order as today and does everything that is not a file copy itself, synchronously, before moving on: folders (create, skip, move-by-rename), links, the skipped-folder set. Because a folder is handled before any later item is dispatched, a file's folder always exists before its copy starts.

For a file it waits for a free worker slot (a `SemaphoreSlim(Workers)`), then starts `ProcessFileAsync` without awaiting it and tracks the task. Before each dispatch it checks, as today: `_outcome.Stopped`, cancellation, pause. At the end of each root, and before anything that needs all copies finished (folder dates, checksums file, `JobFinished` record, journal close), it waits for **all** in-flight files.

**Stop and failure.** When the job is stopped (failure rule "stop") or cancelled, the dispatcher starts no new file; files already in flight finish (on "stop") or are cancelled (on cancel) through their normal clean-up (part deleted, nothing under the real name). The job does not return, and the journal is not disposed, until every in-flight task has ended. An exception from one worker (other than cancellation) is captured, the rest are awaited, then it is rethrown: the job never leaves a copy running behind it.

**Pause.** Unchanged: the dispatcher waits at the pause gate before dispatching, and each copy already waits inside the copier.

### Names: decided in planned order, and two files never aim at the same target

Today the decision "does the target exist, what does the conflict rule say, which free name" happens one file at a time, in planned order. That stays exactly so: **everything before the copy itself runs in the dispatcher, one file at a time, in planned order** (Claude's call): the "done already" check, the resume checks (`TryRecoverAsync`, `RecheckAsync`), the existence check, the conflict rule and question, and `NameAllocator.NextFreeName`. Only the copy and what follows it (`CopyFileAsync`: copy, check, streams, rename, dates, permissions, move's source delete, journal done) runs in a worker. So names and totals are the same at any worker count, on resume too. Cost: a resume rechecks its files one at a time (resume is rare; correctness wins).

Because a target chosen for an in-flight file is not on disk yet, the job also keeps a set of **reserved target keys** (chosen, not yet committed, failed or abandoned):

- `NextFreeName` treats reserved names as taken.
- If a file's natural target (or a resume recheck target) is reserved by an in-flight file, the dispatcher waits for that file to finish, then decides as a sequential run would (it now sees the file as existing). This only happens in rare name collisions (for example incoming `a.txt` renamed to `a (2).txt` while the source also has its own `a (2).txt`).
- The reservation is released when that file's worker ends, whatever the outcome.
- On a resume, before a file's resume checks (which read files already in the destination folder), the dispatcher waits for every in-flight file whose target is in that same folder, so a check never reads a file another worker is still committing.

### Conflict questions

Asking the user (`IConflictResolver`, rule "ask") happens in the dispatcher, so at most one question is open at a time and answers such as "apply to all" (M2) take effect before the next file decides. While a question is open, no new file starts; copies already running finish.

### Per-root and per-file values

`_sameVolume`, `_clone` and `_localDrives` are fields set per root today, and `_sameVolume` is flipped to false inside `ProcessFileAsync` when a rename is refused as crossing volumes. Same-volume roots always run one at a time (see "When the job uses several workers"), so the flip stays in the dispatcher's sequential path; values a worker needs (`clone`, `localDrives`, the root) are passed into the worker as arguments, never read from fields that can change while it runs.

### Shared job state

Everything workers touch together becomes safe to share:

- `JobOutcome` counters and `ProblemList`: one lock (`Copied`, `Skipped`, `Failed`, `Verified`, `VerifyFailed`, `Stopped`, permission warnings).
- `ChecksumWriter.Add`: under a lock; the checksum file is written **sorted by path**, so it is the same at any worker count. Today it is in the order files finished, so this changes the file at Workers 1 too; any test that checks line order is updated.
- Journal: already locked. Record order may differ from planned order; resume reads by key, which is unaffected.
- **Speed limit:** today each file copy makes its own `SpeedLimiter`, so N workers would go N times over the limit. The job makes one thread-safe limiter and every copy in it shares it (`CopyOptions`/hooks carry it).
- Retry delays and the retry loop stay per file.

### Progress

`ProgressTracker` tracks a set of in-flight files (key → bytes so far) instead of one current file. Bytes done = finished bytes + the sum of in-flight bytes. `JobProgress.CurrentFile` stays (the most recently started file still running, so the CLI is unchanged) and gains `InFlight` (count) for M2. The phase shown is "Copying" while any copy runs; "Verifying" only when every in-flight file is verifying. Retrying a file resets only that file's bytes.

### Large files: check behind the copy

A file is "large" when it takes the unbuffered path (`length >= UnbufferedThresholdBytes`, 8 MiB), which is also where the check reopens the closed part (`VerifyClosedPartAsync`).

- A job-wide **large-copy gate** (one at a time) is held only while a large file's data is being transferred. The copier releases it through a new hook (`TransferDone`) once the data is written, flushed and closed, before its check starts; a retry takes it again.
- A job-wide **large-check gate** (one at a time) is held while a large file is read back. So at most one large file is being checked while the next one copies, and a third waits.
- Small files are not gated: they keep using the free worker slots while a large file copies or checks.
- Order inside one file is unchanged: copy → check → streams → rename → dates → permissions → (move) delete source → journal done. Nothing about a file's real name changes until its own check passes.
- With one worker the gates change nothing (today's behaviour).

### What the copier must give

- `FileCopyHooks.TransferDone` (called once per attempt, after the part is closed and before its check), used by the job to release the large-copy gate; and the large-check gate is taken by the job around the `Verifying` call (or the copier takes an optional gate object). The builder picks the simpler of these; the rule is the two gates above.
- The shared speed limiter, as above.

## Tests (must exist before the code that passes them)

- Worker mode decision: SSD/SSD → several; any spinning, unknown, network, removable, rescue, same-volume move → one.
- Same tree, Workers 1 vs 4 (in-memory and real temp folder): identical destination files, names, totals, `AllVerified`, checksum file.
- Rename collision: incoming `a.txt` (rename rule) and source `a (2).txt` in flight together never share a target; result matches the sequential run.
- Stop rule with a failing file while others are in flight: no new files start, in-flight ones finish, job returns only after all end, journal intact.
- Cancel mid-job with several in flight: every part deleted, no file under its real name half-written, resume completes the job.
- Kill/resume with several in flight (existing kill tests, run at Workers 4).
- Speed limit at Workers 4 stays within the limit (fake time).
- Large-file gates: with a fake copier, at most one large transfer and one large check at once, and a check overlaps the next transfer.
- Progress: bytes done never go down or over total with several files in flight; `CurrentFile` is a file still in flight.
- Conflict "ask": one question at a time; no new file starts while one is open.
- Existing suite passes unchanged at the default and at Workers 1. Use deterministic gates (TaskCompletionSource) instead of timing in concurrency tests.

## Security (§14, trust boundary: file copy/move/delete logic)

No new input, process boundary or privilege. Risks introduced: target races between workers (covered by reservation), a source deleted before its own copy verified (unchanged per-file order; tested), work running after the journal closes (job awaits all; tested), speed limit bypass (shared limiter). Threat-model rows for the engine get a line on parallel copies and target reservation. `/security-review` on the branch before merge.

## Measuring

After build and review: a short worker-count probe (small set only, 1/2/4/8, one round each) to pick the default, then one `bench.ps1` run of the two NVMe gated sets, verify on and off, 3 rounds. Update `docs/perf/results.md` with the numbers behind the default. HDD sets stay open until the HDD is back.

## Left to the builder

- Exact names of the new types and hooks; whether the gates live in `TreeJob` or a small helper class.
- How the seek-penalty query is wired into `NativeVolumeApi` and cached.

## Calls made by Claude (for the user to check)

- Unknown drive type, network shares and removable drives count as "one at a time" (safe default; network may gain the most later, but it is not measured).
- Rescue mode and same-volume moves stay one at a time.
- At most one large file copying and one being checked; small files fill the other worker slots.
- Default worker count picked from a short probe (expected about 4), not swept on the large set (drive wear).
- Checksum file sorted by path so it does not depend on timing.
- Target choice, conflict questions and resume checks stay one at a time in planned order (same names at any worker count; resume does not get faster).
- Worker count is internal only: no setting, switch or environment variable; tuned through a bench mode.
- `JobProgress` keeps `CurrentFile` and adds an in-flight count; the M2 UI decides how to show it.
