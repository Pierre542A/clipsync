import { authToken, encKey, encryptText, decryptText, decryptBytes } from '/crypto.js';

const $ = (s) => document.querySelector(s);
const CFG = 'clipsync.cfg';

function loadCfg() {
  let c = {};
  try { c = JSON.parse(localStorage.getItem(CFG)) || {}; } catch {}
  if (!c.deviceId) c.deviceId = 'iphone-' + Math.random().toString(36).slice(2, 10);
  if (!c.deviceName) c.deviceName = 'iPhone';
  saveCfg(c);
  return c;
}
function saveCfg(c) { localStorage.setItem(CFG, JSON.stringify(c)); }

let cfg = loadCfg();
let ws = null, connected = false, key = null, token = null;

const wsURL = (location.protocol === 'https:' ? 'wss' : 'ws') + '://' + location.host + '/ws';
const httpBase = location.origin;
const configured = () => !!(cfg.accountId && cfg.secret);

function setStatus(text, online, sub = '') {
  $('#dot').className = 'dot ' + (online ? 'on' : 'off');
  $('#statusText').textContent = text;
  $('#statusSub').textContent = sub;
}

let toastTimer;
function toast(msg) {
  const t = $('#toast');
  t.textContent = msg;
  t.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.remove('show'), 2600);
}

// --- Connexion WebSocket ---------------------------------------------------

async function ensureKeys() {
  token = await authToken(cfg.secret);
  key = await encKey(cfg.secret);
}

async function connect() {
  if (!configured()) { setStatus('À configurer', false); openSettings(); return; }
  await ensureKeys();
  try { ws?.close(); } catch {}
  ws = new WebSocket(wsURL);
  ws.onopen = () => {
    ws.send(JSON.stringify({
      type: 'hello', accountId: cfg.accountId, token,
      deviceId: cfg.deviceId, deviceName: cfg.deviceName, platform: 'ios',
    }));
    connected = true;
    setStatus('Connecté', true);
  };
  ws.onmessage = (e) => { try { handle(JSON.parse(e.data)); } catch {} };
  ws.onclose = () => { connected = false; setStatus('Hors ligne', false); setTimeout(connect, 3000); };
  ws.onerror = () => {};
}

setInterval(() => { if (ws && ws.readyState === 1) ws.send(JSON.stringify({ type: 'heartbeat' })); }, 30000);

async function handle(msg) {
  switch (msg.type) {
    case 'welcome':
    case 'devices': renderDevices(msg.devices || []); break;
    case 'clip': await onClip(msg); break;
    case 'sent': break;
    case 'applied': toast(`Collé sur ${msg.by?.name || 'PC'} ✓`); break;
    case 'error': toast('Erreur : ' + msg.message); break;
  }
}

// --- Réception (déchiffrement) ---------------------------------------------

async function onClip(msg) {
  const from = msg.from?.name || 'un appareil';
  if (msg.contentType === 'text') {
    const text = msg.enc === 'v1' ? await decryptText(key, msg.text) : msg.text;
    if (text != null) addReceived(from, 'text', text);
  } else if (msg.contentType === 'image' && msg.fileId) {
    const blob = await fetchFile(msg.fileId);
    if (!blob) return;
    const bytes = msg.enc === 'v1' ? await decryptBytes(key, blob) : blob;
    if (bytes) addReceived(from, 'image', bytes);
  }
}

async function fetchFile(fileId) {
  const res = await fetch(httpBase + '/files/' + fileId, {
    headers: { 'x-account-id': cfg.accountId, 'x-token': token },
  });
  if (!res.ok) return null;
  return new Uint8Array(await res.arrayBuffer());
}

let firstReceived = true;
function addReceived(from, type, payload) {
  if (firstReceived) { $('#received').innerHTML = ''; firstReceived = false; }
  const div = document.createElement('div');
  div.className = 'item';
  const meta = document.createElement('div');
  meta.className = 'meta';
  meta.textContent = 'Reçu de ' + from;
  div.appendChild(meta);

  if (type === 'text') {
    const body = document.createElement('div');
    body.className = 'body';
    body.textContent = payload;
    div.appendChild(body);
    const btn = document.createElement('button');
    btn.className = 'copy';
    btn.textContent = 'Copier';
    btn.onclick = async () => {
      try { await navigator.clipboard.writeText(payload); toast('Copié ✓'); }
      catch { toast('Touche « Copier » à nouveau'); }
    };
    div.appendChild(btn);
  } else {
    const url = URL.createObjectURL(new Blob([payload], { type: 'image/png' }));
    const img = document.createElement('img');
    img.src = url;
    div.appendChild(img);
  }
  $('#received').prepend(div);
  toast('Nouveau contenu reçu');
}

function renderDevices(devices) {
  const el = $('#devices');
  if (!devices.length) { el.innerHTML = '<div class="empty">Aucun autre appareil</div>'; return; }
  el.innerHTML = '';
  for (const d of devices) {
    const row = document.createElement('div');
    row.className = 'device';
    row.innerHTML = `<span class="dot ${d.online ? 'on' : 'off'}"></span>
      <span class="name">${escapeHtml(d.name)}</span>
      <span class="plat">${escapeHtml(d.platform)}</span>`;
    el.appendChild(row);
  }
}
function escapeHtml(s) { return String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }

// --- Envoi (chiffrement) ---------------------------------------------------

$('#send').onclick = async () => {
  if (!configured()) { openSettings(); return; }
  if (!connected) { toast('Connexion en cours…'); }
  let text;
  try { text = await navigator.clipboard.readText(); }
  catch { toast("Autorise l'accès au presse-papiers (Coller)"); return; }
  if (!text) { toast('Presse-papiers vide'); return; }

  const ct = await encryptText(key, text);
  let res;
  try {
    res = await fetch(httpBase + '/clip', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-account-id': cfg.accountId, 'x-token': token },
      body: JSON.stringify({
        contentType: 'text', text: ct, enc: 'v1', targets: 'all',
        deviceName: cfg.deviceName, deviceId: cfg.deviceId,
      }),
    });
  } catch { toast('Serveur injoignable'); return; }
  if (res.status === 401) { toast('Secret incorrect'); return; }
  const j = await res.json().catch(() => ({}));
  toast(j.delivered > 0 ? `Envoyé à ${j.delivered} PC ✓` : 'Aucun PC en ligne');
};

// --- Réglages --------------------------------------------------------------

function openSettings() {
  $('#cfgAccount').value = cfg.accountId || '';
  $('#cfgSecret').value = cfg.secret || '';
  $('#cfgName').value = cfg.deviceName || '';
  $('#settings').showModal();
}
$('#settingsBtn').onclick = openSettings;
$('#cfgCancel').onclick = () => $('#settings').close();
$('#cfgSave').onclick = async () => {
  cfg.accountId = $('#cfgAccount').value.trim();
  cfg.secret = $('#cfgSecret').value;
  cfg.deviceName = $('#cfgName').value.trim() || 'iPhone';
  saveCfg(cfg);
  $('#settings').close();
  await connect();
};

// --- Démarrage -------------------------------------------------------------

if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register('/sw.js').catch(() => {});
}
connect();
