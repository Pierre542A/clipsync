import { authToken, encKey, accountId, encryptText, encryptBytes, decryptText, decryptBytes } from '/crypto.js';

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
let ws = null, connected = false, key = null, token = null, acct = null;

const wsURL = (location.protocol === 'https:' ? 'wss' : 'ws') + '://' + location.host + '/ws';
const httpBase = location.origin;
const configured = () => !!cfg.phrase;

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
  token = await authToken(cfg.phrase);
  key = await encKey(cfg.phrase);
  acct = await accountId(cfg.phrase);
}

async function connect() {
  if (!configured()) { setStatus('À configurer', false); openSettings(); return; }
  await ensureKeys();
  try { ws?.close(); } catch {}
  ws = new WebSocket(wsURL);
  ws.onopen = () => {
    ws.send(JSON.stringify({
      type: 'hello', accountId: acct, token,
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
    headers: { 'x-account-id': acct, 'x-token': token },
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

// Upload des octets (déjà chiffrés) via XHR pour avoir la progression -> renvoie fileId.
function uploadBlob(bytes, onProgress) {
  return new Promise((resolve) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', httpBase + '/files');
    xhr.setRequestHeader('content-type', 'application/octet-stream');
    xhr.setRequestHeader('x-account-id', acct);
    xhr.setRequestHeader('x-token', token);
    xhr.setRequestHeader('x-file-type', 'application/octet-stream');
    if (onProgress) xhr.upload.onprogress = (e) => { if (e.lengthComputable) onProgress(e.loaded / e.total); };
    xhr.onload = () => { try { resolve((JSON.parse(xhr.responseText) || {}).fileId || null); } catch { resolve(null); } };
    xhr.onerror = () => resolve(null);
    xhr.send(bytes);
  });
}

// Envoi d'une image (Blob/File) : chiffrement -> upload -> clip image.
async function doSendImage(blob) {
  if (!blob) { toast('Aucune image'); return; }
  if (!token || !acct) await ensureKeys();
  toast("Envoi de l'image…");
  const raw = new Uint8Array(await blob.arrayBuffer());
  const enc = await encryptBytes(key, raw);
  const fileId = await uploadBlob(enc);
  if (!fileId) { toast("Échec de l'envoi de l'image"); return; }
  let res;
  try {
    res = await fetch(httpBase + '/clip', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-account-id': acct, 'x-token': token },
      body: JSON.stringify({
        contentType: 'image', fileId, fileType: blob.type || 'image/png', enc: 'v1',
        targets: 'all', deviceName: cfg.deviceName, deviceId: cfg.deviceId,
      }),
    });
  } catch { toast('Serveur injoignable'); return; }
  if (res.status === 401) { toast('Phrase incorrecte'); return; }
  const j = await res.json().catch(() => ({}));
  toast(j.delivered > 0 ? `Image envoyée à ${j.delivered} PC ✓` : 'Aucun PC en ligne');
}

async function doSend(text) {
  if (!text) { toast('Rien à envoyer'); return; }
  if (!token || !acct) await ensureKeys();
  const ct = await encryptText(key, text);
  let res;
  try {
    res = await fetch(httpBase + '/clip', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-account-id': acct, 'x-token': token },
      body: JSON.stringify({
        contentType: 'text', text: ct, enc: 'v1', targets: 'all',
        deviceName: cfg.deviceName, deviceId: cfg.deviceId,
      }),
    });
  } catch { toast('Serveur injoignable'); return; }
  if (res.status === 401) { toast('Phrase incorrecte'); return; }
  const j = await res.json().catch(() => ({}));
  toast(j.delivered > 0 ? `Envoyé à ${j.delivered} PC ✓` : 'Aucun PC en ligne (ouvre ClipSync sur le PC)');
}

$('#send').onclick = async () => {
  if (!configured()) { openSettings(); return; }
  // 1) Essai lecture complète (texte OU image) — quand iOS l'autorise.
  try {
    const items = await navigator.clipboard.read();
    for (const item of items) {
      const imgType = item.types.find((t) => t.startsWith('image/'));
      if (imgType) { $('#pasteFallback').hidden = true; await doSendImage(await item.getType(imgType)); return; }
    }
    for (const item of items) {
      if (item.types.includes('text/plain')) {
        const txt = await (await item.getType('text/plain')).text();
        if (txt) { $('#pasteFallback').hidden = true; await doSend(txt); return; }
      }
    }
  } catch { /* iOS bloque souvent clipboard.read() */ }

  // 2) Essai texte simple.
  let text = null;
  try { text = await navigator.clipboard.readText(); } catch {}
  if (text) { $('#pasteFallback').hidden = true; await doSend(text); return; }

  // 3) Rien d'automatique -> champ manuel (texte). Pour une image : bouton dédié.
  $('#pasteFallback').hidden = false;
  $('#pasteBox').innerHTML = '';
  $('#pasteBox').focus();
  toast('Appui long dans le champ → Coller (texte ou image)');
};

// Coller dans le champ : image -> envoi direct ; texte -> reste pour « Envoyer ».
$('#pasteBox').addEventListener('paste', async (e) => {
  const items = e.clipboardData ? [...(e.clipboardData.items || [])] : [];
  const imgItem = items.find((it) => it.type && it.type.startsWith('image/'));
  if (imgItem) {
    e.preventDefault();
    const blob = imgItem.getAsFile();
    $('#pasteBox').innerHTML = '';
    $('#pasteFallback').hidden = true;
    if (blob) await doSendImage(blob);
  }
  // sinon : le texte se colle normalement dans le champ
});

$('#pasteSend').onclick = async () => {
  const text = $('#pasteBox').innerText.trim();
  if (!text) { toast('Le champ est vide'); return; }
  await doSend(text);
  $('#pasteBox').innerHTML = '';
  $('#pasteFallback').hidden = true;
};

// Envoi d'image fiable : sélecteur de photo (Photos / Fichiers).
$('#sendImage').onclick = () => { if (!configured()) { openSettings(); return; } $('#imgInput').click(); };
$('#imgInput').onchange = async (e) => {
  const file = e.target.files && e.target.files[0];
  if (file) await doSendImage(file);
  e.target.value = '';
};

// --- Envoi de fichiers (n'importe quel type, plusieurs, avec progression) --
const MAX_FILE = 100 * 1024 * 1024;

function showProgress(name, frac) {
  $('#progress').hidden = false;
  $('#progressLabel').textContent = name;
  $('#progressBar').style.width = Math.round(frac * 100) + '%';
}
function hideProgress() { $('#progress').hidden = true; $('#progressBar').style.width = '0%'; }

async function doSendFile(file) {
  if (!token || !acct) await ensureKeys();
  if (file.size > MAX_FILE) { toast(`Trop lourd : ${file.name} (max 100 Mo)`); return; }
  const raw = new Uint8Array(await file.arrayBuffer());
  const enc = await encryptBytes(key, raw);
  showProgress(file.name, 0);
  const fileId = await uploadBlob(enc, (p) => showProgress(file.name, p));
  hideProgress();
  if (!fileId) { toast('Échec : ' + file.name); return; }
  try {
    const res = await fetch(httpBase + '/clip', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-account-id': acct, 'x-token': token },
      body: JSON.stringify({
        contentType: 'file', fileId, fileType: file.type || 'application/octet-stream', enc: 'v1',
        meta: { filename: file.name, size: file.size }, targets: 'all',
        deviceName: cfg.deviceName, deviceId: cfg.deviceId,
      }),
    });
    const j = await res.json().catch(() => ({}));
    toast(j.delivered > 0 ? `Envoyé : ${file.name}` : 'Aucun PC en ligne');
  } catch { toast('Serveur injoignable'); }
}

$('#sendFiles').onclick = () => { if (!configured()) { openSettings(); return; } $('#fileInput').click(); };
$('#fileInput').onchange = async (e) => {
  const files = [...(e.target.files || [])];
  for (const f of files) await doSendFile(f);
  e.target.value = '';
};

// --- Réglages --------------------------------------------------------------

function openSettings() {
  $('#cfgPhrase').value = cfg.phrase || '';
  $('#cfgName').value = cfg.deviceName || '';
  $('#settings').showModal();
}
$('#settingsBtn').onclick = openSettings;
$('#cfgCancel').onclick = () => $('#settings').close();
$('#cfgSave').onclick = async () => {
  cfg.phrase = $('#cfgPhrase').value;
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
