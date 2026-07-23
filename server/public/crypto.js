// Chiffrement de bout en bout côté navigateur (Web Crypto).
// Miroir exact des clients Windows (.NET) et iOS (CryptoKit) :
//   authToken = hex(HKDF-SHA256(secret, salt="clipsync-v1", info="clipsync-auth-v1", 32))
//   encKey    = HKDF-SHA256(secret, salt="clipsync-v1", info="clipsync-enc-v1", 32)   [AES-256-GCM]
// Blob = nonce(12) ‖ ciphertext ‖ tag(16).

const enc = new TextEncoder();
const SALT = enc.encode('clipsync-v1');

async function hkdf(secret, info, length = 32) {
  const km = await crypto.subtle.importKey('raw', enc.encode(secret), 'HKDF', false, ['deriveBits']);
  const bits = await crypto.subtle.deriveBits(
    { name: 'HKDF', hash: 'SHA-256', salt: SALT, info: enc.encode(info) }, km, length * 8);
  return new Uint8Array(bits);
}

export async function authToken(secret) {
  const b = await hkdf(secret, 'clipsync-auth-v1', 32);
  return [...b].map((x) => x.toString(16).padStart(2, '0')).join('');
}

export async function encKey(secret) {
  const raw = await hkdf(secret, 'clipsync-enc-v1', 32);
  return crypto.subtle.importKey('raw', raw, { name: 'AES-GCM' }, false, ['encrypt', 'decrypt']);
}

// Identifiant de compte dérivé de la phrase (unique par phrase → identique sur tous
// tes appareils, jamais en collision avec un autre utilisateur).
export async function accountId(phrase) {
  const h = await crypto.subtle.digest('SHA-256', enc.encode('clipsync-acct-v1:' + phrase));
  const hex = [...new Uint8Array(h)].map((x) => x.toString(16).padStart(2, '0')).join('');
  return 'u-' + hex.slice(0, 16);
}

export async function encryptBytes(key, data) {
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const ct = new Uint8Array(await crypto.subtle.encrypt({ name: 'AES-GCM', iv }, key, data)); // ct‖tag
  const blob = new Uint8Array(12 + ct.length);
  blob.set(iv, 0);
  blob.set(ct, 12);
  return blob;
}

export async function decryptBytes(key, blob) {
  const iv = blob.slice(0, 12);
  const ct = blob.slice(12);
  try {
    const pt = await crypto.subtle.decrypt({ name: 'AES-GCM', iv }, key, ct);
    return new Uint8Array(pt);
  } catch {
    return null;
  }
}

const b64enc = (u8) => btoa(String.fromCharCode(...u8));
const b64dec = (s) => Uint8Array.from(atob(s), (c) => c.charCodeAt(0));

export async function encryptText(key, text) {
  return b64enc(await encryptBytes(key, enc.encode(text)));
}

export async function decryptText(key, b64) {
  const pt = await decryptBytes(key, b64dec(b64));
  return pt ? new TextDecoder().decode(pt) : null;
}

export { b64enc, b64dec };
