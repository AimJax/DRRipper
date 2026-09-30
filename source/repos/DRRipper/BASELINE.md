# DRRipper — Engineering Baseline (Ticket #002)

Date: 2026-09-30 (UTC). Production engine **unchanged** — all results below characterize the baseline; known defects were not fixed.

## 1. Build environment

- OS: Windows 10 build 19045 (win-x64), 8 logical cores, Intel64 Family 6 Model 60.
- .NET SDK installed by this ticket via winget (`Microsoft.DotNet.SDK.10`, official Microsoft distribution): **10.0.401** (`C:\Program Files\dotnet`). No admin intervention was required. Note: fresh shells needed the SDK on PATH; the installer location was used explicitly.
- Runtime observed during tests/benchmarks: .NET 10.0.12.
- NuGet reachable (xUnit/Test.SDK restored from the public feed).

## 2. Build results

- `dotnet restore DRRipper.slnx` — success (~2.5 s first run).
- `dotnet build DRRipper.slnx -c Release` — **success, 0 errors**, ~8 s.
- Warnings: **1** — `ParallelDownloader.cs(158,34): CS0168` (unused `ex` in the `ConnectCallback` DNS catch; pre-existing, production code untouched).
- Solution now contains 4 projects: `DRRipper` (WPF app, net10.0-windows), `DRRipper.TestServer`, `DRRipper.Tests`, `DRRipper.Benchmarks`.

## 3. Test infrastructure

- `DRRipper.Tests` (xUnit 2.9.3, net10.0-windows, `ProjectReference` to the production assembly — no test dependency leaks into the app). Run: `dotnet test` (safe suite is the default signal; `Category=KnownFailure` is the visible defect ledger).
- `DRRipper.TestServer` (in-process Kestrel, ephemeral localhost ports; standalone via `dotnet run --project DRRipper.TestServer -- --port 5099 --size-mb 100`). Deterministic content generated on the fly (no filesystem I/O): pattern bytes or constant bytes. Supports: ranges + 206/Content-Range, no-range mode, chunked unknown-length, clean-EOF short bodies, abort truncation, throttling, per-range and global errors (404/429/500/503), wrong Content-Range totals, gzip bodies, ETag/Last-Modified switching, and a full request log (method/range/status/bytes/connection) for assertions.
- Every engine call in tests is bounded by an explicit `CancellationToken` timeout — the engine retries some failures forever, so unbounded awaits are forbidden in tests.

## 4. Test results (13 tests)

| ID | Scenario | Result | Maps to |
|---|---|---|---|
| T-UNIT-01 | Range coverage of [0,size), no gaps/overlaps (black-box; chunk math is inline/private, see §7) | **PASS** | — |
| T-INT-01 | Normal 20 MB download, 4 conns | **PASS** | — |
| T-INT-02 | Well-formed gzipped ranges, compressible content | **PASS** (see §5) | F-08 refined |
| T-INT-03 | Short chunk body + clean EOF | **KNOWN FAILURE** — corrupt file reported `Completed` | F-01 confirmed |
| T-INT-03b | Aborted (RST) chunk body | **PASS** — retry resumes to byte-identical file | retry path works |
| T-INT-04 | Persistent 500 on one 8 MB range (35 s bound) | **KNOWN FAILURE** — endless `Retrying`, ends `Completed`/incomplete, no explicit failure | F-02 confirmed |
| T-INT-05 | Lying Content-Range total | **PASS** — file correct, lie undetected | P-03 gap documented |
| T-INT-06 | No-range server → single-stream fallback | **PASS** | — |
| T-INT-07 | Unknown length (chunked) | **PASS** | — |
| T-INT-08 | Throttled 2 MB/s server | **PASS** | — |
| T-REC-01 | Interrupt at ~5 MB/24 MB → restart | **KNOWN FAILURE** — full retransmission (ratio 1.23), final content correct | F-03 confirmed |
| T-REC-03 | Payload switched (same size, new seed/ETag) between runs | **PASS** — clean full v2 copy; switch undetected, masked by re-download | F-04 latent |
| T-REC-05 | Single-stream interrupt → restart | **KNOWN FAILURE** — full retransmission (ratio 1.45) | F-03 (refines F-10, see §5) |

Totals: **9 passing, 4 known-failing, 0 unexpected failures, 0 not executed.**

## 5. Baseline refinements discovered by testing (engine still unfixed)

1. **F-01 confirmed empirically (T-INT-03):** clean-EOF short body → SHA-256 mismatch while state = `Completed`.
2. **F-08 narrower than audited (T-INT-02):** HttpClient strips Content-Length on transparent decompression, so the engine's span fallback is exact and well-formed gzip servers download correctly. The residual hazard is any party reporting an encoded length as identity length.
3. **F-10 mechanism corrected (was T-REC-05 premise):** the stale-tail path is unreachable — both single-stream entry points pre-truncate (`FileMode.Create`). The observable defect is full retransmission, shared with F-03.
4. **NEW observation — inconsistent cancellation (candidate for a future finding ID):** cancelling the external token mid-flight on the parallel path returns a path with state `Completed` and a zero-padded partial file (observed in T-REC-01: 5.3 MB downloaded of 24 MB, `Completed`); the single-stream path instead throws `OperationCanceledException` (observed in T-REC-05). Cancel semantics need unification in Phase 1.
5. **P-03 confirmed by characterization (T-INT-05):** wrong Content-Range totals are silently ignored.
6. **Retry works for error-terminated bodies (T-INT-03b):** aborts resume from the last written byte to a correct file — only clean EOF is mishandled.

## 6. Benchmark configuration and performance results

Harness: `DRRipper.Benchmarks` (`dotnet run --project DRRipper.Benchmarks -c Release -- --sizes-mb 10,100,1024 --conns 1,2,4,8,16 --iters 3`). Same-host Kestrel loopback (detects software bottlenecks; NOT internet performance). Integrity verified outside the timed region; all 45 runs OK, 0 corrupt, 0 retransmitted bytes on the clean server. Raw table retained locally (git-ignored `results/`); medians:

| Size | conn=1 | conn=2 | conn=4 | conn=8 | conn=16 (median MB/s) |
|---|---|---|---|---|---|
| 10 MB | 161.9 | 145.5 | 151.9 | 161.9 | 162.6 |
| 100 MB | 199.5 | 227.6 | 214.8 | 215.2 | 208.5 |
| 1024 MB | 201.7 | **252.8** | 220.0 | 225.0 | 230.6 |

- More connections ≠ more speed: the optimum here is 2; 4–16 are flat or slower (server/CDN-throttling thesis in AUDIT.md holds even on loopback).
- Small files are overhead-dominated (10 MB ≈ same across conns; first cold run 47.7 MB/s).
- Finalization latency ≈ 0 ms in all runs (direct-offset writes, no concatenation — architecture requirement holds).
- CPU ≈ 22 s per 1 GB run (8-core box; includes in-process deterministic content generation — harness overhead, not pure engine cost).
- Peak working set ≈ 2 172 MB on 1 GB runs: **test-server artifact** — the server materializes each full response body (`Slice`) before pacing; single-stream conn=1 builds a 1 GB array. Not engine memory.
- Handle deltas ≈ 0; request counts exact (e.g. 128 chunks + HEAD = 129 for 1 GB multi-conn).
- Localhost numbers are a software-bottleneck baseline only.

## 7. Hardware/software versions and limitations

- Hardware: 8-logical-core Intel64 box, Windows 10.0.19045. No reliable RAM/disk-model inventory available — stated explicitly as a limitation; rerun benchmarks on target hardware before tuning.
- Software: SDK 10.0.401, runtime 10.0.12, xUnit 2.9.3, Test SDK 17.14.1, Kestrel (ASP.NET Core 10).
- Limitations: no internet-path measurements; server and downloader share one process (CPU/WS include harness); T-UNIT-01 is black-box because chunk math is inline and production accessibility was deliberately not changed (Ticket #002 §8); per-test timeouts bound, but do not eliminate, the engine's infinite-retry hangs (T-INT-04 uses a 35 s bound); benchmark `finalization` is approximated as return-time minus last-progress-time.
- Machine-specific paths, credentials, and raw result artifacts are excluded from the repo (see `.gitignore`).

---

# Ticket #003 appendix (2026-09-30, same hardware)

Production engine modified (strict validation, completion gate, fault propagation,
unified cancellation, bounded retry). Test server rewritten to bounded-memory
streaming. Ticket #002 data above is preserved as the pre-fix record.

## Build

- `dotnet build DRRipper.slnx -c Release --no-incremental`: **0 errors**,
  **1 pre-existing warning** (CS0168, untouched DNS catch — same as Ticket #002).

## Tests: 30 passing, 2 known-failing, 0 unexpected

- New/migrated integrity tests all green: A (T-INT-03 resume-to-identical),
  B, C, D, E (T-INT-05 fail-fast), F, G (T-INT-04 bounded throw), H
  (Retry-After honoured), I/J/K (cancellation family + `Cancelled`, never
  `Completed`), L (reason-preserving session abort), M (RangeTracker unit),
  N (classifier + locked-file), O, P/Q (unchanged fallbacks), mid-run
  identity-switch rejection.
- Known failures carried over unchanged: T-REC-01/05 (persistent cross-process
  recovery — explicitly the next ticket; retransmission ratios 1.23/1.45 re-observed).

## Benchmarks (fresh comparable baseline)

Same matrix (10/100/1024 MB × 1/2/4/8/16 conns × 3 iters), all 45 runs
SHA-256 OK, 0 retransmits, finalization ≈ 0 ms, handle deltas ≈ 0.
Median MB/s:

| Size | conn=1 | conn=2 | conn=4 | conn=8 | conn=16 |
|---|---|---|---|---|---|
| 10 MB | 259.9 | 251.5 | 294.2 | 304.1 | 284.8 |
| 100 MB | 392.4 | 543.1 | 457.2 | 344.6 | 402.1 |
| 1024 MB | 420.9 | 583.9 | 508.1 | 451.6 | 433.8 |

CPU ≈ 17 s per 1 GB run (was ≈ 22 s); peak working set ≈ 74 MB on every
configuration (was 2 172 MB on 1 GB runs).

## Regression analysis

Throughput is roughly 2× the Ticket #002 medians (e.g. 1 GB conn=2:
252.8 → 583.9 MB/s) with the same optimum shape (2 connections best;
4–16 flat or slower — the audit thesis holds). Per the ticket's methodology
rule this is reported as a **fresh baseline, not a claimed engine speedup**:
the test-server rewrite (full-body materialization → 64 KB streaming,
2 GB of transient arrays removed) eliminates the dominant same-host
bottleneck, so most of the delta is harness effect. Plausible genuine engine
contributors — identity encoding (no decompression path), one timer instead
of two linked CTS allocations per read, cheaper completion accounting — were
not isolated and are not claimed. No benchmark regressed: every cell is
faster-or-equal, all integrity checks pass, so correctness was not traded
for speed. The >5% rule will apply to future changes against THIS table.
