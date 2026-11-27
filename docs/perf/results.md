# Task 16: Ferry speed against FastCopy

Measured 2026-11-22 with `bench.ps1` (Ferry CLI against FastCopy, both run as programs, alternating which goes first, destination emptied before every run). Median of 3 rounds.

## Test PC

- CPU: Intel Core i5-13600K (20 threads), 64 GB RAM, Windows 11 Pro.
- From: WD_BLACK SN850X 2 TB (NVMe). To: Samsung SSD 980 1 TB (NVMe, no DRAM cache).
- Windows Defender real-time protection on (default).
- Standby cache **not** cleared between runs (needs an administrator; `bench.ps1` does it when run elevated). Large files bypass the cache on both tools (unbuffered I/O); the small files' source data was cached for both tools alike.
- The HDD sets from the spec were **not run**: the HDD was not available (no drive letter). They stay open.
- Another heavy program (browser tests) ran during part of the measurements; runs vary by up to 2x, so read the ratios as rough.

## Result against the release gate (Ferry ≤ 1.10 × FastCopy)

| Set | Files | Size | Verify | FastCopy | Ferry | Ratio | Gate |
|---|---:|---:|---|---:|---:|---:|---|
| NVMe → NVMe, large (20 × 1 GiB) | 20 | 20 GiB | off | 18.2 s | 9.4 s | 0.52 | pass |
| NVMe → NVMe, large | 20 | 20 GiB | on | 19.2 s | 22.4 s | 1.17 | **fail** |
| NVMe → NVMe, mixed (2 GiB large + 2 GiB of 1–64 KiB files) | 64,602 | 4 GiB | off | 53.4 s | 108 s | 2.02 | **fail** |
| NVMe → NVMe, mixed | 64,602 | 4 GiB | on | 57.1 s | 434 s | 7.61 | **fail** |
| NVMe → NVMe, small (10,000 × 1–64 KiB), not gated | 10,000 | 318 MiB | off | 6.5 s | 15.2 s | 2.36 | – |
| NVMe → NVMe, small, not gated | 10,000 | 318 MiB | on | 6.5 s | 22.9 s | 3.53 | – |
| HDD → NVMe, large and small | – | – | – | – | – | – | not run |

**The gate is not met.** Large files without verify are twice as fast as FastCopy; everything with many small files, and large files with verify, are slower.

## Why (measured, not guessed)

Profiled with `dotnet-trace` and small probes on the same drives.

1. **Forcing each small file to disk** (FlushFileBuffers before the rename) was about 80% of a small file's copy time. *Changed:* copies now skip it for files under 8 MiB (moves, rescued and reconnected files still flush); the journal marks those files and a resume checks them again (see the threat model). Small set, verify off: 16.6 s → 12–15 s.
2. **Reopening a file just after writing it waits 5–7 ms** (antivirus scans a new file on its first open; 0.5 ms a moment later). Verify reopened every file to read it back, so 10,000 small files spent about a minute waiting. *Changed:* small files are now written unbuffered and read back through their own still-open handle (still a read from the drive, not the cache). Small set, verify on: 86 s → 18–23 s. Write-through on part files cost a further ~1 ms per small file with verify on and is not needed for safety, so it was removed.
3. **What is left** for small files is per-file work FastCopy does not do: the journal records, the part file and rename, separate opens for checks, dates and attributes. About 0.8 ms per file more than FastCopy with verify off. In the mixed set, verify on costs ~6.7 ms per small file against 2.3 ms in the small set; the likely cause is the destination drive (no DRAM cache) handling tens of thousands of small unbuffered writes and reads, but this is not proven yet.
4. **Large files with verify:** Ferry copies, then reads the copy back, one after the other. FastCopy's verify costs it almost nothing (it overlaps reading back with copying the next file). Tuning buffer sizes cannot close that; overlapping can.

## Tuning levers (R27)

The buffer size (1/4/8/16 MiB), pipeline depth (2/4/8), unbuffered threshold (1/8/32 MiB) and file order were **not swept on the full sets**: a sweep of the large set would write about 500 GB to the test drives, and none of the gaps above comes from these levers (large files without verify are already 2x FastCopy). A smoke sweep of 1,000 small files showed only noise between the choices. **Defaults are unchanged:** 4 MiB buffer, depth 4, 8 MiB unbuffered threshold, smallest file first. `Ferry.Bench sweep` is ready for when a lever is worth measuring.

## Micro-benchmarks (BenchmarkDotNet, one core)

| What | Result |
|---|---|
| Buffer pool rent + return (1 MiB and 4 MiB) | 40 ns, no allocation |
| XxHash3, 64 MiB in 4 MiB pieces | 6.05 ms ≈ 11 GB/s (plan target ≥ 10 GB/s: met) |
| SHA-256, same | 26.6 ms ≈ 2.5 GB/s |
| FileCopier, 256 MiB in the temp folder | not run (writes several GB; run `bench.ps1 -Micro -MicroFilter '*FileCopier*'` when useful) |

## What would close the gap (for the user to decide before M2)

- **Copy several small files at once** (FastCopy does): hides the per-file waits. The engine copies one file at a time today; this touches the job loop, journal order and progress.
- **Verify large files while the next one copies:** removes most of the large-file verify cost.
- **Do less per small file:** fewer separate opens and checks per file, where safety allows.
- Measure the HDD sets once the HDD is back.

## Parallel copies (2026-11-27)

Ferry now copies several files at once when every drive is a solid-state drive (see `docs/superpowers/specs/2026-11-26-ferry-parallel-copy-design.md`), and checks a large file while the next one copies. Measured on the same PC with one change: the source is now a Windows Storage Space (the earlier single NVMe source was no longer available). The destination is the same Samsung SSD 980 1 TB. Ferry reports the Storage Space as a solid-state drive. Standby cache not cleared (not run as administrator).

### How many files at once

`Ferry.Bench jobs` on the small set (10,000 files of 1–64 KiB, 318 MiB), whole jobs through the engine:

| Files at once | Verify off | Verify on |
|---:|---:|---:|
| 1 | 14.25 s | 20.08 s |
| 2 | 9.41 s | 11.91 s |
| 4 | 7.25 s | 8.10 s |
| **8** | **5.76 s** | **6.21 s** |

**Default changed to 8** (the fastest in both rows).

Counts above 8, two passes in opposite order (verify off / on, seconds):

| Files at once | Pass 1 (rising order) | Pass 2 (falling order) |
|---:|---:|---:|
| 8 | 4.49 / 5.22 | 4.05 / 4.84 |
| 12 | 3.90 / 4.81 | 4.43 / 4.89 |
| 16 | 4.03 / 4.53 | 4.41 / 5.01 |
| 24 | 3.89 / 4.63 | – |
| 32 | 4.28 / 5.31 | – |

Above 8 there is no gain: whichever count runs later in a pass looks faster, and the order flips the result. 8 stays (the smaller count wins a tie). Runs vary by about 25% from one day to the next (8 workers: 5.8 s in the first probe, 4.0–4.5 s here).

### Against FastCopy (median of 3 rounds)

| Set | Verify | FastCopy | Ferry | Ratio | Gate | Ratio on 2026-11-22 |
|---|---|---:|---:|---:|---|---:|
| Large (20 × 1 GiB) | off | 18.17 s | 23.23 s | 1.28 | **fail** | 0.52 |
| Large | on | 18.15 s | 25.30 s | 1.39 | **fail** | 1.17 |
| Mixed (64,602 files, 4 GiB) | off | 70.46 s | 52.08 s | 0.74 | pass | 2.02 |
| Mixed | on | 52.69 s | 85.54 s | 1.62 | **fail** | 7.61 |

- **Many small files:** the gap is mostly closed. Mixed with verify off is now faster than FastCopy; with verify on, Ferry's time fell from 434 s to 86 s. Mixed runs varied a lot (Ferry 40–95 s, FastCopy 51–72 s), so read those ratios as rough.
- **Large files got slower on this setup, not because of this change.** Verify off went from 9.4 s to 23.2 s. A before/after run of the Ferry CLI on 4 of the large files (verify off, alternating) took 4.5 s and 4.0 s with the code from before parallel copies, and 4.0 s and 4.1 s with the new code: the same. A read-only test shows the Storage Space reads at 1.6 GB/s with one or four reads waiting, and 2.3 GB/s with sixteen; Ferry keeps four waiting and reached about 0.9 GB/s over the whole set. The likely causes are the new source and the destination (no DRAM cache) after many gigabytes of writes in a row; not proven. FastCopy's large-file times did not change between the two days.
- **Large files with verify** now cost Ferry about 2 s more than without (25.3 against 23.2 s), down from 13 s: checking behind the next copy works.

### Still open

- Large files on this setup (1.28 / 1.39): try more reads waiting on large files, and rerun once a single-NVMe source is back so the result can be compared with 2026-11-22.
- Mixed with verify on (1.62): the per-small-file read-back.
- HDD sets: not run (no HDD available).

## How to run

1. Copy `bench.example.json` to `bench.local.json` (git-ignored) and fill in FastCopy's path and the folders. Destinations must be empty or made by the bench tools.
2. `./bench.ps1 -Generate` creates the data and runs the comparison (results in `artifacts/bench/`). Run it as administrator to clear the standby cache between runs.
3. `./bench.ps1 -Micro -MicroFilter '*Hash*'` for a micro-benchmark; `-Sweep` adds the tuning sweep (writes a lot on large sets).
