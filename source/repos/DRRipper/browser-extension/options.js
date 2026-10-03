/* DRRipper options page logic (Ticket #007 §25): three controls only. */
async function load() {
  const defaults = { intercept: false, showMenu: true, minBytes: 0 };
  let stored = {};
  try { stored = await chrome.storage.sync.get(defaults); } catch (e) { /* unset */ }
  const settings = Object.assign({}, defaults, stored);
  document.getElementById('showMenu').checked = settings.showMenu !== false;
  document.getElementById('intercept').checked = settings.intercept === true;
  document.getElementById('minBytes').value = settings.minBytes || 0;
}

async function save() {
  const minBytes = Math.max(0, parseInt(document.getElementById('minBytes').value || '0', 10) || 0);
  const settings = {
    showMenu: document.getElementById('showMenu').checked,
    intercept: document.getElementById('intercept').checked,
    minBytes,
  };
  try {
    await chrome.storage.sync.set(settings);
    document.getElementById('status').textContent = 'Saved.';
  } catch (e) {
    document.getElementById('status').textContent = 'Save failed: ' + e;
  }
}

document.addEventListener('DOMContentLoaded', () => {
  load();
  document.getElementById('save').addEventListener('click', save);
});
