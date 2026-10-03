/* DRRipper native-messaging client (Ticket #007 §9/§26).
 * One port per handoff: post the framed JSON request, await the single
 * acknowledgement, then disconnect. Timeouts keep a wedged host from hanging
 * the browser download decision (§45: cancel ONLY after positive ack). */

const DRIPPER_HOST_NAME = 'com.dripper.host';
const DRIPPER_PROTOCOL_VERSION = 1;
const DRIPPER_ACK_TIMEOUT_MS = 30000;
// Upper bound for one handoff round-trip. Warm handoffs answer in
// milliseconds; a cold desktop launch (tray start + queue recovery) can take
// tens of seconds. Past this the browser download is KEPT (never lost),
// which may rarely duplicate a very slow cold-start handoff (§45).

function dripperErrorMessage(code, fallback) {
  const map = {
    'accepted': 'Sent to DRRipper.',
    'invalid-url': 'DRRipper rejected the URL.',
    'disallowed-scheme': 'Only http/https downloads can go to DRRipper.',
    'invalid-filename': 'DRRipper rejected the filename.',
    'invalid-headers': 'DRRipper rejected the request headers.',
    'invalid-cookies': 'DRRipper rejected the cookies.',
    'invalid-referrer': 'DRRipper rejected the referrer.',
    'protocol-version-mismatch': 'DRRipper host version mismatch — update DRRipper.',
    'malformed-message': 'DRRipper could not understand the request.',
    'message-too-large': 'Download request too large for DRRipper.',
    'desktop-unavailable': 'DRRipper desktop app is not reachable.',
    'internal-error': 'DRRipper reported an internal error.',
    'native-host-unavailable': 'DRRipper native host is not installed.',
    'timeout': 'DRRipper did not answer in time; browser download kept.',
  };
  if (code && map[code]) return map[code];
  return fallback || 'DRRipper handoff failed; browser download kept.';
}

/* Sends one enqueue request; resolves {accepted, response} — never rejects
 * the browser download decision into an exception the caller must catch:
 * transport failures resolve accepted:false with a mapped code. */
function dripperSend(request, timeoutMs) {
  const timeout = typeof timeoutMs === 'number' ? timeoutMs : DRIPPER_ACK_TIMEOUT_MS;
  return new Promise((resolve) => {
    let port = null;
    let settled = false;
    const finish = (result) => {
      if (settled) return;
      settled = true;
      try { clearTimeout(timer); } catch (e) { /* ignore */ }
      try { if (port) port.disconnect(); } catch (e) { /* ignore */ }
      resolve(result);
    };
    const timer = setTimeout(() => {
      finish({ accepted: false, errorCode: 'timeout', response: null });
    }, timeout);
    try {
      port = chrome.runtime.connectNative(DRIPPER_HOST_NAME);
    } catch (e) {
      finish({ accepted: false, errorCode: 'native-host-unavailable', response: null });
      return;
    }
    port.onMessage.addListener((response) => {
      const accepted = !!(response && response.accepted === true);
      finish({
        accepted,
        errorCode: accepted ? 'accepted' : ((response && response.errorCode) || 'internal-error'),
        response: response || null,
      });
    });
    port.onDisconnect.addListener(() => {
      finish({ accepted: false, errorCode: 'native-host-unavailable', response: null });
    });
    try {
      port.postMessage(request);
    } catch (e) {
      finish({ accepted: false, errorCode: 'native-host-unavailable', response: null });
    }
  });
}

/* Builds the versioned enqueue request (§14). requestId is a fresh uuid per
 * user action so intentional repeats create independent jobs; interception
 * retries reuse the download id for idempotency (§27). */
function dripperBuildRequest({ url, filename, referrer, source, headers, requestId }) {
  return {
    version: DRIPPER_PROTOCOL_VERSION,
    type: 'enqueue',
    requestId: requestId || (crypto.randomUUID ? crypto.randomUUID() : String(Date.now())),
    url: url || '',
    suggestedFileName: filename || null,
    referrer: referrer || null,
    source: source || null,
    headers: headers || null,
    cookies: null, // cookie transfer deferred to #012 (§17): never harvested
  };
}

/* Eligibility shared by context menu + interception (§4): ordinary
 * http/https file downloads only. Exported for options-page reuse. */
function dripperIsEligibleUrl(url) {
  if (typeof url !== 'string') return false;
  const u = url.trim().toLowerCase();
  return u.startsWith('http://') || u.startsWith('https://');
}

/* Interception decision (§8): eligible scheme + enabled + size floor. */
function dripperShouldIntercept(url, totalBytes, settings) {
  if (!settings || settings.intercept !== true) return false;
  if (!dripperIsEligibleUrl(url)) return false;
  const min = typeof settings.minBytes === 'number' ? settings.minBytes : 0;
  if (typeof totalBytes === 'number' && totalBytes >= 0 && totalBytes < min) return false;
  return true;
}

// Exported for unit-style checks (options page, manual console tests).
if (typeof self !== 'undefined') {
  self.dripperIsEligibleUrl = dripperIsEligibleUrl;
  self.dripperShouldIntercept = dripperShouldIntercept;
  self.dripperBuildRequest = dripperBuildRequest;
  self.dripperErrorMessage = dripperErrorMessage;
}
