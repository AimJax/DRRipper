# DRRipper browser extension (Ticket #007)

Plain Manifest V3 JavaScript/HTML — no build step, no Node required (§34).
Shared codebase; only the manifest differs per browser family.

## Layout

- `manifest-chromium.json` — Chrome/Edge (service worker background)
- `manifest-firefox.json` — Firefox (page background scripts)
- `background.js` — context menu + optional interception
- `native.js` — native-messaging client, eligibility + error mapping
- `options.html` / `options.js` — 3 settings (menu, interception, min size)
- `icons/dripper.svg` — source artwork
- `icons/dripper-16.png`, `dripper-48.png`, `dripper-128.png` — committed
  rasterized icons (rendered from the SVG at exactly 16/48/128 px)

## Load unpacked (Chromium)

1. Copy this folder to a stable path (the manifest must stay put).
2. Copy the manifest for your browser:
   - Chrome/Edge: copy `manifest-chromium.json` → `manifest.json`
   - Firefox: copy `manifest-firefox.json` → `manifest.json`
   - Do NOT commit `manifest.json` (git-ignored): it is a local copy.
3. Chrome: `chrome://extensions` → Developer mode → Load unpacked → this folder.
   Note the 32-character extension ID shown on the card.
4. Edge: `edge://extensions` → same steps.

## Load temporary (Firefox)

1. Copy `manifest-firefox.json` → `manifest.json`.
2. `about:debugging#/runtime/this-firefox` → Load Temporary Add-on →
   select `manifest-firefox.json`.
3. Note: temporary IDs change per session; for stable native messaging use
   a signed/packaged build (see BROWSER_INTEGRATION.md §release).

## Register the native host

In DRRipper: Settings → Browser integration → paste the extension ID →
Install (Chrome/Edge/Firefox). Or from a prompt next to
`DRRipper.NativeHost.exe`:

```
DRRipper.NativeHost.exe --register --browser chrome --extension-id <32-char-id>
DRRipper.NativeHost.exe --status --browser chrome
DRRipper.NativeHost.exe --unregister --browser chrome
```

`--browser` accepts `chrome`, `edge`, `firefox`.

## Behavior contract (do not violate)

- The browser download is cancelled ONLY after a positive DRRipper
  acknowledgement (`accepted: true`). Timeouts, missing hosts, and errors
  always keep the native browser download.
- Only `http://`/`https://` URLs are eligible; everything else is rejected
  with a notification, never sent.
- Cookies are never harvested in this ticket (deferred to #012).

## Permissions rationale (§6)

`contextMenus` (menu item), `downloads` (observe/cancel-after-ack),
`nativeMessaging` (talk to the host), `storage` (3 settings),
`notifications` (user-visible handoff success/failure feedback —
best-effort only, never gates download behavior). No `cookies`,
no `tabs`, no host permissions — the extension never reads page content.
