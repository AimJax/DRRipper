# DRRipper — Technical Audit

- **Date:** 2026-09-30 (UTC)
- **Scope:** `source/repos/DRRipper/` (authoritative baseline) vs `source/repos/DRRipper Beta v0.1` … `v0.9` (history only)
- **Baseline files reviewed:** `DRRipper/ParallelDownloader.cs` (1073 lines), `DRRipper/MainWindow.xaml.cs` (265 lines), `DRRipper/MainWindow.xaml`, `DRRipper/App.xaml(.cs)`, `DRRipper/AssemblyInfo.cs`, `DRRipper/DRRipper.csproj` (v0.7.0, `net10.0-windows`, WPF), `DRRipper/Properties/PublishProfiles/FolderProfile.pubxml`
- **Task type:** Audit only. No application source was modified.
- **Pre-existing repo state:** `git status` shows one unrelated pre-existing change — `deleted: source/repos/links.txt` (unstaged). It was preserved untouched and excluded from the audit commit.

---

## 1. Executive summary

DRRipper is a WPF (.NET 10) download manager with a parallel HTTP-range engine that writes segments directly into a single preallocated destination file via `RandomAccess` — i.e. it already satisfies the non-negotiable "no concatenation" requirement. Connection reuse is preserved by sharing one `HttpClient`/`SocketsHttpHandler` across a batch.

However, the engine is **not currently trustworthy**: two confirmed critical defects mean an incomplete or corrupted file can be reported as **Completed** (F-01 premature chunk acceptance — a regression introduced after Beta v0.9; F-02 worker failures swallowed and overwritten by an unconditional `Completed` state). A third critical defect (F-03) makes true resume impossible because the destination file is truncated **before** persisted metadata is consulted. Until Phase 1 of `ROADMAP.md` is implemented, DRRipper must not be presented as a reliable download manager.

- **Confirmed Critical:** 4 (F-01 … F-04)
- **Confirmed High:** 8 (F-05 … F-12)
- **Potential (needs controlled testing):** 10 (P-01 … P-10)
- **Build verification:** NOT possible — no .NET SDK is installed in this environment (see §9). No runtime testing was performed; nothing below is claimed from execution.
- **Historical regression:** exactly one source delta exists between Beta v0.9 and the current baseline, and it is a reliability regression (F-01). `MainWindow.xaml.cs` and `DRRipper.csproj` are byte-identical (SHA-256) between v0.9 and current.

---

## 2. Current architecture (summary; full detail in ARCHITECTURE.md)

Single `MainWindow` code-behind drives a batch loop that sequentially awaits `ParallelDownloader.StartAsync(url, dir, 8, ct)` per URL, reusing one `ParallelDownloader` (hence one `HttpClient`) for the batch. `StartAsync` probes with HEAD then a `Range: 0-0` GET, preallocates the file with `FileMode.Create` + `SetLength`, then either single-streams (unknown size / no range support) or fans out N=8 workers over a `ConcurrentQueue<(start,end)>` of fixed 8 MB chunks. Workers issue `Range` GETs, stream through pooled 256 KB buffers, and write with `RandomAccess.WriteAsync` at absolute offsets. Pause is cooperative (`PauseToken` + per-request CTS cancellation); resume metadata (`.drmeta` JSON) records remaining chunk boundaries only. Progress is reported on a 250 ms timer with EMA-smoothed speed.

---

## 3. Confirmed defects

Severity scale: **Critical** = silent data corruption or data loss. **High** = batch failure, hang, leak, or integrity gap that is at least fail-noisy. **Medium/Low** = hardening.

### F-01 — Premature chunk acceptance on early EOF (CRITICAL, REGRESSION vs v0.9)

- **File:** `DRRipper/ParallelDownloader.cs`
- **Method/location:** worker read loop, lines ~777–783
- **Explanation:** When the server closes the stream (`read <= 0`), the current code treats the chunk as successful if **any** byte was received (`if (written > 0 || written >= expectedChunkBytes) break;`). Beta v0.9 required `written >= expectedChunkBytes`. A throttled, truncated, or connection-reset response therefore leaves a hole of unwritten (zero-filled, preallocated) bytes that is never retried and never detected.
- **Reproduction:** Serve a file with range support from a controlled local server; abort the response body mid-chunk (e.g. close after 20% of an 8 MB chunk). The download still reaches `Completed`; the output contains a zero region.
- **Correction:** Restore the v0.9 condition (`written >= expectedChunkBytes`, else throw `IOException` to trigger the resume-retry path). Additionally verify `Content-Range` per response (see P-03) and add a final size/byte-accounting gate (F-12).
- **Regression test:** TEST_PLAN.md T-INT-03 (truncated chunk body must retry and produce a byte-identical file).
- **Impact if unfixed:** Silent corruption of any download that encounters a mid-chunk disconnect — the exact scenario a download manager must survive.

### F-02 — Worker exceptions swallowed; `Failed` overwritten by unconditional `Completed` (CRITICAL)

- **File:** `DRRipper/ParallelDownloader.cs`
- **Method/location:** worker `catch (Exception ex)` lines ~847–851; completion lines ~856–862
- **Explanation:** A per-chunk `catch (Exception)` calls `SetState(Failed, …)` but then **continues the worker loop** to the next chunk, and `Task.WhenAll` therefore completes successfully, after which `SetState(DownloadState.Completed)` unconditionally overwrites the failure and the `.drmeta` file is deleted. A failed segment is indistinguishable from success to the caller (`StartAsync` returns a path normally).
- **Reproduction:** Point one chunk's range at a server that returns 500 for that range (or inject a fault); observe state flicker `Failed → Completed` and a corrupt output with no exception surfacing to `MainWindow`.
- **Correction:** Record worker faults (e.g. `ConcurrentQueue<Exception>` / first-fault `TaskCompletionSource`), cancel sibling workers on non-transient failure, and after `WhenAll` rethrow or return a result object; only enter `Completed` when every byte is accounted for (F-12).
- **Regression test:** T-INT-04, T-UNIT-05.
- **Impact:** Any non-transient segment failure is reported as a successful download.

### F-03 — Destination truncated before resume metadata is consulted; resume impossible (CRITICAL)

- **File:** `DRRipper/ParallelDownloader.cs`
- **Method/location:** `StartAsync`, preallocation lines ~601–606 executes **before** metadata load lines ~629–648
- **Explanation:** `new FileStream(outputPath, FileMode.Create, …)` + `SetLength(totalSize)` destroys all previously downloaded bytes, and only afterwards is `outputPath + ".drmeta"` read. Restarting an interrupted download therefore discards the partial file and re-downloads from zero (while the metadata describes the *previous* run's remaining chunks). Crash/OS-shutdown recovery retransmits everything.
- **Reproduction:** Start a large download, kill the process mid-way (`.drmeta` exists, partial file exists), restart the same download. Observe the partial file truncated to zero-filled `totalSize` and full retransmission.
- **Correction:** Load and validate metadata (URL, size, ETag/Last-Modified once captured — F-04) **before** opening the file; open with `FileMode.OpenOrCreate` (never `Create`) when resuming; only preallocate when starting fresh; reconcile on-disk length with metadata.
- **Regression test:** T-REC-01, T-REC-02.
- **Impact:** No crash/network recovery of partial data; wasted bandwidth; violates product vision items 6 and 8.

### F-04 — Resume metadata has no file-identity validation and records no per-byte progress (CRITICAL)

- **File:** `DRRipper/ParallelDownloader.cs`
- **Method/location:** `DownloadMetadata` lines ~45–53; `SaveMetadata` lines ~399–423; resume rebuild lines ~635–648
- **Explanation:** (a) ETag / Last-Modified are never captured, so a changed server file is never detected — stale chunks can be stitched into a different file version. (b) `ChunkDownloaded` is always serialized as `0` and the queue stores only whole-chunk boundaries, so intra-chunk progress is lost on pause/crash. (c) No checksum of the metadata itself; a corrupt `.drmeta` is either silently ignored (`catch → null`, full re-download) or trusted blindly.
- **Reproduction:** Download half of file V1, replace the server file with same-length V2, resume. Output is a V1/V2 hybrid reported `Completed`.
- **Correction:** Persist URL, total size, ETag, Last-Modified, chunk size, per-chunk completed bytes, and a metadata checksum; validate all on load and discard/resync on mismatch (server revalidation via `If-Range`/`If-None-Match`).
- **Regression test:** T-REC-03, T-REC-04.
- **Impact:** Silent file corruption across resume whenever the origin changes.

### F-05 — One bad URL aborts the entire batch (HIGH)

- **File:** `DRRipper/MainWindow.xaml.cs`
- **Method/location:** batch loop lines ~107–142; per-file `catch (OperationCanceledException)` lines ~138–141 only
- **Explanation:** Any non-cancellation exception from `StartAsync` (e.g. 4xx `NonRetryableHttpException`, DNS failure, disk error) propagates out of the `for` loop to the outer `catch`, abandoning all remaining URLs. For the 100–10,000 URL scalability target this is a batch-killer: a single dead link stops the queue.
- **Reproduction:** Queue `[good, 404-url, good]`; the third file is never attempted.
- **Correction:** Catch per-file exceptions inside the loop, record per-file status, continue; surface a batch summary. (Part of the persistent job-scheduler work in Phase 3.)
- **Regression test:** T-INT-09, T-STRESS-02.
- **Impact:** No failure isolation; bulk downloading is unusable with real-world URL lists.

### F-06 — Shared downloader state across batch files; Cancel/Dispose race (HIGH)

- **File:** `DRRipper/MainWindow.xaml.cs` lines ~87–105, ~244–263; `DRRipper/ParallelDownloader.cs` lines ~60–81, ~323–348
- **Explanation:** One `ParallelDownloader` is reused for all files, so `_currentChunks`, `_currentMetaPath`, `_internalCts`, `_totalDownloaded`, `_activeChunks` are overwritten per file. `CancelButton_Click` calls `_downloader.Cancel()` then immediately `Dispose()`s (tearing down the shared `HttpClient`) while `StartAsync` may still be awaiting — racing into `ObjectDisposedException` on in-flight requests. Pause/Resume similarly act on whatever file is current.
- **Reproduction:** Cancel during the 2nd file of a batch; observe `ObjectDisposedException` surfacing as "Download failed" instead of a clean `Cancelled`.
- **Correction:** Short term: don't dispose until the batch loop exits; guard `Cancel`/`Dispose` with a generation counter. Long term: independent download-session objects (Phase 3, ARCHITECTURE.md §5).
- **Regression test:** T-UNIT-08, T-INT-10.
- **Impact:** Unclean cancellation; cross-file state corruption on pause/cancel.

### F-07 — Unbounded `MaxConnectionsPerServer` and oversized socket buffers (HIGH)

- **File:** `DRRipper/ParallelDownloader.cs`
- **Method/location:** ctor lines ~133–143 (handler), ~168–173 (`ConnectCallback`)
- **Explanation:** `MaxConnectionsPerServer = int.MaxValue` removes all pooling backpressure; combined with 2 MB receive + 1 MB send buffers per socket, high connection counts (or a 10k-URL batch with redirects/retries) risk socket exhaustion, large non-paged-memory use, and triggering server/CDN throttling ("tarpitting" the code comment itself worries about). Throughput does not scale monotonically with connections (see §7 performance).
- **Reproduction/measurement:** Benchmark throughput vs connection count against a local server while monitoring socket count and memory; expect plateau then degradation.
- **Correction:** Restore a bounded pool (tune empirically, e.g. start at per-host limit 6–8 with a global cap), set buffers to measured BDP-appropriate values (64–256 KB), and add per-host concurrency limits (Phase 2/3).
- **Regression test:** T-PERF-01, T-PERF-02.
- **Impact:** Resource exhaustion and *lower* real-world throughput via throttling.

### F-08 — `AutomaticDecompression = All` interacts unsafely with range requests (HIGH)

- **File:** `DRRipper/ParallelDownloader.cs`, line ~138
- **Method/location:** ctor handler config vs all `Range` requests
- **Explanation:** With decompression enabled, `HttpClient` advertises `Accept-Encoding: gzip, deflate, br` and transparently decompresses. `Content-Length`/`Content-Range` then describe the *encoded* transfer while the engine accounts *decoded* bytes at absolute file offsets — a mismatch that can corrupt offsets or break the `expectedChunkBytes` accounting (F-01's clamp logic trusts `Content-Length`). Many servers also ignore/alter ranges for encoded responses.
- **Reproduction:** Download a compressible resource from a server that encodes range responses; compare output hash.
- **Correction:** Disable automatic decompression for ranged downloads (send `Accept-Encoding: identity` on range requests) or restrict decompression to the single-stream identity path; verify with T-INT-02.
- **Regression test:** T-INT-02.
- **Impact:** Potential silent corruption on servers that compress range responses.

### F-09 — `_internalCts` never disposed; recreated per file (HIGH)

- **File:** `DRRipper/ParallelDownloader.cs`, lines ~60, ~675
- **Method/location:** field `_internalCts`; assignment in `StartAsync`
- **Explanation:** Each `StartAsync` call replaces `_internalCts` with a new linked CTS without disposing the previous one. Over a 1,000–10,000 URL batch this leaks native handles and linked-token registrations.
- **Reproduction:** Run a large batch; observe handle-count growth in Process Explorer / performance counters.
- **Correction:** Dispose the previous CTS before reassignment (and on completion/cancel); manage CTS lifetime per download session.
- **Regression test:** T-STRESS-04 (handle/memory stability over N files).
- **Impact:** Handle/memory growth proportional to batch size.

### F-10 — Single-stream resume offset is memory-only; restart corrupts the file (HIGH)

- **File:** `DRRipper/ParallelDownloader.cs`
- **Method/location:** `SingleStreamDownload`, lines ~878–1015 (`writeOffset` local; `FileMode.OpenOrCreate` line ~900)
- **Explanation:** `writeOffset` is rebuilt from zero on every `StartAsync`. A restarted unknown-size/no-range download opens the existing file with `OpenOrCreate` and overwrites from byte 0, leaving a stale tail if the new transfer is shorter — a corrupt file of plausible length. (Within a single run, retry works; across runs it does not.)
- **Reproduction:** Interrupt a single-stream download halfway, restart it, let the server return fewer bytes; tail of the old run remains.
- **Correction:** Persist single-stream progress (offset + validators) like the parallel path, or truncate-then-full-download atomically with a `.part` + rename; verify final length.
- **Regression test:** T-REC-05.
- **Impact:** Silent corruption for the non-range fallback path.

### F-11 — Disk-full and other non-transient I/O errors retried forever (HIGH)

- **File:** `DRRipper/ParallelDownloader.cs`
- **Method/location:** worker retry filter line ~817; single-stream retry line ~988; backoff capped at 2 s lines ~837, ~998
- **Explanation:** The retry filter catches `IOException` indiscriminately. `IOException` from `RandomAccess.WriteAsync` on a full disk (or read-only media, quota, path-too-long) will never succeed on retry, yet the engine loops forever with ≤2 s backoff, stuck in `Retrying`. `EnsureEnoughDiskSpace` only checks once before preallocation.
- **Reproduction:** Fill the target disk (or use a tiny RAM disk) and start a download; the app hangs in `Retrying` instead of failing.
- **Correction:** Inspect `IOException.HResult` (e.g. `ERROR_DISK_FULL 0x70`, `ERROR_HANDLE_DISK_FULL 0x27`) and fail fast with a clear message; re-check free space on retry backoff.
- **Regression test:** T-REC-06.
- **Impact:** Indefinite hang on an unrecoverable condition.

### F-12 — No completion gate: size/byte-accounting never verified before `Completed` (HIGH)

- **File:** `DRRipper/ParallelDownloader.cs`, lines ~856–862
- **Method/location:** post-`WhenAll` block
- **Explanation:** `Completed` is entered when the chunk queue drains and workers exit — not when `bytes written == totalSize`. Combined with F-01/F-02, shortfalls are invisible. There is no final `FileInfo.Length` check, no per-chunk byte accounting reconciliation, and no optional hash verification.
- **Reproduction:** Any F-01/F-02 repro ends in `Completed` with a short/zero-padded file.
- **Correction:** Define Completed = (all ranges acknowledged 206 with matching Content-Range) AND (accounted bytes == totalSize) AND (on-disk length == totalSize); optionally verify checksum when the server supplies `Content-MD5`/`Digest`. See TEST_PLAN.md §2 completion criteria.
- **Regression test:** T-UNIT-05, T-INT-03, T-INT-04.
- **Impact:** The `Completed` state currently certifies nothing.

---

## 4. Potential defects (hypotheses — require controlled testing, NOT confirmed)

| ID | Location | Hypothesis and why it matters | Test to confirm |
|---|---|---|---|
| P-01 | `ParallelDownloader.cs` ~90–91, ~714, ~842 | `_activeRequestCts` keyed by chunk start: after partial-progress shifts `chunk.start`, and on retry `TryAdd(originalStart)` can silently fail on key collision, so `Pause()` may miss cancelling a hung request. | T-UNIT-03 |
| P-02 | `ParallelDownloader.cs` ~790–793 vs ~991 | Parallel path never rolls back `_totalDownloaded` on retry while single-stream does (`Interlocked.Add(-writtenThisAttempt)`); pause/retry accounting may double-count or diverge from on-disk bytes. | T-UNIT-04 |
| P-03 | `ParallelDownloader.cs` ~729–733 | Only the 206 status is validated; `Content-Range` unit/total/span is never parsed per response, so a misbehaving server returning the wrong span is written to the wrong offset. | T-INT-05 |
| P-04 | `ParallelDownloader.cs` ~146–186 | Custom `ConnectCallback` replaces the handler's Happy-Eyeballs/fast-fallback path with sequential IP attempts and sets socket buffers post-connect; may slow connection setup and interact poorly with HTTP/2 multiplexing (`EnableMultipleHttp2Connections`). | T-PERF-03 |
| P-05 | `ParallelDownloader.cs` ~358–395, ~246 | Progress timer and `SetState→ReportNow` race on `_currentTotalSize`/`_smoothedSpeed` (`_timerLock` vs unsynchronised writers); speed may spike/NaN across files in a batch. | T-UNIT-06 |
| P-06 | `ParallelDownloader.cs` ~98–108 | `SanitizeFileName` replaces `..` in a single pass and doesn't handle reserved names (`CON`, `PRN`, `NUL`), trailing dots/spaces, ADS colons, or `MAX_PATH`; crafted `Content-Disposition` could still cause overwrites or failures. | T-SEC-01 |
| P-07 | `ParallelDownloader.cs` ~458–551; `MainWindow.xaml.cs` ~107–142 | Every file pays HEAD + (often) a probe GET before any data flows; at 10k URLs this doubles request load and serial latency with no caching/skip. | T-PERF-04 |
| P-08 | `ParallelDownloader.cs` ~95; `MainWindow.xaml.cs` ~118 | Fixed 8 MB chunks and fixed 8 connections for all sizes: small files pay 8+ range handshakes for kilobytes; multi-GB files create thousands of queue entries and metadata rows. | T-PERF-05 |
| P-09 | `ParallelDownloader.cs` ~188–191, ~438–447 | Global `Referer` (scheme+host) plus default auto-redirect may forward origin information — and any credentials embedded in URLs — to third-party redirect targets. | T-SEC-02 |
| P-10 | `ParallelDownloader.cs` ~237–243 | `SetThreadExecutionState` is toggled per state transition with no ref-counting; overlapping sessions in a future concurrent design could clear sleep-prevention prematurely. | T-UNIT-07 |

---

## 5. Security findings

- **Secrets scan:** searched all tracked source (`*.cs`, `*.xaml`, `*.csproj`, `*.pubxml*`) for `api-key/secret/password/token/connection-string/private-key/aws_/ghp_/Bearer`. **No credentials, tokens, or secret values found.** Only benign matches (`CancellationToken`, pause-token naming). No secret values are reproduced in this report.
- **Publish profile** (`FolderProfile.pubxml`) contains only build settings and a local publish path (`E:\MVS Project\publish\DRRipper Beta v0.9.0`); the `.pubxml.user` contains VS history timestamps only. No remediation needed, but the absolute local path should be parameterised before shared builds.
- **Recommendations (no secret material involved):** harden filename sanitisation (P-06); set an explicit redirect policy with a hop limit and strip `Authorization`/credentials on cross-host redirect (P-09); never log URLs containing userinfo; add `Path.GetFullPath` + containment check so the final path cannot escape the chosen directory.

---

## 6. Reliability assessment

**Not reliable in the current state.** Transfer completeness is not enforced (F-01, F-02, F-12), resume destroys data (F-03/F-04/F-10), unrecoverable errors hang (F-11), and batch failure isolation is absent (F-05). The pause path itself is the most careful part of the code (per-request CTS tracking, active-chunk re-queue), but it preserves whole-chunk boundaries only and races with shared batch state (F-06). Network-availability handling (`Retrying` on loss) exists but cannot compensate for the acceptance of short reads. Reliability must be fixed before any performance work (ROADMAP.md Phase 1 first).

## 7. Performance assessment

No measurements were taken (no SDK/runtime here), so all of the following are **reasoned opportunities, not claims**:

1. **Request overhead dominates small files** — HEAD + probe + 8 range handshakes; consider GET-only probing and connection-count scaling by file size (P-07/P-08).
2. **Unbounded connections likely hurt, not help** — pooling backpressure, per-host limits, and empirical tuning (F-07, T-PERF-01).
3. **Per-read allocation churn** — two linked CTS objects are allocated per 256 KB/64 KB read; a single per-request CTS with a sliding timeout (or `WaitAsync` timeout) would cut GC pressure.
4. **Tiny destination buffer** — the shared `FileStream` is opened with buffer 4096 while all writes bypass it via `RandomAccess`; either drop the stream to a bare `SafeFileHandle` (`File.OpenHandle`) or size buffers deliberately.
5. **Fixed 8 MB chunks** — adaptive sizing (fewer, larger segments on fast disks; dynamic splitting of slow chunks instead of static stealing) should be benchmarked (T-PERF-05).
6. **Preallocation via `SetLength`** is the right call on NTFS (sparse/fast) — keep it, but move it after metadata validation (F-03) and keep the disk-space check.

## 8. Maintainability assessment

All download logic, networking, persistence, progress, and power management live in one ~1070-line class manipulated directly by code-behind; there is no MVVM, no session/job abstraction, no logging, no configuration surface (connection count is a hardcoded `8` at the call site), and only sequential single-file execution. This is serviceable for a prototype but cannot carry per-host limits, persistent queues, concurrent sessions, or testability. Introducing MVVM + a dedicated download-session manager + a persistent job scheduler (as the task brief suggests) is endorsed — scoped as ROADMAP.md Phase 3/4, after integrity is repaired.

## 9. Build verification

- **SDK status:** `dotnet` is not on `PATH`; `C:\Program Files\dotnet\dotnet.exe` does not exist. **No .NET SDK is installed in this environment.**
- **Result:** restore and build could not be performed. No build warnings/errors to record. No runtime testing was performed; no throughput, memory, CPU, disk-I/O, or integrity claims in this audit derive from execution.
- **Recorded SDK version:** none (tooling unavailable — explicit limitation).
- **Recommendation:** install the .NET 10 SDK and re-run `dotnet restore` / `dotnet build` plus TEST_PLAN.md before any Phase 1 code changes, to establish a clean baseline.

## 10. Historical regression analysis

| Comparison | Method | Result |
|---|---|---|
| Beta v0.9 → current `ParallelDownloader.cs` | `git diff --no-index` | **13 insertions, 8 deletions** — confined to the chunk read loop: (1) `expectedChunkBytes` now trusts response `Content-Length` instead of the requested span; (2) EOF with `written > 0` is accepted as success (was: retry unless `written >= expected`); (3) writes clamped to `expectedChunkBytes`. Change (2) is F-01, a **reliability regression**. Change (1) is only safe with Content-Range validation (P-03); change (3) masks over-long responses instead of flagging them. |
| Beta v0.9 → current `MainWindow.xaml.cs` | SHA-256 | **Identical** (`99DC08…AD920F0` both). No UI regression, no UI improvement. |
| Beta v0.9 → current `DRRipper.csproj` | SHA-256 | **Identical** (`C94DC6…70C70C45` both). Still v0.7.0 / `net10.0-windows`. |
| v0.1 … v0.9 trajectory | file sizes 30 561 → 52 920 bytes | Steady engine growth (pause/resume, metadata, retry, single-stream fallback, filename resolution). Nothing was accidentally removed; the betas show incremental hardening, which the current delta partially undoes (F-01). |

No old code should be restored wholesale; the v0.9 EOF condition should be reinstated as part of the F-01/F-12 fix with new tests.

---

---

## 11. Ticket #003 — resolution status (2026-09-30, commit pending)

Sections 1–10 above are the frozen Ticket #001 audit and are left intact.
This section records the new finding discovered during Ticket #002 testing
and the Ticket #003 disposition of each finding. Production fix: strict
range validation, exact chunk completion, fault propagation, explicit
completion gate, unified cancellation, bounded retry (see ARCHITECTURE.md §9).

### F-13 — Parallel-path cancellation swallowed into false Completed (CRITICAL)

1. **Identifier:** F-13 (next in the confirmed-defect sequence).
2. **Severity:** Critical.
3. **Affected file:** `DRRipper/ParallelDownloader.cs` (pre-#003 worker completion block).
4. **Method/location:** `StartAsync` post-`Task.WhenAll` continuation; worker `catch (OperationCanceledException) { break; }` path.
5. **Explanation:** Cancelling the external `CancellationToken` mid-flight made every worker break out of its loop, after which `Task.WhenAll` completed normally and the code unconditionally executed `SetState(Completed)` and returned the output path. The destination file — preallocated, mostly zeros — was therefore reported as a successful download. The single-stream path instead propagated `OperationCanceledException`, so cancellation semantics were also inconsistent across paths.
6. **Reproduction:** T-REC-01 observation (Ticket #002): cancelled at 5.3 MB of 24 MB, engine state `Completed`, path returned.
7. **Correction (Ticket #003):** post-`WhenAll` cancellation check throws `OperationCanceledException` (state `Cancelled`, no path returned); single-stream maps terminal cancellation to `Cancelled` likewise; `Cancel()` + `Dispose()` ordering hardened.
8. **Regression tests:** IntegrityTests I/J/K (cancel parallel / single-stream / during backoff).
9. **Impact if unfixed:** every user or app-level cancel of a parallel download silently certifies a corrupt file.

### Disposition table

| Finding | Ticket #003 status | Evidence |
|---|---|---|
| F-01 premature chunk acceptance | **Resolved** | T-INT-03 (short clean-EOF body now resumes to byte-identical file) |
| F-02 swallowed worker failures | **Resolved** | T-INT-04, L (faults propagate as `DownloadFailedException`; `Failed` never becomes `Completed`) |
| F-03 resume truncation | Unchanged (next ticket) | T-REC-01/05 still KnownFailure |
| F-04 no identity validators | **Partially addressed**: in-run ETag/Last-Modified consistency enforced; cross-run persistence still next ticket | IdentitySwitch rejection test; T-REC-03 still passes |
| F-05 batch abort | Unchanged (scheduler ticket) | — |
| F-06 shared state/Dispose race | **Partially addressed**: CTS disposal + cancel-before-teardown; full session isolation still next ticket | N-locked test; code review |
| F-07 unbounded connections | Unchanged (tuning ticket) | Benchmarks still peak at 2 conns |
| F-08 decompression vs ranges | **Mitigated**: `Accept-Encoding: identity` on all range requests; span fallback retained | T-INT-02 passes uncompressed path |
| F-09 CTS leak | **Resolved** | Previous source disposed before reassignment |
| F-10 single-stream resume | **Refined + partially addressed**: stale-tail path proven unreachable (callers pre-truncate); bounded retry + length check added; persistence next ticket | T-REC-05 still KnownFailure (retransmission) |
| F-11 infinite disk-full retry | **Resolved** | Classifier + fail-fast (unit tests N, integration N-locked) |
| F-12 no completion gate | **Resolved** | RangeTracker + byte + on-disk gate; M unit tests; gate-violation path throws |
| F-13 (new) cancellation swallow | **Resolved** | IntegrityTests I/J/K |
| P-01 request-CTS key collision | **Mitigated**: AddOrUpdate always tracks the live request | Code review |
| P-03 Content-Range ignored | **Resolved** | C/D/E rejection tests |

---

## 13. Ticket #004.1 — resolution status (2026-10-01)

### F-14 — Checkpoint generation race: timed-out checkpoint work can publish stale metadata (HIGH)

1. **Identifier:** F-14 (next in the confirmed-defect sequence).
2. **Severity:** High (stale recovery metadata class).
3. **Affected file:** `DRRipper/DownloadSession.cs` (`Checkpoint`/`CheckpointCore`, Ticket #004 code).
4. **Method/location:** bounded `Checkpoint(bool, TimeSpan)` + `RecoveryMetadata.WriteAtomically` (shared `.tmp` path).
5. **Explanation:** The bounded checkpoint ran IO on a detached task without authority: after a caller timeout, the orphaned task kept writing the single shared `.drmeta.tmp` path and then replaced the canonical snapshot. A slow generation could therefore overwrite newer, already-published metadata, and concurrent generations shared one temp file (torn replace). Found by architectural review; a live variant (stray sweep deleting a live temp) was caught by T-CKPT-STRESS during development.
6. **Reproduction:** T-CKPT-02 (stall → timeout → newer publishes → stale resumes → must discard) fails on the old design; T-CKPT-STRESS fails it reliably.
7. **Correction (Ticket #004.1):** monotonic generations, unique temp per generation, publish-only-if-strictly-newer under a short lock, stale self-cleanup with live-set protection, validated flush before publish, observed task outcomes.
8. **Regression tests:** T-CKPT-01–04 + stress (adversarial ordering suite).
9. **Impact if unfixed:** crash recovery could resume from silently regressed byte maps.

### F-15 — FlushFileBuffers return value ignored (MEDIUM)

1. **Identifier:** F-15.
2. **Severity:** Medium (durability guarantee void if triggered; no known field trigger).
3. **Affected file:** `DRRipper/DownloadSession.cs` (`CheckpointCore`, Ticket #004 code).
4. **Method/location:** `FlushFileBuffers(DestHandle)` invoked with discarded boolean.
5. **Explanation:** A `false` return (flush failed) was swallowed, so metadata could be published describing ranges as durable that the OS never confirmed, voiding the ARCHITECTURE.md §10 guarantee at the Win32 boundary.
6. **Reproduction:** T-CKPT-05 injects `Win32Exception(112)`; old code path publishes anyway.
7. **Correction (Ticket #004.1):** `OsFileFlusher` throws `Win32Exception` preserving `Marshal.GetLastWin32Error()`; publish happens strictly after a successful flush; failures propagate as `DownloadFailedException` (pause degrades loudly, finalize fails loudly).
8. **Regression tests:** T-CKPT-05/06/10.
9. **Impact if unfixed:** unasserted durability on flush failure.

### Disposition table (Ticket #004.1)

| Finding | Status | Evidence |
|---|---|---|
| F-14 checkpoint generation race | **Resolved** | T-CKPT-01–04 + stress |
| F-15 unchecked flush result | **Resolved** | T-CKPT-05/06/10 |
| F-03/F-04/F-06/F-10/F-12/F-13 | Stay resolved; covered by unchanged + new tests | Full suite green |

---

## 12. Ticket #004 — resolution status (2026-10-01)

Sections 1–11 above are frozen and left intact. This section records the
Ticket #004 disposition. Production work: per-download `DownloadSession`
isolation, versioned checksummed recovery metadata (`.part` + `.part.drmeta`,
atomic replace), non-destructive resume with cross-run identity validation,
exact-coverage checkpoints, settled-read lifecycle, controlled pause machine
with run-end mutual exclusion, `.part` → final atomic rename (see
ARCHITECTURE.md §10).

### Disposition table

| Finding | Ticket #004 status | Evidence |
|---|---|---|
| F-03 resume truncation | **Resolved** | T-REC-01 (reuse ratio bounded), T-REC-01-KILL (genuine kill), T-REC-02 early kill; metadata loads before any truncating open |
| F-04 no identity validators | **Resolved** (single-download scope) | Validators persisted; mismatch forces full restart (T-REC-03); in-run switching still rejected; queue-level tracking stays a scheduler-milestone item |
| F-05 batch abort | Unchanged (scheduler ticket — explicitly out of scope) | — |
| F-06 shared state/Dispose race | **Resolved** (single-download scope) | Per-call sessions; Teardown (keep files) vs RequestCancel (drop partials); N-locked test now asserts no-overwrite routing |
| F-10 single-stream resume | **Resolved** | Prefix resume with If-Range on range servers (T-REC-05b); safe full restart otherwise (T-REC-05) |
| F-13 cancellation swallow | Stays resolved; extended with cancel/teardown distinction and terminal-state guards | I/J/K still green; T-STATE-05/06 |
| P-02 progress accounting | **Resolved** | Append-only accounting + credit-carrying queue entries; gate equality enforced (T-UNIT-01, T-INT-COVERAGE) |

### New observations (test-infrastructure grade, not product defects)

- **Abort-race byte overcount:** server-side transmitted totals overcount bytes
  the client never consumed when aborts race full-speed streaming. Integrity
  tests therefore assert client-observable contracts (request spans, resume
  offsets, hashes) instead of transmitted ratios wherever aborts are involved.
- **Host scheduling stalls:** this development box exhibited multi-second to
  minutes-long single-thread stalls with healthy sibling timers (proven by
  bounded-wait telemetry + watchdog heartbeats). All engine waits are bounded;
  rapid timing test T-STATE-04 is quarantined as environment-sensitive (CI
  non-blocking) until it runs on healthy hardware. No product change can fix a
  frozen thread; the mitigation is fail-fast bounds everywhere (semaphores,
  ack, checkpoint budgets, test wrappers).

---

## 14. Ticket #005 — resolution status (2026-10-02)

### F-05 — One bad URL aborts the entire batch (HIGH) — RESOLVED

1. **Identifier:** F-05 (confirmed in the frozen Ticket #001 audit).
2. **Severity:** High (batch-killer for bulk queues).
3. **Affected files (old):** `DRRipper/MainWindow.xaml.cs` (sequential batch loop — removed).
4. **Correction (Ticket #005):** the sequential loop is replaced by `DownloadScheduler`:
   each job runs in an isolated `ActiveJobRuntime` with per-job try/catch and terminal
   mapping; a `DownloadFailedException` (e.g. 404) marks only its job `Failed` with the
   reason persisted, and the admission loop continues. Global scheduler exceptions are
   contained per job and diagnosed, never silently killing the queue loop.
5. **Regression tests:** T-SCHED-02 (good/404/good), T-STRESS-MIXED (40/20/1 ledger).
6. **Impact if unfixed:** bulk downloading unusable with real-world URL lists.

### F-07 — Unbounded connection pooling (HIGH) — ADDRESSED AT SCHEDULER LEVEL

1. **Identifier:** F-07.
2. **Status:** connection policy now lives in the scheduler (`ConnectionBudget`:
   global 16 + per-host 8 defaults, configurable, enforced per transfer attempt via
   `INetworkPermitGate`), replacing `int.MaxValue` as effective policy. The handler's
   `MaxConnectionsPerServer` field is unchanged (engine untouched); backpressure now
   comes from the gate. Socket-buffer retuning and adaptive concurrency stay in Phase 2.
3. **Evidence:** T-BUDGET-01…08 (caps never exceeded, permits always returned, no starvation).

### P-10 — Sleep-prevention races (POTENTIAL) — RESOLVED

1. **Status:** `SchedulerPowerManager` reference-counts active jobs; system-required
   state held while ≥1 transfer active, released at zero. Unit-tested (refcount + underflow-safe).

### F-16 — Concurrent same-filename sessions share one `.part` (HIGH, found by T-SCHED-02/09)

1. **Identifier:** F-16 (new in Ticket #005 testing).
2. **Severity:** High (cross-job corruption: second job deleted/truncated the first's
   in-progress part; `ResolveUniquePath` no-ops when the final doesn't exist yet).
3. **Affected file:** `DRRipper/DownloadSession.cs` (`Prepare`/`CreateFreshFile`).
4. **Correction:** process-local live-part claim registry + `ResolveClaimFreePath`
   (always advances) + `CreateNew` atomic creation; resume still reuses validated
   parts across restarts (claims are process-local, released on Dispose/Cancel/Finalize).
5. **Regression tests:** T-SCHED-02, T-SCHED-09 (duplicate URLs → independent files).
6. **Impact if unfixed:** any two concurrent same-name jobs corrupt each other.

### Disposition table (Ticket #005)

| Finding | Status | Evidence |
|---|---|---|
| F-05 batch abort | **Resolved** | T-SCHED-02, T-STRESS-MIXED |
| F-07 unbounded connections | **Addressed** (policy at scheduler gate) | T-BUDGET-01…08 |
| P-10 power races | **Resolved** | T_POWER_RefCounted |
| F-16 part sharing | **Resolved** | T-SCHED-02/09 |
| F-01/F-02/F-03/F-04/F-06/F-10/F-12/F-13/F-14/F-15 | Stay resolved; engine untouched in trust model | Full suite green |

*End of AUDIT.md — see ROADMAP.md, ARCHITECTURE.md, TEST_PLAN.md.*
