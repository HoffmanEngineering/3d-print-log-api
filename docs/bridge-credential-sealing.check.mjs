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

async function aesKey(shared, ephRaw, recipientRaw) {
  const ikm = await subtle.importKey('raw', shared, 'HKDF', false, ['deriveKey']);
  const salt = new Uint8Array([...ephRaw, ...recipientRaw]);
  return subtle.deriveKey({ name: 'HKDF', hash: 'SHA-256', salt, info: INFO }, ikm, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
}

// The browser side: seal to the bridge's SPKI public key.
async function seal(spkiB64, ad, plaintext, ephJwk, nonce) {
  const recipient = await subtle.importKey('spki', b64(spkiB64), { name: 'ECDH', namedCurve: 'P-256' }, true, []);
  const recipientRaw = new Uint8Array(await subtle.exportKey('raw', recipient));
  const eph = ephJwk
    ? { privateKey: await subtle.importKey('jwk', ephJwk, { name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']),
        publicKey: await subtle.importKey('jwk', { ...ephJwk, d: undefined, key_ops: [] }, { name: 'ECDH', namedCurve: 'P-256' }, true, []) }
    : await subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, true, ['deriveBits']);
  const ephRaw = new Uint8Array(await subtle.exportKey('raw', eph.publicKey));
  const shared = await subtle.deriveBits({ name: 'ECDH', public: recipient }, eph.privateKey, 256);
  const key = await aesKey(shared, ephRaw, recipientRaw);
  nonce ??= globalThis.crypto.getRandomValues(new Uint8Array(12));
  const ct = new Uint8Array(await subtle.encrypt({ name: 'AES-GCM', iv: nonce, additionalData: enc.encode(ad), tagLength: 128 }, key, enc.encode(plaintext)));
  return 'v1.' + b64urlEncode(new Uint8Array([...ephRaw, ...nonce, ...ct]));
}

// The bridge side, done in WebCrypto too, to check the vectors open.
async function open(pkcs8B64, recipientRawHex, sealed, ad) {
  const priv = await subtle.importKey('pkcs8', b64(pkcs8B64), { name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);
  const blob = b64url(sealed.slice(3));
  const ephRaw = blob.slice(0, 65);
  const nonce = blob.slice(65, 77);
  const eph = await subtle.importKey('raw', ephRaw, { name: 'ECDH', namedCurve: 'P-256' }, false, []);
  const shared = await subtle.deriveBits({ name: 'ECDH', public: eph }, priv, 256);
  const key = await aesKey(shared, ephRaw, hex(recipientRawHex));
  return new TextDecoder('utf-8', { fatal: true }).decode(await subtle.decrypt({ name: 'AES-GCM', iv: nonce, additionalData: enc.encode(ad) }, key, blob.slice(77)));
}

const r = file.recipient;
const fp = toHex(await subtle.digest('SHA-256', b64(r.publicKey)));
if (fp !== r.fingerprint) throw new Error('fingerprint ' + fp);
for (const v of file.vectors) {
  const ad = `printlog-credential:v1:${v.bridgeId}:${v.entryId}:${v.field}`;
  if (ad !== v.associatedData) throw new Error('ad');
  const opened = await open(r.privateKeyPkcs8Base64, r.publicKeyRawHex, v.sealed, ad);
  if (opened !== v.plaintext) throw new Error('open ' + opened);
  const pub = hex(v.ephemeralPublicKeyHex);
  const jwkB64 = (b) => b64urlEncode(b);
  const ephJwk = { kty: 'EC', crv: 'P-256', d: jwkB64(hex(v.ephemeralPrivateKeyHex)), x: jwkB64(pub.slice(1, 33)), y: jwkB64(pub.slice(33)), ext: true };
  const again = await seal(r.publicKey, ad, v.plaintext, ephJwk, hex(v.nonceHex));
  if (again !== v.sealed) throw new Error('reseal differs');
  const fresh = await seal(r.publicKey, ad, v.plaintext);
  if (await open(r.privateKeyPkcs8Base64, r.publicKeyRawHex, fresh, ad) !== v.plaintext) throw new Error('fresh');
  let refused = false;
  try { await open(r.privateKeyPkcs8Base64, r.publicKeyRawHex, v.sealed, ad.replace(`:${v.entryId}:`, `:${v.entryId + 1}:`)); } catch { refused = true; }
  if (!refused) throw new Error('wrong AD opened');
}
for (const n of file.mustNotOpen ?? []) {
  let refused = false;
  try { await open(r.privateKeyPkcs8Base64, r.publicKeyRawHex, n.sealed, n.associatedData); } catch { refused = true; }
  if (!refused) throw new Error('mustNotOpen opened: ' + n.why);
}
console.log(`WebCrypto: ${file.vectors.length} vectors open and reseal byte for byte; ${(file.mustNotOpen ?? []).length} must-not-open refused`);
