# DRRipper — Browser Integration (Ticket #007)

Ordinary HTTP/HTTPS downloads flow from Chromium/Firefox into the normal
persistent scheduler. The transfer engine is untouched except for
per-request context application (referrer + allowlisted headers); no
scheduling, budgeting, recovery, or UI-transport semantics changed.

## 1. Architecture

```
Browser Extension (MV3, plain JS)
      │  Native Messaging (stdio: 4-byte LE length + UTF-8 JSON)
      ▼
DRRipper.NativeHost.exe (framework-dependent, win-x64)
  validate → forward ──► Named pipe \\.\pipe\DRRipper.NativeBridge
      │                         (4-byte LE length + UTF-8 JSON, same schema)
      │                  ┌───────► launch DRRipper.exe --background (bounded 45 s)
      ▼                  ▼
DRRipper desktop process (single instance: mutex + live pipe)
  BrowserBridgeService ──► DownloadScheduler.EnqueueBrowserAsync ──► normal queue
```

- Extension source: `browser-extension/` (shared JS; per-browser manifests).
- Shared contract + validation: `DRRipper.BrowserProtocol` (referenced by the
  desktop app, the native host, and the test project).
- Desktop bridge: `DRRipper/Scheduler/BrowserBridgeService.cs` (+
  `BrowserRequestContext.cs`, `SingleInstanceGuard.cs`).
- UI: Settings → Browser integration (`BrowserIntegrationViewModel`,
  install/remove/test per browser); queue Details pane shows
  `Source: Chrome · example.com` (never headers/cookies).

## 2. Message contract (versioned from day one)

Request `{version:1, type:"enqueue", requestId, url, suggestedFileName,
referrer, source, headers:{…}, cookies:[…]}`. Response `{version, requestId,
accepted, jobId, errorCode, message, duplicate, warnings}`. `requestId` is
always echoed back verbatim so the extension can correlate retries.

Error categories (§26): `invalid-url`, `disallowed-scheme`,
`invalid-filename`, `invalid-headers`, `invalid-cookies`,
`invalid-referrer`, `protocol-version-mismatch`, `malformed-message`,
`message-too-large`, `desktop-unavailable`, `internal-error`.

## 3. Trust boundaries

1. **Extension → native host.** Every field validated: http/https URLs only
   (`file:`, `javascript:`, `data:`, `ftp:`, custom schemes rejected);
   filename length-capped then sanitized; headers filtered to the allowlist;
   cookies shape-checked; referrer must be http/https; 1 MB message cap;
   zero/truncated/malformed frames rejected with categories.
2. **Native host → desktop pipe.** The bridge re-validates everything (the
   pipe is local-only but still untrusted). The host never touches
   `queue.db` and never executes anything from extension data.
3. **Desktop → engine.** Only `BrowserRequestContext` (referrer + User-Agent
   + Accept/Accept-Language/Authorization) reaches `HttpRequestMessage`
   via `ApplyTo`, which writes a closed set of headers even for hand-built
   maps. Cookies are accepted structurally and reported as
   `cookies-deferred-to-012` — never applied (deferred to #012, no fake
   support).

## 4. Header policy (§16)

Forwarded: `Referer`, `User-Agent`, `Accept`, `Accept-Language`,
`Authorization` (extra care: transient only, redacted everywhere).
Never forwarded (dropped, reported): `Host`, `Content-Length`,
`Connection`, `Transfer-Encoding`, `TE`, `Trailer`, `Upgrade`,
`Proxy-*`, `Keep-Alive`, `Range`/`If-Range` (engine owns ranges),
`Accept-Encoding` (engine forces `identity`), `Cookie`/`Set-Cookie`,
`Content-Range`, and any unknown name. CR/LF/NUL values rejected
(response-splitting guard).

## 5. Privacy (§19)

- Exact download URL persists in the queue DB (required for resume); all
  diagnostics use the redacted form (`…[redacted]` query/userinfo strip).
- `Authorization` values and cookie values never reach logs, exceptions,
  UI, or the DB. `DescribeForDiagnostics` emits header NAMES only
  (`Authorization:[redacted]`).
- Only the referrer HOST persists (`ReferrerHost`); the full source-page URL
  is transient. Requested headers/cookies are never persisted.
- Resume after restart re-synthesizes `Referer` from the persisted host;
  full header context is intentionally transient.

## 6. Idempotency and failure safety (§27/§45)

- Same `requestId` within 10 minutes (in-memory window + persisted
  `BrowserRequestId` lookup) replays the original acceptance with
  `duplicate:true` — no second job. Distinct ids for one URL create
  independent jobs (intentional repeats work).
- **The browser cancels its native download ONLY after `accepted:true`.**
  Timeouts, missing hosts, and errors always keep the browser download.
  The extension maps every failure to a concise notification.

## 7. Single instance and background launch (§12/§29/§30/§44)

- First desktop process holds `Local\DRRipper.SingleInstance` (+ in-process
  latch) and serves the pipe. Later GUI launches ping the bridge and exit.
- Native host: pipe → (failure) → launch
  `DRRipper.exe --background --browser-bridge` (hidden, tray-only, no
  window flash) → bounded 45 s pipe retry. Five simultaneous handoffs still
  land on one desktop process.
- Recognized flags: `--background`, `--browser-bridge` (all spellings
  `--`/`-`/`/`). Everything else is ignored — no URL/file execution.

## 8. Queue behavior (§31–33)

Browser jobs are ordinary scheduler jobs: JobId, persistence, recovery,
budgets, failure isolation, tray behavior. The browser suggests a filename
only; the target directory is always DRRipper's configured folder.
Suggestions are sanitized (`..`/separators/ADS/reserved names/trailing
dots/spaces, 255 cap, `download.bin` fallback) and the engine sanitizes
again at path build (defense in depth) — nothing escapes the target dir.

## 9. Installation

- DRRipper Settings → Browser integration: paste the extension ID (from
  `chrome://extensions` developer card) → Install per browser (Chrome,
  Edge, Firefox). Repair = re-install; Remove deletes only DRRipper-owned
  keys + manifests. Test connection pings the live bridge.
- CLI (next to `DRRipper.NativeHost.exe`):
  `--register/--unregister/--status --browser <chrome|edge|firefox>
  --extension-id <id>`.
- Manifests live in `%LOCALAPPDATA%\DRRipper\NativeHosts\` with the
  INSTALLED host path (never a dev path); HKCU keys only (no admin).
- Chromium manifests constrain `allowed_origins` to the packaged extension
  origin (no wildcards); Firefox uses `allowed_extensions`.
- Extension loading: see `browser-extension/README.md` (unpacked layout is
  load-ready as committed: 16/48/128 PNG icons rendered from the SVG,
  temporary Firefox IDs).

## 10. Extension behavior (§7/§8/§25)

- Context menu “Download with DRRipper” on links/images/video/audio:
  captures URL + page/referrer + filename hint → ack → notification.
- Automatic interception (default OFF): on download creation, eligible
  http/https jobs above the size floor are offered; the browser copy is
  cancelled + erased ONLY on positive ack.
- Options: show-menu toggle, interception toggle, minimum bytes. Permissions
  are minimal (`contextMenus`, `downloads`, `nativeMessaging`, `storage`,
  `notifications` — the last solely for user-visible handoff success/failure
  feedback, best-effort only and never gating download behavior;
  no `cookies`, no `tabs`, no host permissions — no page-content access).

## 11. Manual validation procedure (§40–42)

Chromium (repeat per browser): load unpacked → register host →
right-click direct file link → Download with DRRipper → job appears →
completes with known hash → tray-hide and repeat → full exit and repeat
(background launch) → unregister and confirm the browser download is kept
with a clear error. Interception matrix: small/large file, 404, redirect,
Content-Disposition filename, signed URL, missing host. Firefox: same
context-menu + host smoke; if not robust, mark deferred (see §13).

Validated 2026-10-04 (Ticket #007.1): the committed tree loads unpacked in
Microsoft Edge with zero manifest asset errors — the `background.js`
service worker starts (proves manifest parses, worker + `native.js`
resolve, icons load). No click/interception pass was performed (no test
page harness); covered instead by the package-integrity suite
(`ExtensionPackageTests`, 14 tests) plus the #007 host/bridge live smoke.

## 12. Diagnostics (§47)

Settings → Browser integration → Test connection reports: native host
registered (per browser + manifest path), desktop bridge reachable,
extension missing, version mismatch, registration path missing. Bridge
counters (`AcceptedCount`/`RejectedCount`) are in-memory diagnostics.

## 13. Current status and limitations

- Implemented and tested: Chromium MV3 extension, stdio host, pipe bridge,
  registration for Chrome/Edge/Firefox formats, header/referrer context,
  idempotency, background launch, settings UX, redaction.
- Firefox: manifest + registration formats implemented; live Firefox smoke
  is deferred (no Firefox in this environment) — marked validation-deferred,
  not pretended-complete.
- Cookies: contract carries them; transfer deferred to #012 with an
  explicit `cookies-deferred-to-012` warning per handoff.
- Pipe ACL: local-only pipe + same-user launch + dual validation; explicit
  per-user PipeSecurity ACLs are not applied (SDK surface in this tree) —
  hardening noted for release engineering.
- Release packaging (store listing, stable extension IDs, signed host,
  MSIX colocating `DRRipper.NativeHost.exe`) is Phase 6 work; source is
  structured for it (deterministic `browser-extension/` package dir).

*End of BROWSER_INTEGRATION.md.*
