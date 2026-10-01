# DRRipper — Development Roadmap

Principle: **reliability before optimisation**. No performance phase may begin until Phase 1 acceptance criteria pass. References like F-01/T-INT-03 point to AUDIT.md and TEST_PLAN.md.

---

## PHASE 1 — Download Integrity and Reliability

**Objectives.** Make `Completed` mean *every byte verified present*; make pause/resume/crash recovery lossless; fail fast and loudly on unrecoverable errors.

**Required changes.**
1. Restore strict chunk acceptance (`written >= expectedChunkBytes`, F-01) + per-response `Content-Range` validation (P-03).
2. Fault propagation: worker errors collected, siblings cancelled on non-transient failure, `Completed` gated on byte accounting == `totalSize` == on-disk length (F-02, F-12). Consider returning a result object instead of bare path.
3. Resume redesign: load + validate `.drmeta` (URL, size, ETag/Last-Modified, chunk map, checksum) *before* opening the file; `OpenOrCreate` on resume, `Create` only when fresh (F-03, F-04, F-10).
4. Disk-full / access-denied fail-fast via `HResult` inspection (F-11).
5. Per-file try/catch in the batch loop so one failure doesn't abort the queue (F-05); stop disposing the shared downloader mid-flight (F-06); dispose/recreate `_internalCts` correctly (F-09).
6. Range + decompression: send `Accept-Encoding: identity` on ranged requests (F-08).

**Dependencies.** None (first milestone). Needs .NET 10 SDK + local test server (TEST_PLAN.md §1).

**Technical considerations.** Keep the single-file direct-offset architecture; `.part` temp name + atomic rename on completion is acceptable and recommended. `If-Range` revalidation on resume. Backoff with jitter stays, but only for transient faults.

**Acceptance criteria.** All T-UNIT, T-INT, T-REC recovery/integrity tests green; forced kill mid-download resumes without retransmitting verified bytes; truncated-chunk and fault-injection tests produce byte-identical files or clean `Failed`.

**Testing requirements.** TEST_PLAN.md suites: unit, integration (T-INT-01…10), recovery (T-REC-01…06).

**Potential risks.** Stricter acceptance may surface previously hidden server incompatibilities (non-206 responses) — handle by clean fallback to single-stream, not by loosening checks.

## PHASE 2 — Download Engine Performance

**Objectives.** Maximise sustained throughput and minimise CPU/memory/disk overhead, guided by measurement.

**Required changes.**
1. Bounded connection pooling + per-host concurrency limits; replace `MaxConnectionsPerServer = int.MaxValue` (F-07).
2. Adaptive segmentation: scale worker count and chunk size by file size and observed throughput; dynamic splitting of straggler chunks (P-08).
3. Cut per-read allocations (single per-request CTS + sliding timeout instead of two linked CTS per read).
4. Rationalise socket buffers (measure BDP; 64–256 KB starting point) and re-evaluate the custom `ConnectCallback` vs default Happy-Eyeballs (P-04).
5. Remove the HEAD+probe double round-trip where possible (GET-first probing, P-07); keep preallocation (`SetLength`) but after metadata validation.
6. Reduce progress/reporting overhead; size destination buffering deliberately.

**Dependencies.** Phase 1 (optimising an untrusted engine wastes effort and hides corruption).

**Technical considerations.** Benchmark every change against the local harness (T-PERF-01…05); do not assume more concurrency = more speed; watch CDN throttling signals.

**Acceptance criteria.** Documented throughput/memory/CPU/disk-I/O deltas vs Phase-1 baseline on small/medium/large files; no integrity-test regressions.

**Testing requirements.** T-PERF suite + full re-run of T-INT/T-REC after each tuning step.

**Potential risks.** Over-tuning to one server/disk; mitigations: test matrix (localhost, LAN, throttled profiles, HDD vs SSD).

## PHASE 3 — Persistent Bulk Download Architecture

**Objectives.** Support 100 → 10,000 URL queues with persistence, concurrency, failure isolation, and restart recovery.

**Required changes.**
1. Dedicated download-session objects (own state, CTS, chunk map, metadata) replacing shared mutable fields (F-06).
2. Persistent job scheduler: durable queue (SQLite/JSON store) with per-job status, retry policy, and crash-safe restart.
3. Global connection manager: total + per-host concurrency caps, host throttling awareness.
4. Concurrent downloads (configurable degree) with independent pause/resume/cancel per job.
5. Per-host ETag/Last-Modified tracking and bandwidth accounting.

**Dependencies.** Phases 1–2 (session correctness and engine efficiency).

**Technical considerations.** Schema-versioned metadata; atomic writes (write-temp + rename) for the job store; backpressure so 10k queued URLs cost O(1) memory until scheduled.

**Acceptance criteria.** 10k-URL queue persists across process kill and resumes correctly; one failing job never affects others; memory/handles stable over full batch (T-STRESS suite).

**Testing requirements.** T-STRESS-01…04, T-REC restart tests at queue level.

**Potential risks.** Store corruption — mitigate with checksums + journaling; scope creep — keep the scheduler boring and file-based initially.

## PHASE 4 — Professional Desktop Interface

**Objectives.** Decouple UI from engine (MVVM), support bulk workflows, and integrate with Windows professionally.

**Required changes.**
1. MVVM: views bind to session/queue view-models; engine exposes observable state, never touches `Dispatcher` directly.
2. Queue UI: multi-select, per-job progress/speed/status, retry/remove/reorder, batch import from text/clipboard/file.
3. Global affordances: pause-all/resume-all, speed limiter, save-location templates, notifications, system-tray/minimise, power-management integration.
4. Logging surface (per-job log + diagnostics export).

**Dependencies.** Phase 3 view-models ride on the session/scheduler abstractions.

**Technical considerations.** Keep code-behind thin; progress throttling to avoid UI-thread flooding at high speeds.

**Acceptance criteria.** UI remains responsive during max-throughput multi-job runs; all engine states representable and controllable from the UI.

**Testing requirements.** UI automation smoke tests + manual checklist; state-transition coverage (T-UNIT-07/08 equivalents at VM level).

**Potential risks.** Over-design; keep Phase 4 to parity + queue management, defer theming/extras.

## PHASE 5 — Browser Integration

**Objectives.** Capture downloads from browsers like IDM does.

**Required changes.** Browser extension(s) + native-messaging host that forwards URLs/cookies/headers to DRRipper's scheduler; URL protocol handler; cookie/auth forwarding without persisting secrets to disk unencrypted.

**Dependencies.** Phase 3 scheduler (ingest API) and Phase 4 job management.

**Technical considerations.** Per-browser extension review policies; signed native host; least-privilege header forwarding; user consent per capture rule.

**Acceptance criteria.** One-click capture from Chromium/Firefox builds a correctly attributed job (filename, referrer, cookies) that downloads to `Completed` under test.

**Testing requirements.** Extension/host contract tests; credential-handling security tests (T-SEC-02/03).

**Potential risks.** Browser API churn; security review burden — keep the host minimal and auditable.

## PHASE 6 — Production Release Engineering

**Objectives.** Ship a trustworthy, installable, updatable product.

**Required changes.**
1. Versioning discipline (SemVer; current `0.7.0` tag vs `Beta v0.9` folders is already confusing — reconcile).
2. Signed installer (MSIX/Inno), update channel, crash reporting (opt-in), and diagnostics bundle.
3. CI: build + test suites on every PR; benchmark regression gates; secret scanning.
4. Publish hygiene: parameterise the absolute `PublishDir`, document profiles, reproducible builds.
5. Docs: user guide, troubleshooting, privacy/security notes.

**Dependencies.** All prior phases; CI should start running Phase-1 tests as soon as they exist (don't wait for Phase 6 to create CI).

**Technical considerations.** Code signing certificate handling in CI secrets; symbol packages for crash triage.

**Acceptance criteria.** Clean install/uninstall/upgrade on a fresh Windows VM; signed binaries; green CI with tests + benchmarks.

**Testing requirements.** Install-matrix tests; upgrade tests; full TEST_PLAN.md pass as release gate.

**Potential risks.** Certificate/procurement lead times; start early.

---

## Recommended first milestone

**Phase 1, items 1–3 (F-01 + F-02/F-12 + F-03):** strict chunk acceptance, gated completion, and non-destructive resume. These three convert DRRipper from "fast but untrusted" to "correct", unlock every later phase, and each carries a regression test. Estimated order within the milestone: F-02/F-12 scaffolding first (so failures become visible), then F-01, then F-03/F-04.

---

## Ticket #004 status (2026-10-01)

Phase 1 integrity items are complete (Tickets #002–#004): strict acceptance,
fault propagation, completion gate, bounded retry, and now persistent
crash recovery with session isolation (F-03/F-04/F-06/F-10 resolved;
see AUDIT.md §12). Remaining Phase 1 work deferred to the bulk-scheduler
milestone: per-file failure isolation across queues (F-05) and queue-level
validator tracking (ROADMAP Phase 3 item 5). Connection management (F-07)
and adaptive segmentation stay in Phase 2. No roadmap restructuring needed.

*End of ROADMAP.md.*
