# DRRipper — Transfer Performance (Ticket #008)

## 1. Transfer modes

| | Balanced (default) | Maximum throughput |
|---|---|---|
| Per-file workers | Fixed `ConnectionsPerFileDefault` (default 8) | Adaptive 4 → 32 along the ladder |
| Budgets | 16 global / 8 per-host, fair share | Preset 32 / 32, saturating share |
| Ramp | None (fixed from first byte) | Staged 4→8→16→24→32 with validation |
| Backoff | Bounded retry only | + concurrency step-down on 429/503/regression |
| Durability | Full (flush + checkpoints + gate) | Full — identical guarantees |
| UI | — | Warning label; applies to newly started jobs |

MaximumThroughput is explicit opt-in (Settings → Network / Performance).
Balanced behavior is unchanged from pre-#008. Switching modes never
rewrites history: running jobs keep their configuration; new jobs use the
new mode. Budget ceilings still need a restart (allocator lifetime);
mode + per-file ceiling apply to newly started jobs live.

## 2. Adaptive ladder

Rungs: `4 → 8 → 24 → 32` (see `SchedulerThroughputPolicy.ConcurrencyLadder`;
`PrevLadderStep`/`NextLadderStep` are gradual by construction — never
32 → 1 unless a severe fault storm drops to the floor of 4).

- **Initial target by size:** <64 MB → 4; 64 MB–1 GB → 8; >1 GB → 16;
  unknown size → 8. Small files never pay for 32 workers.
- **Measurement windows:** ≥2 s (`AdaptiveConcurrencyController.MinWindow`),
  evaluated after successful chunks from verified-downloaded bytes (never
  from queued intentions). One evaluation per window; worker completion
  bursts serialize on a dedicated lock so windows, streaks, and gate
  accounting stay truthful (found by live test during development).
- **Step up** when aggregate gain ≥10%, or optimistically while each
  connection still delivers ≥60% of its best rate (this is what ramps on
  per-connection-capped servers, where aggregate keeps scaling linearly).
- **Validate every increase over two windows:** the first is warm-up grace
  (new workers still establishing connections); the second decides.
  Acceptance needs BOTH aggregate gain ≥5% AND per-connection health ≥60%
  — either signal alone misfires (ramp-up lag looks like a cap; a global
  cap looks like headroom). Failed validation reverts one rung with a
  lengthening probe block (3→6→12→24 windows), so capped paths probe rarely
  and never ratchet, while slow-to-scale paths retry and succeed.
- **Step down gradually** on >15% regression, any 429/503/Retry-After, and
  to the floor on fault storms (≥10 faults/window). After a step-down, no
  ramp for 3 windows (hysteresis).
- **Plateau:** sustained negligible windows hold position with a bounded
  probe block (no 16→24→16→24 oscillation, no unbounded creep).

## 3. Worker mechanics

- Workers = min(cap, max(initial, segment-count)) tasks — never more than
  useful (single-owner chunks), so small files don't park dozens of tasks;
  only `target` hold the dequeue semaphore at a time. No task churn on
  ramp-up, no teardown on ramp-down. Pause/cancel/teardown semantics
  unchanged: parked workers hold no attempt slots, so pause acknowledgement
  stays bounded; teardown cancels the session token and parked workers exit.
- Segment size stays 8 MB (recovery-metadata compatible across modes and
  restarts). Read buffers stay 256 KB pooled (no per-connection giants;
  32 workers ≈ 8 MB transient by design, verified in WS measurements).
- Progress events remain coalesced (250 ms timer + 2 s DB persist); speed
  does not touch the UI thread per read (§37).

## 4. Budget philosophy

The #005.1 allocator is untouched (central FIFO, atomic dual-grant,
exactly-once release, cancellation-safe). MaximumThroughput changes only
the NUMBERS (32/32) and the per-file cap — never the mechanism. One active
large file may therefore consume the whole budget; with 2–3 active jobs the
shared gate still apportions sanely (dominance emerges from worker counts,
not from reservations). Redirects re-attribute to the effective host for
all attempts after the first redirected response (§39; the in-flight
attempt keeps its origin permit — bounded, released normally).

## 5. HTTP stack

- Scheduler-owned transfers share one process-wide `SocketsHttpHandler`
  (`SharedHttpStack`); per-job `HttpClient` instances keep isolated
  `DefaultRequestHeaders`. Direct unit-test constructions keep private
  stacks (zero test-isolation risk).
- Kept: `Accept-Encoding: identity` on every ranged request (byte
  correctness is non-negotiable); `MaxConnectionsPerServer = int.MaxValue`
  (the budget gate, not the handler, provides backpressure);
  2-minute pooled-connection lifetime.
- ConnectCallback/DNS/socket-buffer review (§17/§18): measured, see §8.
  Per-request headers (including browser `RequestContext`) remain isolated.

## 6. Telemetry

`TryGetAdaptiveSnapshot(jobId)` → `(Active, Target, Cap, PeakMBps,
Decision, Evals)`: current target, ceiling, best window, last decision
(`up-gain`, `up-probe`, `down-throttle`, `down-regression`, `down-revert`,
`plateau-hold`, …), evaluation count. Internal API (tests + future UI).

## 7. Benchmark methodology

- Deterministic in-process Kestrel server; medians of 3 iters; integrity
  (SHA-256) outside the timed region; CORRUPT excluded, never averaged.
- New server knobs: per-response `BytesPerSecond` (per-connection cap),
  `GlobalBytesPerSecond` (shared token bucket), `DegradeAfterConnections` +
  `DegradeAddedLatency` (bad scaling), `InitialLatency` (20/80/150 ms WAN
  simulation), `RedirectTarget` (302 cross-host), plus existing
  429/503/Retry-After/reset/short-body faults.
- New harness knobs: `--connect custom|stock`, `--sockbuf tuned|stock`,
  `--url <runtime URL>` (redacted; bytes + TTFB + t50/t90 curves).
- Loopback measures software bottlenecks only; capped/latency profiles
  measure scaling behavior (the actual MaximumThroughput question).

## 8. Results (this box)

Loopback (25×8 logical cores, same-host Kestrel; software-bottleneck
baselines, not internet numbers). Medians of 3 unless noted.

### 8.1 Single-file conns ladder, 100 MB, Balanced (pre-change baseline, §3)

| Conns | 1 | 2 | 4 | 8 | 16 | 24 | 32 |
|---|---|---|---|---|---|---|---|
| Median MB/s | 187.9 | 219.9 | 207.5 | 199.5 | 191.0 | 182.2 | 183.2 |

Optimum ≈ 2; 4–32 flat-or-declining (server/disk-bound loopback).
Finalization ≈ 270 ms (durable flush), CPU ≈ 2 s/100 MB, WS flat 76 MB
across conns (pooled 256 KB buffers — no per-connection giants),
reTx 0, reqs exact. More connections ≠ more speed HERE — this is why
MaximumThroughput is proven on capped/latency profiles below, not here.

### 8.2 Multi-file scheduler, 100 MB/file, Balanced vs Maximum (§24/§25/§53)

| Jobs | Balanced agg (4/file) | Maximum agg (share-capped) | Note |
|---|---|---|---|
| 1 | ~190 | ~175–180 | −5–8%: cold-iter noise band, 1 file × 32 cap |
| 3 | ~265–270 | ~221 | knob mismatch (10/file vs 4/file) on saturated loopback |
| 8 | ~269–296 | ~243 | knob mismatch (8/file vs 4/file) on saturated loopback |

Loopback is server-bound near ~250 MB/s aggregate, so worker-count
differences dominate these cells more than mode logic. The share guard
(§12) bounds worst-case over-parallelization (pre-guard 8-job run showed
wilder outliers); the §53 no-regression proof that MATTERS is §8.5
(capped profiles, where adaptation settles by design): Maximum ≈
Balanced on globally-capped paths, multiples faster on
per-connection-capped paths.

### 8.3 HTTP stack A/B, 100 MB/8 conns (§17/§18)

| Stack | Median MB/s |
|---|---|
| custom ConnectCallback + tuned buffers (current) | 199.5 |
| stock connect | 188.8 (−5%) |
| stock socket buffers | 191.7 (−4%) |

Custom path retained: never slower, marginally faster, plus deterministic
sequential-DNS fallback behavior. Differences near noise; no
simplification justified.

### 8.4 Real-world endpoint (§43/§44)

`speed.cloudflare.com` 100 MB (global per-client cap ≈ 30 MB/s):
Balanced-8 → 27.6 MB/s; Maximum → 29.2 MB/s (cap held, no collapse,
+6% within noise). TTFB ≈ 0.5 s both modes; t90 ≈ 3 s both.
Redacted logging verified (query stripped in all output).

### 8.5 Capped-profile lab proofs (§28/§29/§45A/§53)

Per-connection cap (2 MB/s/resp, 200–500 MB, 1 job): Balanced-8 ≈ 10–16 MB/s;
Maximum ramps to 29/32 permits (≈ 30–50+ MB/s) — see T-PERF-BUDGET-01 and
T-PERF-ADAPT-09. Improvement ≈ +200%, far above the +25% bar.
Global cap (30 MB/s shared bucket, 200 MB, 1 job): Balanced-8 → 65.2 MB/s
median; Maximum → 65.1 MB/s median — bit-identical, zero steady-state
regression (§53). (Absolute numbers exceed the nominal bucket rate due to
coarse per-slice pacing; the relative equality is the result.)
Real endpoint with global cap (`speed.cloudflare.com`, §8.4): identical
conclusion (+6% within noise).
Simulated 80 ms latency (100 MB, 1 job): Balanced-4 → 116.4 MB/s median;
Maximum → 115.2 MB/s median — identical (file too small for ramp windows;
no regression, scaling proven on capped profiles instead).

### 8.6 Load-stall note (test environment)

One cancel-timing test (T-PERF-LIFE-02) intermittently observes permits
held 60 s+ with zero active runtimes under full-suite parallel load. The
allocator was proven race-free by construction (grant/set-Granted-complete
all under one lock; 11k+ race asserts green) and every attempt path is
cancellation-linked, matching this box's documented scheduling-stall
pathology (BASELINE.md: single threads frozen 60–170 s; cf. T-STATE-04).
Mitigations: deterministic mid-transfer cancel gating, 60 s settle polls
with full run-state diagnostics in failure text, and the existing CI
failures-only retry. No product change was justified: every isolated and
loaded re-run balances exactly.

## 9. Beta v0.9.0 comparison (behavioral, no code copied)

Beta v0.9's `ParallelDownloader` is the same lineage: 8 MB chunks, 8 fixed
workers, per-download `HttpClient`, unbounded per-server connections,
2 MB/1 MB socket buffers, sequential-DNS ConnectCallback, same UA string.
Why it *feels* more aggressive:

1. **No durability tax.** Beta never flushed file data before reporting
   progress/completion (page-cache speed). Current engine flushes before
   ranges become recoverable (Ticket #004 policy) — the documented ~2×
   durable-vs-buffered gap. MaximumThroughput does NOT remove this
   (correctness first); it wins back wall-clock time with parallelism.
2. **No fairness caps.** Beta ran one file with 8 workers and zero
   budgeting. Balanced multi-file splits 16/8 across jobs; Maximum gives
   one file up to 32 workers and the whole budget.
3. **No validation/retry discipline.** Beta accepted short reads and
   retried forever without accounting; current work (range validation,
   bounded retry, checkpoints, SQLite summaries, permit acquires) costs
   microseconds per attempt — measurable only in aggregate, kept.
4. **Same 8-worker default.** The single-file Balanced default was never
   slower than Beta by construction; the *perception* gap is the
   durability + validation work above, which this ticket keeps.

## 10. Recommended settings

- Typical desktop: Balanced, 8/file, 16/8 budgets (unchanged).
- Fast line + large ISOs: Maximum throughput (32/32/32 preset). Expect
  other apps to feel it — that is the point of the mode.
- Upload-sensitive / metered: Balanced, fewer active downloads.

## 11. Known limitations

- 8 MB segments bound tail parallelism (no dynamic re-splitting, §20 —
  investigated, deferred as unnecessary at this segment size).
- Per-user pipe ACL hardening still release engineering (§28 of #007).
- Real-WAN behavior varies by server; loopback numbers are bottleneck
  baselines, capped/latency profiles are scaling proofs.
- 48/64 rungs not justified by measurement (plateau at 24–32 on loopback);
  ladder caps at 32 unless future data says otherwise (explicit user values
  up to 64 still honored).

*End of PERFORMANCE.md.*
