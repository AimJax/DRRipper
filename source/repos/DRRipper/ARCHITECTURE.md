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

## 12. Post-Ticket #005 architecture deltas (2026-10-02)

Ticket #005 adds the persistent scheduler layer ABOVE the engine. Transfer,
checkpointing, pause machine, and file lifecycle are unchanged except two
hardening deltas (part-claim registry, bounded finalize retry — both
fail-loud-preserving, see below).

```
MainWindow (thin host) ──events──▶ DownloadScheduler ──owns──▶ JobStore (SQLite)
        │                               │  ├─ ConnectionBudget (global + per-host gate)
        │                               │  └─ ActiveJobRuntime × N (≤ ActiveDownloadLimit)
        │                               ▼
        │                         ParallelDownloader (per job) ──borrows──▶ HttpClient/handler
        │                               ▼
        │                         DownloadSession (+ INetworkPermitGate per attempt)
        ▼
QueueRow models (INPC, Dispatcher-marshaled)
```

- **Job store (§5/§6):** SQLite (`Microsoft.Data.Sqlite`), WAL + `foreign_keys=ON`
  + `busy_timeout=5000` + `synchronous=NORMAL` (documented desktop trade-off),
  single-connection serialized access, all parameterized, single-batch transactions
  for imports, versioned schema (`SchemaVersion` table, idempotent migrate),
  future versions produce an explicit error (never silent reset). Production path:
  `%LOCALAPPDATA%\DRRipper\queue.db`; tests inject temp paths. `*.db*` git-ignored.
- **Job model (§7/§8):** GUID JobId, URL/dir/names, QueuePosition + Priority,
  state, reason, attempts, conns/file, bytes, host key, timestamps. Runtime-only
  objects never persisted. States: Queued/Downloading/Pausing/Paused/Retrying/
  Completed/Failed/Cancelled/Interrupted; crash-active rows normalize to
  Interrupted on Start; recovery reuses `.drmeta` (session authority unchanged).
- **Admission (§9/§11):** FIFO by QueuePosition within Priority; bulk import in
  one transaction with per-line validation + summary; no dedup (independent jobs).
  ActiveDownloadLimit default 3 (1/2/3/4/8 supported); only active jobs own
  runtimes/sessions — 10k queued rows cost zero sessions and ~zero DB traffic.
- **Budgets (§12/§13/§15):** global 16 + per-host 8 (configurable, never
  `int.MaxValue` as policy). Enforcement is per transfer attempt inside
  `DownloadSession` via `INetworkPermitGate` (global-first, host-second; released
  in `finally` before any backoff). Never held across pause/checkpoint/backoff/
  finalize/parking. Fairness = bounded per-job conns × shared FIFO-ish semaphores;
  aggregate scales to disk/server bounds (see BASELINE).
- **Host keying (§14):** `scheme://host[:port]` with default-port elision,
  case-insensitive, IPv6-safe. Attributed per request from the job URL; a
  redirect is served within the single request's permit (bounded leak, documented).
- **Failure isolation (§17, F-05):** per-job try/catch + terminal mapping; a 404
  fails only its job with reason; scheduler loop never stops on a job fault.
- **Progress (§18/§25):** SQLite holds informational summaries only (`.drmeta`
  remains the byte authority). Persisted ≤ every `ProgressPersistInterval` (2 s);
  UI events coalesced ≤ ~4/s. No per-read persistence.
- **Shutdown (§20) / cancel-vs-remove (§21):** Stop = stop admitting + teardown
  preserving files + persist Interrupted + settle bounded; never deletes partials.
  Pause/shutdown preserve; Cancel drops partials (engine semantics) and marks
  Cancelled; Remove deletes part/meta but never final user files.
- **Retries (§22):** session-level transient policy unchanged; scheduler performs
  NO automatic requeue (no retry storms); manual `RetryJobAsync` gated by
  `MaxJobAttempts` (default 3) and touches only that job. Permanent errors (404)
  stay Failed until the user retries.
- **Power (§37):** `SchedulerPowerManager` ref-counts active jobs; sleep prevention
  held while ≥1 active, released at zero (replaces per-transition toggling races).
- **Part-claim registry (F-16, found by T-SCHED-02/09):** concurrent same-filename
  sessions never share a `.part`: live claims (`ConcurrentDictionary`, case-
  insensitive full-path keys) force `ResolveClaimFreePath` allocation (always
  advances, even when the final doesn't exist yet) + `CreateNew` atomic creation.
  Resume still reuses validated parts across restarts (claims are process-local).
- **Finalize retry:** final checkpoint retries ≤3 on filesystem faults (transient
  AV locks under parallel load); persistent faults still throw the ORIGINAL
  exception via `ExceptionDispatchInfo` (T-CKPT-10 green: `Win32Exception` intact).
- **UI (§23/§24):** thin host — `QueueRow` models, Dispatcher marshaling only in
  MainWindow, EMA speed, throttled events. No Dispatcher in engine/scheduler.

## 13. Post-Ticket #005.1 allocator + progress deltas (2026-10-03)

Ticket #005.1 replaces only the permit mechanism and progress SQL; admission,
store schema (still v1), lifecycle, and UI are untouched.

- **Central allocator (§6A):** one global FIFO waiter queue + per-host active
  counters under a single short lock. A grant issues only when global AND host
  capacity are both free — no budget is ever held while waiting (F-18 fixed).
  The scan grants the first ELIGIBLE waiter, so saturated hosts never head-block
  others, while same-host order stays FIFO.
- **Stable host entries (§4):** `Dictionary<host, HostState>` entries live for the
  allocator lifetime — never removed, so no orphaned pool can bypass the cap
  (F-17 fixed). 10k entries retain low-single-digit MB (measured).
- **Cancellation (§9):** token registration removes Pending waiters and cancels
  their TCS; a grant that already completed keeps standard completed-wait
  semantics (owner holds a valid permit). Exactly one outcome per waiter.
- **Disposal (§10):** rejects new work, fails pending waiters deterministically
  with `ObjectDisposedException`, never throws from permit release paths;
  granted permits stay valid and release normally.
- **Permits:** exactly-once release (`Interlocked` flag); no semaphores remain —
  pure counters + TCS, so no primitive-lifetime hazards at all.
- **Monotonic progress (§12/§13):** `CompletedBytes = MAX(…)` in SQL;
  TotalBytes promotes unknown→known and never shrinks; progress writes carry
  `AND State NOT IN (5,6,7)` so late events can't touch terminal rows;
  cancel/retry/admission pass `resetProgress` for deliberate fresh attempts.

## 14. Post-Ticket #006 MVVM queue UI (2026-10-03)

The #005 thin code-behind host (`MainWindow` owning scheduler + rows directly,
`Scheduler/QueueRow.cs`) is replaced by a real MVVM layer. Transfer, scheduler,
and store internals are untouched (one additive `using` alias fix excluded).

```
Views (XAML only)            View-models (headless-testable)      Services
MainWindow.xaml         ---> MainViewModel ──uses──▶ DownloadScheduler
AddDownloadsDialog.xaml ---> AddDownloadsViewModel ──uses──▶ (ImportAsync)
SettingsDialog.xaml     ---> SettingsViewModel ──uses──▶ SettingsService
Styles/{Colors,          DownloadJobViewModel (row: state/glyph/speed/ETA)
 Controls,DataGrid}.xaml RelayCommand/AsyncRelayCommand (no CommandManager)
Views/Converters.cs      Formatting (bytes/rate/ETA/error, pure)
                              │ events via IUiDispatcher
MainWindow.xaml.cs ───────────┤ (composition root only: services, selection
  WpfDispatcher (sole ────────┘  sync, sorting route, dialogs, shutdown)
  Dispatcher touchpoint)
```

- **No WPF in VMs:** no Dispatcher, no `ICollectionView`, no CommandManager, no
  visual types — `ICommand` itself lives in System.ObjectModel. The full VM
  suite runs headless in the existing test project (`UseWPF=false`).
- **Refresh model (§24):** scheduler progress events update rows O(1) in place;
  one 1 s (configurable 250–5000 ms) coalesced timer recomputes aggregates,
  ETAs, counts, permits, and command states. Event-driven view rebuilds
  coalesce to ≤4/s during import bursts; explicit filter/sort/search/refresh
  always rebuild immediately. No per-row timers, no per-row subscriptions.
- **View projection (§11–13):** master `Dictionary<Guid,VM>` + replaced
  `VisibleJobs` collection; filter buckets (Interrupted waits with Queued);
  debounced search (250 ms) across filename/URL/host/status/path; VM-owned
  comparers for Name/Status/Progress/Size/Speed/Added/Host. Visual sort never
  touches `QueuePosition` — reorder commands rebuild the FULL execution order
  preserving non-movable jobs' slots.
- **Virtualization (§22):** DataGrid with explicit
  `VirtualizingPanel.IsVirtualizing=True` + `VirtualizationMode=Recycling`,
  never wrapped in a ScrollViewer; verified by an STA-constructed-window test
  (no render needed for the attached-property proof) + manual scroll pass.
- **Settings (§18–20):** versioned JSON at `%LOCALAPPDATA%\DRRipper\settings.json`,
  tmp+flush+move atomic replace, corrupt/future versions fall back to defaults
  without touching the queue. Limits/defaults/refresh apply live;
  budgets are restart-flagged.
- **Shutdown (§27):** Closing cancels, runs bounded `ShutdownAsync` ("Saving
  download state…" veil), then re-closes via deferred Dispatcher `Close()`
  (re-entrant `Close()` inside `Closing` is illegal WPF and hangs — found by
  live smoke test, fixed, verified graceful exit).
- **Superseded:** `Scheduler/QueueRow.cs` deleted (replaced by
  `DownloadJobViewModel`); old sequential MainWindow loop long gone (F-05).

## 15. Post-Ticket #006.1 tray + close-safety deltas (2026-10-03)

Presentation/lifecycle only — engine, scheduler, and store untouched.

- **ContextMenu fix (§3, F-20):** the DataGrid popup now binds
  `DataContext="{Binding PlacementTarget.DataContext, RelativeSource={RelativeSource Self}}"`.
  Proven by STA tests that resolve all 13 menu commands against a real
  `MainViewModel` (red before, green after).
- **Tray (§5/§9/§14/§15):** `ITrayService` (events + Show/Hide/UpdateStatus/Dispose)
  with `FakeTrayService` (tests) and `WindowsTrayService` (WinForms NotifyIcon,
  one icon/menu/5 s timer, balloon once). Menu routes through a real
  `TrayMenuRouter` into existing VM commands. Tooltip throttled, ≤60 chars,
  never URLs/filenames. Lifetime = window composition; disposed exactly once
  on real exit; survives jobs/filters/hide cycles.
- **Close machine (§6–8/§11/§12):** `CloseController` (Running/Hiding/Exiting/
  Exited) with pure `DecideClose(behavior, hasActive)`; X defers to hide (tray
  intact, scheduler untouched) or begins a single-flight `RequestExitAsync`
  (shutdown → dispose → `Application.Shutdown`, never re-entrant `Close()`).
  Minimize-to-tray optional; restore reuses the same window/VM. Settings add
  `CloseButtonBehavior` (default MinimizeToTray) + `MinimizeToTray`.
- **Placement (§17/§18):** `WindowPlacement` capture/normalize (size clamp,
  maximized round-trip, minimized never restored, off-screen recenter against
  `Screen.AllScreens` work areas); captured + saved at real exit only.
- **Polish (§19–22/§35):** settings dialog scrolls at 125/150%; toolbar scrolls
  horizontally at compact widths (DataGrid itself never wrapped — virtualization
  intact); Add dialog Enter/Escape; Desktop Behavior section with restart notes.

## 16. Post-Ticket #007 browser-integration deltas
Browser handoff above the untouched scheduler trust model. Transfer,
checkpointing, budgets, admission, and store durability are unchanged except
additive deltas (job columns v1→v2, optional request-context plumbing).

```
Extension (MV3 JS) ──stdio framing──▶ NativeHost.exe ──pipe framing──▶ Bridge
  context menu + interception (cancel ONLY after ack)   validate+forward
  minimal permissions (no cookies/tabs/host)            launch --background
                                                                              ▼
MainWindow (bridge owner, tray/placement unchanged) ◀── DownloadScheduler
  BrowserBridgeService (pipe server, idempotency, validation) ──▶ EnqueueBrowserAsync
        │                                                              │ same queue path
        ▼                                                              ▼
  Settings Browser Integration panel                      ActiveJobRuntime ──▶ RequestContext
  Details pane Source indicator                                (Referer/allowlisted headers)
```

- **Contract (§14):** versioned JSON (`version/type/requestId/url/
  suggestedFileName/referrer/source/headers/cookies` → `accepted/jobId/
  errorCode/message/duplicate/warnings`), shared by stdio + pipe hops in
  `DRRipper.BrowserProtocol`. Both ends validate.
- **Validation (§4):** http/https URLs only; closed header allowlist
  (Referer/User-Agent/Accept/Accept-Language/Authorization) with transport
  names dropped; cookie shape-checked but never applied
  (`cookies-deferred-to-012`, #012 owns transfer); referrer host-only
  persisted; 1 MB caps; filename sanitization (traversal/ADS/device/
  trailing-dot/255-cap) with engine re-sanitize at path build.
- **Store (§15):** schema v2 adds `SourceApplication/ReferrerHost/
  BrowserRequestId` (+ index); headers/cookies deliberately transient.
  v1 databases migrate; future versions still error explicitly.
- **Engine (§18):** `BrowserRequestContext.ApplyTo` writes only the safe set
  onto every probe/transfer request. Resume-after-restart falls back to
  persisted-host Referer. Authorization/cookie VALUES never reach
  logs/exceptions/UI/DB (`DescribeForDiagnostics` names-only).
- **Bridge (§11/§13/§27/§28):** local-only pipe server, 10-min idempotency
  (memory + persisted key), ping support, accept/reject counters, 30 s
  per-client bound, orderly stop. No engine internals exposed.
- **Single instance (§12/§29/§30/§44):** session mutex + in-process latch;
  second GUI exits after bridge ping; native host launches
  `--background --browser-bridge` (tray-only, no flash) with 45 s retry.
  One desktop serves all handoffs.
- **UI (§24/§46):** settings section (per-browser status/install/remove,
  extension-ID field, refresh/test); Details pane `SourceDisplay`
  (`Manual` vs `Chrome · host`); bridge lifecycle inside MainWindow
  composition (created after scheduler, stopped before disposal).

## 17. Post-Ticket #008 maximum-throughput deltas

Transfer, recovery, budgeting mechanism, and store durability unchanged;
additive deltas only (mode enum, optional concurrency gate, schema
untouched — still v2).

```
Settings (TransferMode + ceilings) ──▶ SchedulerSettings ──▶ ActiveJobRuntime
  Balanced: fixed N workers/job            │  share cap = 32/active (8..32)
  Maximum: adaptive gate + controller ─────┘  gate = central allocator (unchanged)
                                                      ▼
DownloadSession: fixed worker tasks × cap, dequeue gated by target;
  MaybeAdapt() per chunk (2 s cadence, serialized) → controller → gate.
  Probe + transfer requests carry RequestContext; effective host
  re-attributed after redirects. Scheduler-path clients share one
  process handler (per-job clients keep isolated defaults).
```

- **Modes (§5):** `TransferMode.Balanced` (fixed workers, 16/8 budgets —
  bit-identical scheduling to pre-#008) vs `MaximumThroughput` (adaptive
  gate, 32/32/32 preset applied explicitly by the UI, never silently).
- **Controller (§6–§9):** `AdaptiveConcurrencyController` (pure): ladder
  4→8→16→24→32, 2 s windows from verified bytes, +10% gain steps, optimistic
  probes while per-connection health holds, two-window validation (warm-up
  grace + dual bar: aggregate gain AND per-conn health, else revert with a
  lengthening probe block), gradual backoff on regression/throttle/
  fault-storm, 3-window cooldown. Evaluation serialized under a lock such
  that worker completion bursts cannot corrupt windows or gate accounting
  (found live during development).
- **Gate (§19):** `SemaphoreSlim` around chunk dequeue; up releases, down
  drains (partial drains retried next window); parked workers hold no
  attempt slots (pause ack unaffected); disposed at run end.
- **Share (§12/§53):** per-file ceiling divided among actual active jobs
  at start (32/16/10/8… floor 8) so one file saturates alone while eight
  files behave like Balanced; dominance within a share via adaptation.
- **Teardown hardening (found by 32-conn tests):** dispose-during-probe now
  surfaces `OperationCanceledException` (Interrupted, never spurious
  Failed+`ObjectDisposedException`); same guard on both transfer loops.
- **Redirects (§39):** `NoteEffectiveHost` re-attributes the budget key to
  the effective host for all later attempts (in-flight permit kept,
  released normally).
- **Settings/UI (§22/§41/§51):** `TransferMode` + `MaxConnectionsPerFile`
  persisted (old files default cleanly); Network/Performance section with
  mode combo (Maximum applies the visible 32/32/32 preset), two ceiling
  combos, and the bandwidth warning. Mode/ceiling apply to newly started
  jobs live; allocator ceilings still need restart.

*End of ARCHITECTURE.md.*

*End of ARCHITECTURE.md.*

*End of ARCHITECTURE.md.*
