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

---

# Ticket #004 appendix (2026-10-01, same hardware)

Production engine re-architected around per-download sessions with durable
recovery (see ARCHITECTURE.md §10). Ticket #003 data above is preserved.

## Build

- `dotnet build DRRipper.slnx -c Release --no-incremental`: **0 errors**,
  **1 pre-existing warning** (CS0168, untouched DNS catch). New projects:
  `DRRipper.RecoveryDriver` (child-process kill driver), `DownloadSession.cs`.
- Test count: **53 safe tests** (30 carried + 23 new), **0 known failures**
  (the ledger is empty; both T-REC items now pass by design), plus
  1 environment-sensitive quarantine (T-STATE-04, CI non-blocking).

## Tests

- Recovery: genuine SIGKILL-equivalent kills resume with bounded
  retransmission (T-REC-01-KILL ratio ≈ 1.1–1.4 vs 2.0 for full restart);
  torn tmp ignored; corrupt/truncated/missing metadata and missing part all
  restart safely; blackout auto-resumes; identity switch detected (full
  restart, clean v2, never hybrid).
- State machine: pause ack ≈ 50–65 ms, checkpoint ≈ 9–14 ms, resume ≈ 0 ms
  (telemetry); rapid cycles green on quiet runs; cancel precedence holds.
- Network: repeated 2 s stalls resume to identical output; pending-read cancel
  is clean (settled-read protocol, abandon-on-unsettled).
- Coverage: post-restart requests provably disjoint from verified spans
  (server-restart design gives pristine attribution).
- Resume startup latency (8 MB resume, first progress event): a few seconds,
  dominated by probe + a trickle of immediate progress (measured < 30 s bound;
  typical observation ≈ 2–5 s).
- Pause checkpoint file size: hundreds of bytes for few-span snapshots
  (~400 B observed for 8 MB); scales ≈ 25 B/span (≈ 3 KB at 128 spans/1 GB).

## Benchmarks (fresh DURABLE baseline — methodology break, read this first)

Same matrix (10/100/1024 MB × 1/2/4/8/16 conns × 3 iters), all 45 runs
SHA-256 OK, 0 retransmits, handle deltas ≈ 0. Median MB/s:

| Size | conn=1 | conn=2 | conn=4 | conn=8 | conn=16 |
|---|---|---|---|---|---|
| 10 MB | 121.1 | 138.7 | 147.4 | 142.5 | 162.6 |
| 100 MB | 200.9 | 230.4 | 214.6 | 188.8¹ | 189.5 |
| 1024 MB | 211.5 | 244.3 | 229.1 | 212.0 | 201.2 |

¹ 100 MB conn=8 cell re-run after a transient 55 MB/s outlier (system hiccup);
rerun median 188.8; the outlier is disclosed, not hidden.

Finalization latency (fixed metric: last byte-growth → return): ≈ 30 ms
(10 MB), ≈ 260 ms (100 MB), ≈ 0.24–2.6 s (1 GB, writeback-race dependent —
larger when the faster transfer outruns OS writeback).
CPU ≈ 15–16 s and WS ≈ 79 MB per 1 GB run; allocated bytes ≈ ±20 MB/run
(GC noise; includes in-process server). Request counts exact
(129 = 128 chunks + HEAD for 1 GB multi-conn).

## Regression analysis — READ BEFORE COMPARING WITH TICKET #003

Throughput is roughly HALF the Ticket #003 medians (e.g. 1 GB conn=2:
583.9 → 244.3 MB/s) with the same optimum shape (2 connections best).
**This is NOT an engine regression.** Isolation proof: #004 engine +
#003 server reproduces the same level; #003 engine on today's box reproduces
583.9; disabling ONLY `FlushFileBuffers` in a scratch tree restores 587 MB/s.
Root cause: Ticket #004 implements the ticket-mandated durability policy
(flush file data before marking ranges recoverable/completed). Direct disk
measurement on this box: 1 GB streams to page cache in 0.57 s but physical
flush costs +2.5 s (≈ 400 MB/s physical). Ticket #003 measured buffered
(page-cache) speed; Ticket #004 measures durable speed. Per the methodology
rule this is a **fresh comparable baseline**: future changes compare against
THIS table, and correctness (durability) is never traded for benchmark speed.
Engine transfer mechanics are unchanged-or-cheaper (per-chunk buffers,
single read timer, settled reads).

## Environment and limitations

- Same Windows 10 box, .NET 10.0.12, 8 logical cores as Tickets #002/#003.
- Intermittent host-level scheduling stalls observed (single threads frozen
  60–170 s while sibling timers ticked; no disk errors in event log; 20 GB
  RAM free; no runaway processes). All engine waits are bounded (semaphore
  30 s, ack 15 s, checkpoint 5/15/120 s, test wrappers 60 s); T-STATE-04 is
  quarantined as environment-sensitive with fail-fast stall detection.
- Localhost loopback measures software bottlenecks, not internet performance.
- Abort-race byte overcount documented (AUDIT.md §12): integrity assertions
  use client-observable contracts (spans, offsets, hashes), never raw
  server-side byte totals across aborts.
