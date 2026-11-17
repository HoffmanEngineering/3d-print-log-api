// Reference sealer and opener for printlog-credential v1 (docs/bridge-credential-sealing.md), WebCrypto only.
// Usage: node bridge-credential-sealing.check.mjs bridge-credential-sealing.vectors.json (Node 20+; the same code runs in a browser).
import { readFileSync } from 'node:fs';

const subtle = globalThis.crypto.subtle;
const file = JSON.parse(readFileSync(process.argv[2], 'utf8'));
const hex = (s) => Uint8Array.from(s.match(/../g).map((h) => parseInt(h, 16)));
const toHex = (b) => [...new Uint8Array(b)].map((x) => x.toString(16).padStart(2, '0')).join('');
const b64 = (s) => Uint8Array.from(atob(s), (c) => c.charCodeAt(0));
const b64url = (s) => b64(s.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - (s.length % 4)) % 4));
const b64urlEncode = (b) => btoa(String.fromCharCode(...b)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
const enc = new TextEncoder();
const INFO = enc.encode('printlog-credential:v1:aes-256-gcm');
// C0 controls, DEL and C1 controls: refused in any credential.
const CONTROL = /[\u0000-\u001f\u007f-\u009f]/;
// Trimmed before sealing (U+0020 and U+00A0 at either end); a password never is.
const TRIMMED_FIELDS = new Set(['access_code', 'username', 'api_key', 'moonraker_api_key']);

class InvalidPlaintext extends Error {}

/** The plaintext rules, on bytes: 1 to 256 bytes of strict UTF-8 with no control characters. */
function checkPlaintext(bytes) {
  if (bytes.length < 1 || bytes.length > 256) throw new InvalidPlaintext('length');
  let text;
  try {
    text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes);
  } catch {
    throw new InvalidPlaintext('utf-8');
  }
  if (CONTROL.test(text)) throw new InvalidPlaintext('control character');
  return text;
}

/** What the browser seals for what the user typed: trimmed except for `password`, never normalized. */
function prepare(field, typed) {
  const text = TRIMMED_FIELDS.has(field) ? typed.replace(/^[  ]+|[  ]+$/g, '') : typed;
  checkPlaintext(enc.encode(text));
  return text;
}

async function aesKey(shared, ephRaw, recipientRaw) {
  const ikm = await subtle.importKey('raw', shared, 'HKDF', false, ['deriveKey']);
  const salt = new Uint8Array([...ephRaw, ...recipientRaw]);
  return subtle.deriveKey({ name: 'HKDF', hash: 'SHA-256', salt, info: INFO }, ikm, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
}

/**
 * The browser side: seal to the bridge's SPKI public key. Returns the sealed value and the
 * fingerprint of the exact SPKI bytes it sealed to, computed here: that is what the browser shows
 * and sends, never the API's publicKeyFingerprint.
 */
async function seal(spkiB64, ad, plaintext, ephJwk, nonce) {
  const spki = b64(spkiB64);
  const fingerprint = toHex(await subtle.digest('SHA-256', spki));
  const recipient = await subtle.importKey('spki', spki, { name: 'ECDH', namedCurve: 'P-256' }, true, []);
  const recipientRaw = new Uint8Array(await subtle.exportKey('raw', recipient));
  const eph = ephJwk
    ? { privateKey: await subtle.importKey('jwk', ephJwk, { name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']),
        publicKey: await subtle.importKey('jwk', { ...ephJwk, d: undefined, key_ops: [] }, { name: 'ECDH', namedCurve: 'P-256' }, true, []) }
    : await subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, true, ['deriveBits']);
  const ephRaw = new Uint8Array(await subtle.exportKey('raw', eph.publicKey));
  const shared = await subtle.deriveBits({ name: 'ECDH', public: recipient }, eph.privateKey, 256);
  const key = await aesKey(shared, ephRaw, recipientRaw);
  const pt = enc.encode(plaintext);
  checkPlaintext(pt);
  nonce ??= globalThis.crypto.getRandomValues(new Uint8Array(12));
  const ct = new Uint8Array(await subtle.encrypt({ name: 'AES-GCM', iv: nonce, additionalData: enc.encode(ad), tagLength: 128 }, key, pt));
  return { sealed: 'v1.' + b64urlEncode(new Uint8Array([...ephRaw, ...nonce, ...ct])), fingerprint };
}

/** The bridge side, done in WebCrypto too. Throws a DOMException (OperationError) when it doesn't open, InvalidPlaintext when it opens to something that isn't a credential. */
async function open(pkcs8B64, recipientRawHex, sealed, ad) {
  const priv = await subtle.importKey('pkcs8', b64(pkcs8B64), { name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);
  const blob = b64url(sealed.slice(3));
  const ephRaw = blob.slice(0, 65);
  const nonce = blob.slice(65, 77);
  const eph = await subtle.importKey('raw', ephRaw, { name: 'ECDH', namedCurve: 'P-256' }, false, []);
  const shared = await subtle.deriveBits({ name: 'ECDH', public: eph }, priv, 256);
  const key = await aesKey(shared, ephRaw, hex(recipientRawHex));
  const pt = new Uint8Array(await subtle.decrypt({ name: 'AES-GCM', iv: nonce, additionalData: enc.encode(ad) }, key, blob.slice(77)));
  return checkPlaintext(pt); // used exactly as opened: no trimming, no normalization
}

const r = file.recipient;
for (const v of file.vectors) {
  const ad = `printlog-credential:v1:${v.bridgeId}:${v.entryId}:${v.field}`;
  if (ad !== v.associatedData) throw new Error('ad');
  if (toHex(enc.encode(v.plaintext)) !== v.plaintextUtf8Hex) throw new Error('plaintext bytes');
  if ('typed' in v && prepare(v.field, v.typed) !== v.plaintext) throw new Error('prepare: ' + v.note);
  const opened = await open(r.privateKeyPkcs8Base64, r.publicKeyRawHex, v.sealed, ad);
  if (opened !== v.plaintext) throw new Error('open ' + opened);
  const pub = hex(v.ephemeralPublicKeyHex);
  const ephJwk = { kty: 'EC', crv: 'P-256', d: b64urlEncode(hex(v.ephemeralPrivateKeyHex)), x: b64urlEncode(pub.slice(1, 33)), y: b64urlEncode(pub.slice(33)), ext: true };
  const again = await seal(r.publicKey, ad, v.plaintext, ephJwk, hex(v.nonceHex));
  if (again.sealed !== v.sealed) throw new Error('reseal differs');
  if (again.fingerprint !== r.fingerprint) throw new Error('fingerprint ' + again.fingerprint);
  const fresh = await seal(r.publicKey, ad, v.plaintext);
  if (await open(r.privateKeyPkcs8Base64, r.publicKeyRawHex, fresh.sealed, ad) !== v.plaintext) throw new Error('fresh');
}
for (const n of file.mustNotOpen) {
  try {
    await open(r.privateKeyPkcs8Base64, r.publicKeyRawHex, n.sealed, n.associatedData);
  } catch (e) {
    const plaintext = e instanceof InvalidPlaintext;
    if (plaintext === (n.reason === 'plaintext')) continue;
    throw new Error('failed the wrong way: ' + n.why);
  }
  throw new Error('mustNotOpen opened: ' + n.why);
}
console.log(`WebCrypto: ${file.vectors.length} vectors open and reseal byte for byte; ${file.mustNotOpen.length} must-not-open refused`);
