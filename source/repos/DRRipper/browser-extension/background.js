/* DRRipper background worker (Ticket #007 §7/§8, Manifest V3 service worker).
 * - "Download with DRRipper" on links/images.
 * - Optional automatic interception: browser download is cancelled ONLY after
 *   a positive desktop acknowledgement (§45). Any failure keeps the native
 *   browser download untouched. */

/* Shared background script: Chromium loads native.js via importScripts
 * (service worker); Firefox loads both as page scripts (see manifest). */
try { if (typeof importScripts === 'function') importScripts('native.js'); } catch (e) { /* firefox page scope */ }

const MENU_ID = 'dripper-download';
const DEFAULT_SETTINGS = { intercept: false, showMenu: true, minBytes: 0 };
const BROWSER_SOURCE = (() => {
  try {
    const ua = (navigator.userAgent || '').toLowerCase();
    if (ua.includes(' edg/')) return 'Edge';
    if (ua.includes('firefox')) return 'Firefox';
    return 'Chrome';
  } catch (e) { return 'Chrome'; }
})();

async function loadSettings() {
  try {
    const stored = await chrome.storage.sync.get(DEFAULT_SETTINGS);
    return Object.assign({}, DEFAULT_SETTINGS, stored);
  } catch (e) {
    return Object.assign({}, DEFAULT_SETTINGS);
  }
}

async function ensureMenu() {
  try {
    await chrome.contextMenus.removeAll();
    const settings = await loadSettings();
    if (settings.showMenu === false) return;
    chrome.contextMenus.create({
      id: MENU_ID,
      title: 'Download with DRRipper',
      contexts: ['link', 'image', 'video', 'audio'],
    });
  } catch (e) { /* menu is best-effort */ }
}

chrome.runtime.onInstalled.addListener(() => { ensureMenu(); });
chrome.runtime.onStartup.addListener(() => { ensureMenu(); });
chrome.storage.onChanged.addListener(() => { ensureMenu(); });

function filenameFromUrl(url) {
  try {
    const u = new URL(url);
    const last = u.pathname.split('/').pop() || '';
    return decodeURIComponent(last) || null;
  } catch (e) { return null; }
}

async function handoff({ url, filename, referrer, requestId }) {
  const settings = await loadSettings();
  const request = dripperBuildRequest({
    url,
    filename: filename || filenameFromUrl(url),
    referrer: referrer || null,
    source: BROWSER_SOURCE,
    headers: null, // authenticated-header forwarding is opt-in per handoff (§16)
    requestId,
  });
  return dripperSend(request);
}

function notify(text) {
  try {
    chrome.notifications.create({
      type: 'basic',
      iconUrl: 'icons/dripper-48.png',
      title: 'DRRipper',
      message: String(text).slice(0, 200),
    });
  } catch (e) { /* notifications are best-effort feedback */ }
}

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId !== MENU_ID) return;
  const url = info.linkUrl || info.srcUrl;
  if (!dripperIsEligibleUrl(url)) {
    notify(dripperErrorMessage('disallowed-scheme'));
    return;
  }
  const result = await handoff({
    url,
    filename: info.filename || null,
    referrer: info.pageUrl || (tab && tab.url) || null,
  });
  notify(dripperErrorMessage(result.errorCode, result.response && result.response.message));
});

const seenInterceptions = new Map(); // downloadId -> requestId (idempotent retries)

chrome.downloads.onCreated.addListener(async (item) => {
  try {
    const settings = await loadSettings();
    if (!dripperShouldIntercept(item.url, item.totalBytes, settings)) return;
    if (seenInterceptions.has(item.id)) return;
    const requestId = 'dl-' + item.id + '-' + (item.startTime || Date.now());
    seenInterceptions.set(item.id, requestId);
    const result = await handoff({
      url: item.finalUrl || item.url,
      filename: item.filename || null,
      referrer: item.referrer || null,
      requestId,
    });
    if (result.accepted) {
      // Positive desktop acknowledgement only — now safe to cancel ours (§45).
      try { await chrome.downloads.cancel(item.id); } catch (e) { /* already finished */ }
      try { await chrome.downloads.erase(item.id); } catch (e) { /* history policy */ }
      notify('Sent to DRRipper.');
    } else {
      // Anything else: the browser download proceeds normally. No data lost.
      seenInterceptions.delete(item.id);
      if (result.errorCode && result.errorCode !== 'desktop-unavailable' && result.errorCode !== 'native-host-unavailable') {
        notify(dripperErrorMessage(result.errorCode));
      }
    }
  } catch (e) { /* never break the browser download on extension errors */ }
});
