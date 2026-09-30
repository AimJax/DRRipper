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
