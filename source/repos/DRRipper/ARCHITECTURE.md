# DRRipper — Architecture

> Convention: §1–§7 describe the **current** implementation (evidence from source). §8 proposes the **future** architecture. Nothing here authorises code changes (audit-only task).

---

## 1. Current application architecture

```
MainWindow (code-behind, WPF) ──owns──▶ ParallelDownloader ──owns──▶ HttpClient ──▶ SocketsHttpHandler
       │                                     │
       │ sequential per-URL loop             │ N=8 workers + 250 ms progress timer
       ▼                                     ▼
  ProgressBar / labels              single preallocated file (RandomAccess)
```

- **Project:** `DRRipper.csproj` — `net10.0-windows`, WPF + WinForms (folder dialog), v0.7.0. No NuGet dependencies beyond the SDK.
- **Separation of responsibilities:** minimal. `MainWindow.xaml.cs` (265 lines) owns batch orchestration, URL parsing, state→button mapping, and formatting. `ParallelDownloader.cs` (~1070 lines) owns networking, segmentation, persistence, progress, retry, power management, and filename resolution. No MVVM, no services, no logging, no configuration surface (connection count hardcoded as `8` at the call site, `MainWindow.xaml.cs:118`).
- **Thread safety model:** `ConcurrentQueue` for chunks, `ConcurrentDictionary` for in-flight tracking, `Interlocked` counters, `lock`s for state/timer/reporting. Correct in the small, but all state is per-`ParallelDownloader`-instance and shared across batch files (§6 risk).

## 2. Existing download lifecycle

1. **Probe:** `HEAD` → optional `Range: 0-0` `GET`; extracts size, `Accept-Ranges`, `Content-Disposition`, media type.
2. **Resolve:** filename from `Content-Disposition` → redirect URI → URL path → `download.bin`, plus media-type extension patching; sanitised and joined to the target dir.
3. **Preallocate:** `FileMode.Create` + `SetLength(totalSize)` (destroys prior partial data — AUDIT.md F-03). Unknown size → create-empty + single-stream.
4. **Fan-out:** fixed 8 MB chunk queue (+ `.drmeta` write on pause); 8 workers (or 1 stream) run to queue exhaustion.
5. **Terminate:** `WhenAll` → unconditional `Completed` (AUDIT.md F-02/F-12); `.drmeta` deleted. Batch loop advances to the next URL reusing the same downloader.

## 3. Networking architecture

- One long-lived `HttpClient` over `SocketsHttpHandler` with `PooledConnectionLifetime` 2 min / idle 30 s, `EnableMultipleHttp2Connections`, infinite client timeout, browser-like `User-Agent`, global `Referer` = origin authority.
- **Concerns (current):** `MaxConnectionsPerServer = int.MaxValue` (no backpressure); custom `ConnectCallback` doing manual DNS + sequential connect + post-connect 2 MB/1 MB socket buffers (bypasses default fast-fallback tuning); `AutomaticDecompression = All` while issuing byte-range requests (offset-accounting hazard); per-read 30 s/45 s timeouts implemented via two linked CTS per read (allocation churn).
- HTTP/1.1 and HTTP/2 both flow through the same handler; range semantics are validated only by status code (206), not by `Content-Range` span (AUDIT.md P-03).

## 4. Worker scheduling

Static 8 MB chunking into a `ConcurrentQueue`; N workers dequeue until empty ("work stealing" only in the sense that fast workers take more chunks — no dynamic re-splitting of slow chunks). Mid-chunk failures resume from `chunk.start + writtenThisAttempt` within the same process run. Pause cancels per-request CTSs and re-queues active chunk boundaries; resume is cooperative (workers block on `PauseToken`). Retry: infinite, transient-only filter (`HttpRequestException`/`IOException`/`SocketException`/`ObjectDisposedException`), exponential backoff capped at 2 s. Single-stream path mirrors this with its own offset tracking (memory-only).

## 5. File writing

Direct-offset writes via `RandomAccess.WriteAsync(handle, …)` on a `SafeFileHandle` held open by a shared `FileStream` (`FileOptions.Asynchronous`, 4096 buffer — effectively bypassed). Preallocation satisfies the "no concatenation" requirement: segments land at final offsets; completion needs only metadata deletion (a `.part` + atomic rename is not yet used but recommended). Buffering: 256 KB `ArrayPool` buffers (parallel) / 64 KB (single-stream).

## 6. Progress reporting, error handling, recovery (current)

- **Progress:** 250 ms timer, EMA-smoothed MB/s, `ProgressChanged` → `Dispatcher.Invoke` UI updates. Baseline resets per file; cross-file speed bleed possible (P-05).
- **Error handling:** 4xx fails fast (`NonRetryableHttpException`); 5xx/network/IO retry forever; per-chunk generic catch marks `Failed` but is then overwritten by `Completed` (F-02); per-file errors abort the whole batch except cancellation (F-05).
- **Recovery:** `.drmeta` JSON (remaining whole-chunk boundaries) written on `Pause()` and deleted on completion; network-loss flips to `Retrying`. No ETag/Last-Modified capture, no checksum, no per-byte progress, no disk-full fail-fast, no queue persistence (F-03/F-04/F-10/F-11).

## 7. Recovery mechanisms (current, enumerated)

| Mechanism | State |
|---|---|
| Pause → `.drmeta` + re-queued active chunks | Works within a run, whole-chunk granularity only |
| Transient retry with backoff | Works within a run, but accepts short reads (F-01) and retries disk-full forever (F-11) |
| Network-availability events → `Retrying`/resume | Present, cooperative |
| Crash/OS-kill restart | **Broken**: file truncated before metadata read (F-03); single-stream offset lost (F-10) |
| Server-file-change detection | **Absent** (F-04) |
| Batch-level failure isolation | **Absent** (F-05) |

## 8. Proposed future architecture

```
UI (MVVM: MainViewModel, JobViewModel)
 │  binds/commands
 ▼
JobScheduler ──▶ DownloadSession × N ──▶ Engine (range workers, shared)
 │ durable queue store (SQLite)    │ own CTS/state/chunk-map/meta │
 │ global + per-host limits        ▼                              │
 └────────────────────────── SafeFileHandle writes (.part→rename)
```

- **Independent download sessions:** one `DownloadSession` per job owning URL, state machine, CTS, chunk map, metadata path, validators (ETag/Last-Modified), and byte accounting. No shared mutable fields across files (fixes F-06).
- **Persistent download queues:** versioned, checksummed job store with atomic updates; O(1) memory for 10k queued URLs; crash-safe restart at queue and byte granularity (fixes F-03/F-04/F-05/F-10).
- **Global connection management:** central pool budget with per-host caps and throttling awareness feeding all sessions (fixes F-07).
- **Per-host concurrency limits:** derived from observed throughput + 429/503 signals, not a constant.
- **Adaptive segment scheduling:** size- and speed-scaled segments; straggler re-splitting; GET-first probing; `Accept-Encoding: identity` on ranges (addresses P-04/P-07/P-08, F-08).
- **Reliable metadata persistence:** write-temp + fsync + rename; schema version + checksum; `If-Range` revalidation (fixes F-04).
- **Controlled download lifecycle:** explicit states with transitions (`Queued → Probing → Downloading ⇄ Paused/Retrying → Verifying → Completed/Failed/Cancelled`); `Completed` gated on byte accounting + length + optional digest (fixes F-02/F-12); disk-full fail-fast (fixes F-11).
- **Independent presentation layer:** MVVM + messaging; engine unit-testable without WPF; per-job logs and diagnostics export.

Migration order follows ROADMAP.md: integrity (sessions + lifecycle + metadata) → engine tuning → scheduler/concurrency → MVVM UI.

## 9. Post-Ticket #003 architecture deltas (2026-09-30)

Ticket #003 kept the direct-offset, preallocated, chunk-queue architecture and changed the trust model around it. Nothing here introduces segment files, concatenation, a database, or adaptive concurrency.

- **Strict range validation (§4):** every range request carries `Accept-Encoding: identity`; every 206 is checked for status, `Content-Range` presence/unit/span-vs-request/total-vs-session, and ETag/Last-Modified stability captured at probe time. `Content-Length` is never trusted for offsets — expected bytes always equal the requested span. Any 200-to-range triggers a one-time clean fallback (truncate + single-stream); 416 and mismatches fail fast.
- **Exact chunk completion (§5):** `DownloadChunkStrictAsync` succeeds only when all requested bytes are written. Clean-EOF shortfalls and stalls throw `IncompleteChunkException` (transient): with forward progress the remainder is re-requested immediately (streak reset); attempts without progress are bounded by `DownloadRetryPolicy` (default 1 s initial / 30 s max / 5 attempts, exponential backoff with jitter, `Retry-After` honoured and capped).
- **Fault propagation (§6):** first fault recorded (`RecordFault`), siblings cancelled, `WhenAll` awaited, then: fault → `Failed` + original exception rethrown via `ExceptionDispatchInfo`; cancellation → `Cancelled` + `OperationCanceledException`; else the completion gate runs. HTTP 408/429/5xx, resets, read timeouts (now via `WaitAsync`, replacing two linked CTS allocations per read), and transient DNS failures are retryable; other 4xx, invalid ranges, identity changes, disk-full/quota/access-denied (`IsUnrecoverableFilesystemError`) fail immediately.
- **Completion gate (§7):** `Completed` requires no-cancellation, no-fault, `RangeTracker` exact coverage (merge-based interval set; duplicates/overlaps cannot inflate), `_totalDownloaded == totalSize`, and on-disk length == totalSize. No file reread. `RangeTracker` is a standalone internal class ready to be owned by a future session object.
- **Cancellation unification (§8, F-13):** parallel and single-stream paths both propagate `OperationCanceledException` and report `Cancelled`; terminal state mapping added to single-stream; `Cancel()` and `Dispose()` signal workers before teardown. Backoff waits are plain cancellable `Task.Delay` (no `ContinueWith` suppression).
- **Single-stream (§10):** same retry taxonomy, resume-mismatch and mid-run total-change fail fast, final length verified when the size is known, EOF shortfall resumes (offset persists; the old byte-rollback was removed as accounting is now append-only).
- **Test server (§11):** bounded-memory streaming (64 KB pooled buffers, incremental generation, streaming gzip) replaced full-body materialization; new knobs: `Respond200ToRanges`, `ContentRangeOverride`, `RetryAfterSeconds`, identity switching after N requests. All prior profiles preserved.

## 10. Post-Ticket #004 architecture deltas (2026-10-01)

Ticket #004 kept the direct-offset architecture and moved all per-run mutable
state into `DownloadSession` (one object per `StartAsync` call; the shared
`HttpClient`/handler is borrowed, never owned). `ParallelDownloader` is now a
facade: probing, filename resolution, progress reporting, and lifecycle
delegation. No segment files, no concatenation, no database, no scheduler, no
UI redesign, no adaptive concurrency.

- **Sessions (§3):** each session owns URL/paths/validators, segment map,
  `RangeTracker` coverage, active-remainder map, request map, attempt slots,
  pause token + generation, session CTS, retry policy, checkpoint settings and
  telemetry. Cross-file contamination (F-06) is structurally impossible.
- **Recovery metadata v2 (§4):** `{SchemaVersion:2, Url, ResolvedUrl,
  FinalFileName, TotalSize, ETag, LastModified, SegmentSize, Mode,
  CompletedRanges (merged), PrefixOffset, CheckpointUtc, Checksum}` with
  SHA-256 over the canonical payload, atomic tmp+flush+move replacement,
  throttled checkpoints (default 2 s, always on pause/final), bounded budgets
  (transfer 5 s, pause 15 s, finalize 120 s) so wedged storage degrades instead
  of hanging. Only verified coverage is ever persisted, after `FlushFileBuffers`.
- **Non-destructive recovery (§5):** metadata loads and validates BEFORE any
  truncating open; resume reuses the existing `.part` (Open, never Create);
  fresh runs preallocate; filename collisions produce unique names (never
  silent overwrites). Crash-during-finalize adopts the completed final file.
- **Identity (§6):** strong-ETag match preferred; weak ETags require
  Last-Modified agreement; no validators on either side means full restart
  (conservative). First post-recovery request carries `If-Range`; 200/412/416
  triggers clean full restart. Mid-run switching still rejected.
- **Range accounting (§7):** queue entries carry `(Start, End, CreditStart)` so
  pause-split chunks credit their full logical span on success (no recount, no
  orphaned prefix). Drops self-requeue exactly once by the owning worker;
  `Pause()` never requeues (eliminates the duplicate class entirely).
- **Read lifecycle (§8):** every pending read is settled (5 s grace) before its
  buffer is reused; unsettled buffers are abandoned (rented fresh, 256 KB cap),
  never returned early. Per-read allocation is one `WaitAsync` timer, down from
  two linked CTS objects.
- **Pause machine (§9):** internal phases (Probing/Downloading/Pausing/Paused/
  Verifying/Terminal) mapped onto the unchanged public `DownloadState` enum.
  Pause parks via token + request-abort, waits bounded acknowledgement that only
  counts genuinely in-flight attempts (parking never holds slots), then
  checkpoints. Resume validates + unparks. Cancel preempts everything and drops
  partials; Teardown (Dispose) preserves them. Gate/finalize/resume run under
  the pause semaphore with completion early-outs and no-stomp guards, so rapid
  cycles and completion races resolve benignly in every order.
- **Single-stream (§11):** prefix mode with persisted offset, If-Range resume on
  range servers, safe full restart otherwise (truncate-first, never stale tail,
  never false reuse claims).
- **Finalization (§12):** gate → flush → close → same-volume rename (collision
  retried with unique names) → metadata delete. No copy, no rebuild, no
  mandatory hashing. SHA-256 remains test-fixture-only.
- **Test server (§11):** added `StallAfterBytes` (clean read-timeout probe),
  `RemotePort` attribution, `BoundPort` (same-port server restart tests).

## 11. Post-Ticket #004.1 architecture deltas (2026-10-01)

Ticket #004.1 changes only the checkpoint publication path and its test seams;
transfer, scheduling, pause machine, and file lifecycle are untouched.

- **Generation authority (§4):** every checkpoint claims a monotonic generation
  (`Interlocked`, reserved under a short lock with the throttle slot) and
  publishes through a unique `*.drmeta.tmp.<generation>` path. Only a
  generation strictly newer than the last published one may replace the
  canonical snapshot; stale generations delete their own temp files. A
  timed-out caller abandons its task, which settles later under the same
  authority rules — it can never overwrite newer metadata.
- **Validated flush (§6):** `OsFileFlusher` checks the Win32 boolean and throws
  `Win32Exception` preserving the native error code. Flush precedes temp-write
  precedes replace, without exception; failed flushes publish nothing.
- **Observed tasks (§9):** checkpoint bodies catch everything into an outcome
  record (`Published` / `StaleDiscarded` / `Faulted`), so abandoned tasks leave
  no unobserved exceptions. Live temp paths are registered at claim time, so
  the stray sweep can never delete an in-flight temp (the exact race the
  stress test caught during development).
- **Shutdown (§9):** `Dispose` invalidates late publishers, bounded-waits for
  in-flight tasks (5 s), then releases handles; in-flight flushes fail safe on
  disposed handles. Finalize awaits its own (newest) checkpoint within budget
  and fails loudly otherwise — never a false durable `Completed`.
- **Degraded pause (§5):** a failed pause checkpoint still parks cleanly but
  reports `Paused (recovery checkpoint degraded: …; last good snapshot
  retained)` with a flag, instead of implying durability.
- **Seams (§11):** `IDurableFileFlusher` / `IMetadataPublisher` (internal,
  constructor/property-injected fakes in tests only). No public API change.
- **Durability policy (§7):** (A) OS-cache bytes, (B) Win32-confirmed flushed
  bytes, (C) snapshots published strictly after (B) for the snapshotted
  ranges. NTFS same-volume replace is the atomicity assumption, documented in
  code; weaker filesystems would need `FileStream.WriteThrough` + fsync review.

*End of ARCHITECTURE.md.*
