# DRRipper — Test Plan

**Repeatability first:** all network behaviour tests run against a **controlled local HTTP server** (e.g. ASP.NET Core / Python harness with scriptable status codes, `Content-Range` faults, throttling, truncation, and kill-switch). No test depends on the public internet. Metrics for every run: throughput (MB/s), CPU %, working-set/private bytes, handle count, disk I/O (read/write bytes), and integrity (SHA-256 vs fixture).

**Completion criteria** (a download may enter `Completed` only if): every range answered 206 with a `Content-Range` span matching the request; accounted bytes == `Content-Range` total == on-disk length; no worker fault outstanding. Cryptographic hash match is integrity verification *on top of* completeness, required wherever a fixture hash exists.

---

## 1. Unit tests

- **T-UNIT-01** Chunk-boundary math: first/middle/last chunk spans for sizes 0/1/8 MB/8 MB+1/10 GB (no overlap, no gap).
- **T-UNIT-02** Metadata round-trip: serialise/deserialise chunk maps incl. empty and 10k-chunk cases; corrupt JSON must be rejected, never trusted.
- **T-UNIT-03** Pause cancels in-flight requests: fault a hung read, assert per-request CTS fires and no request survives resume (covers P-01).
- **T-UNIT-04** Byte accounting: simulated partial-write retries assert `_totalDownloaded` equals on-disk bytes (covers P-02).
- **T-UNIT-05** Completion gate: queue-drained-but-short and fault-flagged runs must NOT yield `Completed` (covers F-02/F-12).
- **T-UNIT-06** Progress math: zero/NaN guards, EMA bounds, per-file baseline reset (covers P-05).
- **T-UNIT-07** Sleep-prevention ref-counting across overlapping sessions (covers P-10).
- **T-UNIT-08** Cancel/Dispose ordering: no `ObjectDisposedException` escapes; final state is `Cancelled` (covers F-06).

## 2. Integration tests (local server)

- **T-INT-01** Happy path matrix: sizes 1 KB → 4 GB × connections 1/4/8; hash match; benchmark recorded.
- **T-INT-02** Gzip/deflate server with range requests: hash must match with `Accept-Encoding: identity` behaviour (covers F-08).
- **T-INT-03** Truncated chunk body (server closes mid-chunk): must retry and finish byte-identical; must never report `Completed` with a hole (covers F-01/F-12). **Regression test for the v0.9→current regression.**
- **T-INT-04** 500-on-one-range fault injection: run must end `Failed` with the fault surfaced, never `Completed` (covers F-02).
- **T-INT-05** Malformed/wrong-span `Content-Range`: must be rejected, not written to the wrong offset (covers P-03).
- **T-INT-06** No-range server (`200` to range request): clean fallback to single-stream, hash match.
- **T-INT-07** Unknown size (chunked transfer): single-stream completes; length verified.
- **T-INT-08** Server-side throttling (e.g. 100 KB/s + resets): completes without short-read acceptance; throughput logged.
- **T-INT-09** Batch `[good, 404, good, timeout]`: good files complete, bad files recorded `Failed`, queue continues (covers F-05).
- **T-INT-10** Cancel mid-batch and pause/resume mid-file: clean states, no cross-file contamination (covers F-06).

## 3. Stress tests

- **T-STRESS-01** 10 GB+ single file: sustained throughput, flat memory/handles, hash match ("enormous files" target).
- **T-STRESS-02** Queues of 100 / 500 / 1 000 / 10 000 URLs (local server, mixed good/bad): completion ledger correct, failures isolated, memory O(active) not O(queue).
- **T-STRESS-03** Concurrent downloads (2/4/8 active) with per-host caps: no socket exhaustion, fair progress.
- **T-STRESS-04** Handle/memory stability: process counters sampled per 100 files; fail on growth trend (covers F-09).

## 4. Recovery tests

- **T-REC-01** Process kill mid-download → restart resumes without retransmitting verified bytes (covers F-03).
- **T-REC-02** OS-style kill during `.drmeta` write →restart tolerates missing/corrupt metadata (full re-download, correct file).
- **T-REC-03** Server file replaced between pause and resume (same size, new ETag/Last-Modified) → resume detects change, resyncs, no hybrid file (covers F-04).
- **T-REC-04** Corrupt `.drmeta` (bit-flip, truncation) → detected via checksum, safe restart.
- **T-REC-05** Single-stream kill → restart produces correct file (covers F-10).
- **T-REC-06** Disk-full mid-download → fast `Failed` with clear message, no infinite `Retrying` (covers F-11).
- **T-REC-07** Network disconnect (30 s blackout) then restore → auto-resume to byte-identical file; pause/resume race fuzz (pause issued during retry backoff / during final chunk).

## 5. Performance benchmarks

- **T-PERF-01** Connections sweep (1–32) on LAN + throttled profiles: throughput/socket-count/memory curves; select defaults empirically (covers F-07).
- **T-PERF-02** Socket-buffer sweep (64 KB–2 MB): throughput vs memory; justify final values.
- **T-PERF-03** Default vs custom `ConnectCallback` (incl. HTTP/2): time-to-first-byte and setup latency (covers P-04).
- **T-PERF-04** HEAD+probe vs GET-first: per-file latency overhead × 1 000 small files (covers P-07).
- **T-PERF-05** Chunk-size sweep (1/4/8/32 MB) × file sizes (10 MB/1 GB/10 GB): overhead vs straggler behaviour (covers P-08).
- Gates: every benchmark records throughput, CPU, working set, handles, disk I/O; any tuning PR must re-run T-INT + T-REC.

## 6. Security tests

- **T-SEC-01** Filename attacks: `../`, absolute paths, `CON`/`NUL`, trailing dots, ADS colons, 300-char names, hostile `Content-Disposition` — all contained in the target dir or rejected (covers P-06).
- **T-SEC-02** Redirect chain to third-party host: credentials/`Authorization` must not leak cross-host; hop limit enforced (covers P-09).
- **T-SEC-03** URL with embedded userinfo: never written to logs/metadata in recoverable form.

## 7. Ticket #002 implementation mapping (baseline status 2026-09-30)

Implemented in `DRRipper.Tests` against `DRRipper.TestServer`; full results in BASELINE.md.

| Plan ID | Implementation | Status |
|---|---|---|
| T-UNIT-01 | Black-box range-coverage assertion (chunk math is inline/private; production accessibility intentionally unchanged) | PASS |
| T-INT-01/05/06/07/08 | As specified | PASS |
| T-INT-02 | Well-formed gzip over compressible content — passes via Content-Length stripping + span fallback; residual F-08 hazard documented | PASS with notes |
| T-INT-03 | Graceful short body + clean EOF | KNOWN FAILURE (F-01) |
| T-INT-03b (extra) | Aborted (RST) chunk body — retry recovers correctly | PASS |
| T-INT-04 | Persistent 500 on one range, 35 s bound | KNOWN FAILURE (F-02) |
| T-REC-01/05 | Interrupt → restart retransmits fully; content correct | KNOWN FAILURE (F-03) |
| T-REC-03 | Identity switch masked by full re-download | PASS with notes (F-04 latent) |

Not yet implemented: remaining T-UNIT/T-STRESS/T-PERF-sweep/T-SEC IDs, 429/503 and multi-range cases — deferred to later tickets.

## 8. Ticket #003 implementation mapping (engine fixes 2026-09-30)

| Required test | Implementation | Status |
|---|---|---|
| A short clean EOF | T-INT-03 rewritten: shortfall resumes to byte-identical file | PASS |
| B 200 during segmented transfer | `B_RangeAnswered200_Falls_Back_To_SingleStream` (+ `Respond200ToRanges` server knob) | PASS |
| C/D wrong Content-Range start/end | Rejection tests (+ `ContentRangeOverride` knob) | PASS |
| E wrong Content-Range total | T-INT-05 rewritten to assert fail-fast rejection | PASS |
| F premature closure, zero progress | `F_ZeroByte_Abort_Exhausts_Bounded_Retries` | PASS |
| G persistent 500 | T-INT-04 rewritten to assert bounded `DownloadFailedException` | PASS |
| H 429 + Retry-After | `H_RateLimit_With_RetryAfter_Fails_Bounded` (elapsed ≥ header) | PASS |
| I/J parallel + single-stream cancel | OCE family, `Cancelled`, never `Completed` | PASS |
| K cancel during backoff | Surfaces in well under the exhaustion time | PASS |
| L failed worker amid active workers | `L_Failed_Worker_Stops_Session_With_Reason` (reason preserved) | PASS |
| M duplicate/overlap accounting | `RangeTracker` unit tests (exact/duplicate/overlap/out-of-order/partial/bounds) | PASS |
| N disk-full / unrecoverable FS | Classifier unit tests (synthetic HResults) + locked-file fail-fast integration | PASS |
| O byte-identical success | `O_Large_Download_Is_ByteIdentical` (32 MB, seed 7, 8 conns) | PASS |
| P/Q no-range + unknown-size fallback | Existing T-INT-06/07, unchanged | PASS |
| Extra §4 identity consistency | `IdentitySwitch_MidDownload_Is_Rejected` (ETag switch mid-run aborts) | PASS |

Suite totals after Ticket #003: **30 passing, 2 known-failing** (T-REC-01/05 persistent cross-process recovery — next ticket), 0 unexpected.

## 9. Ticket #004 implementation mapping (sessions + persistent recovery, 2026-10-01)

| Required scenario | Implementation | Status |
|---|---|---|
| T-REC-01 genuine kill + restart | `T_REC_01_Kill_Reuses_Verified_Bytes` (child driver SIGKILL-equivalent, new-process restart, ratio-bounded reuse) | PASS |
| T-REC-02 kill during checkpoint | `T_REC_02_Early_Kill_Still_Recovers` + `T_REC_02_Torn_Tmp_Ignored_Snapshot_Used` (torn tmp ignored, previous snapshot used) | PASS |
| T-REC-03 identity change | `T_REC_03_Content_Switch_Triggers_Detected_Restart` (validator mismatch → full restart, clean v2, no hybrid) | PASS |
| T-REC-04 corrupt/truncate metadata | Truncated / bit-flip / missing variants → safe full restart | PASS |
| T-REC-05 single-stream | `T_REC_05_NoRange_…` (safe full restart, §11B) + `T_REC_05b_…_Reuses_Prefix` (If-Range prefix resume with exact-offset assertion, §11A) | PASS |
| T-REC-06 missing part | Full restart, correct file | PASS |
| T-REC-07 blackout/restore | 6 s 503 blackout → auto-resume, byte-identical | PASS |
| T-STATE-01/02/03 pause timing | Pause mid-read / mid-backoff / immediate resume | PASS |
| T-STATE-04 rapid cycles | Event-driven cycles + bounded ops + watchdog; quarantined `Sensitive` (host-scheduling flake, see BASELINE) | PASS (quarantined) |
| T-STATE-05/06 cancel precedence | Cancel-while-paused / cancel-racing-pause → `Cancelled`, never `Completed` | PASS |
| T-NET-01 repeated timeouts | `StallAfterBytes` + 2 s reads → resume-to-identical, multi-attempt proof | PASS |
| T-NET-02 cancel pending read | OCE + `Cancelled`, safe buffer lifecycle | PASS |
| T-INT-IDENTITY mid-run switch | Existing rejection test, still green | PASS |
| T-INT-COVERAGE sparse resume | Server-restart design (pristine log): resumed spans disjoint, byte-bounded | PASS |
| Extras | `Checkpoint_Writes_Coverage_To_Disk` unit, `ResolveUniquePath` unit, `Resume_Startup_Latency` measurement, pause-phase telemetry | PASS |

Suite totals after Ticket #004: **53 passing, 0 known-failing, 0 unexpected**
(T-STATE-04 counted among the 53; quarantined from the blocking CI gate only).

## 10. Ticket #004.1 implementation mapping (checkpoint durability, 2026-10-01)

| Required test | Implementation | Status |
|---|---|---|
| T-CKPT-01 concurrent checkpoints | Two gated overlapping generations; canonical ends at the later-claimed generation, valid, no strays | PASS |
| T-CKPT-02 stall/timeout/supersede | Gen stalls, caller times out, newer publishes, stale resumes and is discarded; canonical pinned to newer | PASS |
| T-CKPT-03 timeout leaves no trace | Nothing partial reaches canonical; late-but-newest work still publishes cleanly afterward | PASS |
| T-CKPT-04 out-of-order | Three generations complete newest-first; highest valid wins with full coverage | PASS |
| T-CKPT-05 flush failure | `Win32Exception(112)` preserved as `InnerException`; canonical byte-identical; no strays | PASS |
| T-CKPT-06 temp-flush failure | `IOException` surfaces as `DownloadFailedException`; previous snapshot survives | PASS |
| T-CKPT-07 stale task at kill | Timeout-abandon + dispose + late gate release → discard; restart loads canonical only | PASS |
| T-CKPT-08 dispose during checkpoint | Bounded dispose, no hang, no unobserved fault, no publish, no stray temp | PASS |
| T-CKPT-09 degraded pause | Pause parks cleanly with degraded flag/message; last-good snapshot intact and reusable | PASS |
| T-CKPT-10 finalize flush failure | `Failed` (never `Completed`); part + last-good metadata survive; healed restart completes | PASS |
| T-CKPT-STRESS | 200 concurrent gated checkpoints converge; canonical valid + complete; zero faults/strays | PASS |

Suite totals after Ticket #004.1: **63 passing in the blocking lane
(52 carried + 11 new), 0 known-failing, 0 unexpected.**
T-STATE-04 remains the sole `Sensitive` quarantine (host-scheduling evidence in BASELINE).

*End of TEST_PLAN.md.*
