# DRRipper — Developer Guide

## 1. Environment setup

1. Install the official **.NET 10 SDK** (`Microsoft.DotNet.SDK.10` via winget). Verify: `dotnet --version` (baseline: 10.0.401). If a fresh shell cannot find `dotnet`, use `C:\Program Files\dotnet\dotnet.exe` (installer PATH refresh issue, no admin action needed at baseline).
2. Clone `https://github.com/AimJax/DRRipper.git`, work on **`development`** — never push to `main`, never force-push.
3. No additional workloads, secrets, or configuration are required.

## 2. Solution structure (`source/repos/DRRipper/`)

| Project | Purpose | Touches production? |
|---|---|---|
| `DRRipper/` | WPF download manager (authoritative baseline — do not modify without a ticket) | — |
| `DRRipper.TestServer/` | Deterministic Kestrel test server (library + standalone `dotnet run`) | No |
| `DRRipper.Tests/` | xUnit baseline suite (references the app; nothing flows back into it) | No |
| `DRRipper.Benchmarks/` | Throughput harness (references app + server) | No |
| `.github/workflows/ci.yml` (repo root) | CI: build + safe tests (blocking) + known-failure ledger (visible, non-blocking) | No |

## 3. Build, test, benchmark commands

```powershell
dotnet restore source/repos/DRRipper/DRRipper.slnx
dotnet build source/repos/DRRipper/DRRipper.slnx -c Release
# Safe suite (must be green):
dotnet test source/repos/DRRipper/DRRipper.Tests/DRRipper.Tests.csproj -c Release --filter "Category!=KnownFailure"
# Known-failure ledger (expected red until roadmap phases land; never mute):
dotnet test source/repos/DRRipper/DRRipper.Tests/DRRipper.Tests.csproj -c Release --filter "Category=KnownFailure"
# Benchmarks (manual only, never in CI):
dotnet run --project source/repos/DRRipper/DRRipper.Benchmarks -c Release -- --sizes-mb 10,100,1024 --conns 1,2,4,8,16 --iters 3 --out source/repos/DRRipper/DRRipper.Benchmarks/results/bench.md
```

## 4. Local test-server usage

- In-process (preferred): `await TestDownloadServer.StartAsync(new ServerProfile { FileSize = ... })`, then mutate `server.Profile` between runs (throttle, faults, identity switch) and assert against `server.Requests` / `TotalBytesTransmitted`. Ephemeral ports — tests are parallel-safe.
- Standalone: `dotnet run --project DRRipper.TestServer -- --port 5099 --size-mb 100`.
- Scenario knobs: `SupportRanges`, `SendContentLength`, `TruncateAfterBytes` (abort), `ShortBodyBytes` (clean-EOF short body), `BytesPerSecond`, `GlobalStatus`, `RangeFaults`, `WrongContentRangeTotal`, `GzipResponses`, `ConstantContent`, `ETag`/`LastModified`/`Seed` switching.
- Rule: every `ParallelDownloader.StartAsync` call in tests gets an explicit timeout CTS — the baseline engine retries some failures forever.

## 5. Git workflow

- Branch from / target `development`. One ticket = one commit (e.g. `test: establish DRRipper engineering baseline`). Stage explicit paths only — never `git add .`. Preserve unrelated working-tree changes (e.g. the long-standing `links.txt` deletion). Verify production sources are untouched via `git status`/`git diff --stat` before committing.

## 6. Performance regression policy

- Every performance-sensitive change compares medians against BASELINE.md §6 under comparable conditions (same sizes/conns/iters, same class of hardware).
- Measure: throughput, CPU, working set, handles, disk I/O, finalization latency. Investigate any **>5% median-throughput regression**.
- A regression never justifies keeping incorrect behaviour — correctness (Phase 1) outranks speed. Integrity failures are excluded from medians and reported as CORRUPT, never as results. No performance claims beyond the measured loopback setup.
- Since Ticket #004 the reference is DURABLE throughput (physical flush before completion). Buffered numbers are not comparable; never compare across the methodology break (see BASELINE.md Ticket #004 appendix).

## 7. Recovery model (Ticket #004)

- Downloads persist as `<name>.part` + `<name>.part.drmeta` (v2 JSON: schema
  version, URL, resolved URL, filename, size, ETag/Last-Modified, segment
  size, mode, merged completed ranges / prefix offset, timestamp, SHA-256).
- Resume requires: valid checksum, same URL/filename/size/segment-size, an
  intact full-length `.part`, and agreeing validators (strong ETag, else
  Last-Modified; weak ETags need Last-Modified too; none on either side means
  full restart). First post-recovery range request carries `If-Range`.
- `ParallelDownloader` API is unchanged (`StartAsync` returns the FINAL path
  after atomic rename). Tune per downloader: `RetryPolicy`, `ReadTimeout`
  (default 30 s), `CheckpointInterval` (default 2 s).
- Pause is synchronous and bounded; Cancel drops partials; Dispose preserves
  them. Never delete `.part`/`.drmeta` by hand mid-run.
- Kill-driver tests: `DRRipper.RecoveryDriver --url … --dir … --conns N`
  (its stdout protocol is test-only). Same-port server restart
  (`StartOnPortAsync`) gives pristine per-run request logs.

## 8. Checkpoint durability model (Ticket #004.1)

- Every checkpoint claims a monotonic generation and publishes through a
  unique `<meta>.tmp.<generation>` file. Only a generation strictly newer
  than the last published one may replace the canonical snapshot; stale
  generations delete their own temp files. A timed-out caller abandons its
  task, which settles later under the same authority rules and can never
  overwrite newer metadata.
- Publish order per generation: snapshot → data flush (`OsFileFlusher`,
  `Win32Exception` on failure) → temp write + temp flush → authoritative
  replace. Failed flush ⇒ no publish, ever.
- Budgets: transfer checkpoints 5 s (skipped on timeout, data stays valid in
  memory), pause 15 s (degrades loudly, parks anyway), finalize 120 s
  (failure fails the run — never a false durable `Completed`), session-start
  30 s. Throttle interval still defaults to 2 s.
- Temp lifecycle: live generations are registered; the stray sweep never
  touches them; `DeleteAll` removes canonical + legacy `.tmp` + `.tmp.*`
  strays. Startup loads canonical only and ignores all temps.
- Timeout semantics: periodic → keep old snapshot, record fault, continue;
  pause → park + `Paused (recovery checkpoint degraded: …)` flag;
  finalize → `Failed`, part + last-good metadata retained for recovery.
- Test seams (internal only): `IDurableFileFlusher` /
  `IMetadataPublisher` (property-injected fakes), `DownloadSession` counters
  (`CheckpointFaults`, `PublishedGeneration`, `InflightCheckpoints`),
  server `StallAfterBytes` + `RemotePort`/`BoundPort` attribution.

## 9. Scheduler model (Ticket #005)

- Queue lives in SQLite (`%LOCALAPPDATA%\DRRipper\queue.db`; tests inject temp
  paths): `Jobs` table (all §7 fields) + `SchemaVersion` table; WAL,
  `foreign_keys=ON`, `busy_timeout=5000`, `synchronous=NORMAL` (desktop trade-off,
  documented in code). All SQL parameterized; bulk import = one transaction.
  Corrupt/future schemas throw explicitly — never silent reset.
- States: Queued → Downloading ⇄ Paused, Retrying (persisted), Completed/Failed/
  Cancelled (terminal), Interrupted (crash-normalized). Only active jobs own
  runtimes/sessions; queued rows are inert.
- Budgets: `ConnectionBudget(global, perHost)` implements `INetworkPermitGate`;
  sessions acquire per attempt (global-first) and release in `finally` before any
  backoff. Host key = `scheme://host[:port]`, default ports elided, IPv6-safe.
  Redirects are served inside the single request's permit (bounded, documented).
- Admission: FIFO by QueuePosition within Priority; `ActiveDownloadLimit`
  (default 3) caps runtimes; budgets cap segments; per-job conns bound fairness.
- Shutdown: `StopAsync` stops admission, teardowns preserving files, persists
  Interrupted, settles bounded. Cancel drops partials; Remove deletes part/meta
  only (never finals); Pause preserves. No automatic scheduler requeue;
  `RetryJobAsync` gated by `MaxJobAttempts` (default 3).
- Progress: DB summaries only (`.drmeta` is the byte authority); persist ≤2 s,
  UI events ≤~4/s; 10k idle rows ≈ zero DB traffic. URLs redacted in diagnostics
  (`UrlRedactor` strips query/userinfo); engine always uses the exact URL.
- Power: `SchedulerPowerManager` ref-count (unit-tested).
- Test hook: `AbandonForKillTest()` simulates SIGKILL (zero shutdown writes).
- Bench: `dotnet run --project DRRipper.Benchmarks -- --scheduler-jobs 1,2,3,4,8
  --size-mb 100 --conns 4 --iters 3` (integrity outside timed region; CORRUPT excluded).
- Permit microbench: `--permit-bench 10000[,100000]` (sequential acquire/release
  throughput, B/op, host bookkeeping).

## 10. Allocator + progress model (Ticket #005.1)

- Central grant: one FIFO queue + per-host active counters under one short lock;
  grant iff global AND host capacity free; saturated hosts skipped, never blocking.
- Hosts entries stable for allocator lifetime (never removed); 10k ≈ low MB.
- Cancellation removes Pending waiters (no capacity consumed); completed grants keep
  standard wait-completed semantics. Disposal fails pending waiters deterministically
  (`ObjectDisposedException`); permit release never throws, exactly once.
- Progress SQL: `CompletedBytes=MAX(…)`, TotalBytes promotes unknown→known only,
  terminal rows excluded from progress writes, deliberate resets on
  cancel/retry/admission. `.drmeta` remains the byte authority; DB is the summary.
- Redirect attribution unchanged (origin host key per request, bounded); shared-
  HttpClient refactor explicitly deferred.
