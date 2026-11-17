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
SEALED_SHAPE = re.compile(r"v1\.[A-Za-z0-9_-]+")
# C0 controls, DEL and C1 controls: refused in any credential.
CONTROL = re.compile("[\u0000-\u001f\u007f-\u009f]")
# Trimmed before sealing (U+0020 and U+00A0 at either end); a password never is.
TRIMMED_FIELDS = {"access_code", "username", "api_key", "moonraker_api_key"}


class InvalidPlaintext(ValueError):
    """It opened, but is not a credential: invalid UTF-8, a control character, or out of 1 to 256 bytes.
    The bridge reports it as `invalid_plaintext`."""


def b64url_decode(s):
    return base64.urlsafe_b64decode(s + "=" * (-len(s) % 4))


def b64url_encode(b):
    return base64.urlsafe_b64encode(b).rstrip(b"=").decode("ascii")


def aes_key(shared, eph_raw, recipient_raw):
    return HKDF(algorithm=hashes.SHA256(), length=32, salt=eph_raw + recipient_raw, info=INFO).derive(shared)


def check_plaintext(data):
    """The plaintext rules, on bytes: 1 to 256 bytes of strict UTF-8 with no control characters."""
    if not 1 <= len(data) <= 256:
        raise InvalidPlaintext("length")
    try:
        text = data.decode("utf-8")  # strict: refuses invalid bytes and encoded surrogates
    except UnicodeDecodeError as e:
        raise InvalidPlaintext("utf-8") from e
    if CONTROL.search(text):
        raise InvalidPlaintext("control character")
    return text


def prepare(field, typed):
    """What the sender seals for what the user typed: trimmed for every field but `password`,
    never normalized (no NFC), then checked."""
    text = typed.strip("  ") if field in TRIMMED_FIELDS else typed
    check_plaintext(text.encode("utf-8"))
    return text


def open_sealed(private_key, sealed, ad):
    """Opens a sealed value, or raises: ValueError for the shape, InvalidTag when it doesn't open
    (`undecryptable`), InvalidPlaintext when it opens to something that isn't a credential."""
    if not SEALED_SHAPE.fullmatch(sealed):
        raise ValueError("not a v1 sealed value")
    blob = b64url_decode(sealed[3:])
    if not 94 <= len(blob) <= 349 or blob[0] != 0x04 or b64url_encode(blob) != sealed[3:]:
        raise ValueError("not a v1 sealed value")
    eph_raw, nonce, ct = blob[:65], blob[65:77], blob[77:]
    eph = ec.EllipticCurvePublicKey.from_encoded_point(ec.SECP256R1(), eph_raw)
    shared = private_key.exchange(ec.ECDH(), eph)
    recipient_raw = private_key.public_key().public_bytes(*RAW)
    plaintext = AESGCM(aes_key(shared, eph_raw, recipient_raw)).decrypt(nonce, ct, ad.encode("ascii"))
    # Used exactly as opened: no trimming, no normalization.
    return check_plaintext(plaintext)


def seal(public_key, ad, plaintext, eph=None, nonce=None):
    check_plaintext(plaintext.encode("utf-8"))
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
# The sealer computes the fingerprint itself, over the exact SPKI bytes it seals to.
assert hashlib.sha256(spki).hexdigest() == r["fingerprint"]
for v in data["vectors"]:
    ad = "printlog-credential:v1:%d:%d:%s" % (v["bridgeId"], v["entryId"], v["field"])
    assert ad == v["associatedData"]
    assert v["plaintext"].encode("utf-8").hex() == v["plaintextUtf8Hex"]
    if "typed" in v:
        assert prepare(v["field"], v["typed"]) == v["plaintext"], v["note"]
    assert open_sealed(priv, v["sealed"], ad) == v["plaintext"]
    eph = ec.derive_private_key(int(v["ephemeralPrivateKeyHex"], 16), ec.SECP256R1())
    assert seal(pub, ad, v["plaintext"], eph, bytes.fromhex(v["nonceHex"])) == v["sealed"]
    assert open_sealed(priv, seal(pub, ad, v["plaintext"]), ad) == v["plaintext"]
for n in data["mustNotOpen"]:
    expected = InvalidPlaintext if n["reason"] == "plaintext" else InvalidTag
    try:
        open_sealed(priv, n["sealed"], n["associatedData"])
    except expected:
        continue
    raise AssertionError("opened, or failed the wrong way: " + n["why"])
for bad in data["invalidSealedValues"]:
    try:
        open_sealed(priv, bad["value"], "printlog-credential:v1:42:7:access_code")
    except InvalidPlaintext:
        raise AssertionError("got past the shape check: " + bad["why"])
    except ValueError:
        continue
    raise AssertionError("accepted the shape of: " + bad["why"])
print("cryptography %s: %d vectors open and reseal byte for byte; %d must-not-open and %d bad shapes refused"
      % (cryptography.__version__, len(data["vectors"]), len(data["mustNotOpen"]), len(data["invalidSealedValues"])))
