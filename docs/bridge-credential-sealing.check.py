"""Reference sealer and opener for printlog-credential v1 (docs/bridge-credential-sealing.md), Python `cryptography`.

Usage: python bridge-credential-sealing.check.py bridge-credential-sealing.vectors.json
"""
import base64
import hashlib
import json
import os
import re
import sys

import cryptography

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

INFO = b"printlog-credential:v1:aes-256-gcm"
RAW = serialization.Encoding.X962, serialization.PublicFormat.UncompressedPoint


def b64url_decode(s):
    return base64.urlsafe_b64decode(s + "=" * (-len(s) % 4))


def b64url_encode(b):
    return base64.urlsafe_b64encode(b).rstrip(b"=").decode("ascii")


def aes_key(shared, eph_raw, recipient_raw):
    return HKDF(algorithm=hashes.SHA256(), length=32, salt=eph_raw + recipient_raw, info=INFO).derive(shared)


SEALED_SHAPE = re.compile(r"v1\.[A-Za-z0-9_-]+")


def open_sealed(private_key, sealed, ad):
    """Opens a sealed value or raises (ValueError for the shape, InvalidTag for everything else)."""
    if not SEALED_SHAPE.fullmatch(sealed):
        raise ValueError("not a v1 sealed value")
    blob = b64url_decode(sealed[3:])
    if not 94 <= len(blob) <= 349 or blob[0] != 0x04 or b64url_encode(blob) != sealed[3:]:
        raise ValueError("not a v1 sealed value")
    eph_raw, nonce, ct = blob[:65], blob[65:77], blob[77:]
    eph = ec.EllipticCurvePublicKey.from_encoded_point(ec.SECP256R1(), eph_raw)
    shared = private_key.exchange(ec.ECDH(), eph)
    recipient_raw = private_key.public_key().public_bytes(*RAW)
    return AESGCM(aes_key(shared, eph_raw, recipient_raw)).decrypt(nonce, ct, ad.encode("ascii")).decode("utf-8")


def seal(public_key, ad, plaintext, eph=None, nonce=None):
    eph = eph or ec.generate_private_key(ec.SECP256R1())
    eph_raw = eph.public_key().public_bytes(*RAW)
    shared = eph.exchange(ec.ECDH(), public_key)
    key = aes_key(shared, eph_raw, public_key.public_bytes(*RAW))
    nonce = nonce or os.urandom(12)
    return "v1." + b64url_encode(eph_raw + nonce + AESGCM(key).encrypt(nonce, plaintext.encode("utf-8"), ad.encode("ascii")))


data = json.load(open(sys.argv[1], encoding="utf-8"))
r = data["recipient"]
priv = serialization.load_der_private_key(base64.b64decode(r["privateKeyPkcs8Base64"]), password=None)
assert priv.private_numbers().private_value == int(r["privateKeyHex"], 16)
spki = base64.b64decode(r["publicKey"], validate=True)
pub = serialization.load_der_public_key(spki)
assert hashlib.sha256(spki).hexdigest() == r["fingerprint"]
for v in data["vectors"]:
    ad = "printlog-credential:v1:%d:%d:%s" % (v["bridgeId"], v["entryId"], v["field"])
    assert ad == v["associatedData"]
    assert open_sealed(priv, v["sealed"], ad) == v["plaintext"]
    eph = ec.derive_private_key(int(v["ephemeralPrivateKeyHex"], 16), ec.SECP256R1())
    assert seal(pub, ad, v["plaintext"], eph, bytes.fromhex(v["nonceHex"])) == v["sealed"]
    assert open_sealed(priv, seal(pub, ad, v["plaintext"]), ad) == v["plaintext"]
for n in data.get("mustNotOpen", []):
    try:
        open_sealed(priv, n["sealed"], n["associatedData"])
    except (InvalidTag, ValueError):
        continue
    raise AssertionError("opened: " + n["why"])
for bad in data.get("invalidSealedValues", []):
    try:
        open_sealed(priv, bad["value"], "printlog-credential:v1:42:7:access_code")
    except ValueError:
        continue
    raise AssertionError("accepted the shape of: " + bad["why"])
print("cryptography %s: %d vectors open and reseal byte for byte; %d must-not-open refused"
      % (cryptography.__version__, len(data["vectors"]), len(data.get("mustNotOpen", []))))
