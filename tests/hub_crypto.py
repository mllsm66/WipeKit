#!/usr/bin/env python3
"""Prüft die Hub-Verschlüsselung (WKE1) der Mac- und Windows-Fassung gegen eine unabhängige Umsetzung.

  hub_crypto.py keygen <ordner>          -> <ordner>/priv.pem, <ordner>/pub.b64
  hub_crypto.py open <ordner> <blob> <erwarteter Text>
"""
import base64
import sys
from pathlib import Path

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF


def main():
    cmd, folder = sys.argv[1], Path(sys.argv[2])
    if cmd == "keygen":
        folder.mkdir(parents=True, exist_ok=True)
        k = ec.generate_private_key(ec.SECP256R1())
        (folder / "priv.pem").write_bytes(k.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                                                          serialization.NoEncryption()))
        pub = k.public_key().public_bytes(serialization.Encoding.X962, serialization.PublicFormat.UncompressedPoint)
        (folder / "pub.b64").write_text(base64.b64encode(pub).decode())
        return 0
    blob = Path(sys.argv[3]).read_bytes()
    expected = sys.argv[4].encode()
    k = serialization.load_pem_private_key((folder / "priv.pem").read_bytes(), None)
    pub = k.public_key().public_bytes(serialization.Encoding.X962, serialization.PublicFormat.UncompressedPoint)
    assert blob[:4] == b"WKE1", "kein WKE1"
    eph, nonce, ct = blob[4:69], blob[69:81], blob[81:]
    secret = k.exchange(ec.ECDH(), ec.EllipticCurvePublicKey.from_encoded_point(ec.SECP256R1(), eph))
    key = HKDF(algorithm=hashes.SHA256(), length=32, salt=eph + pub, info=b"WipeKit Hub v1").derive(secret)
    plain = AESGCM(key).decrypt(nonce, ct, b"WKE1")
    assert plain == expected, f"anderer Inhalt: {plain!r}"
    print("WKE1 ok:", plain.decode())
    return 0


if __name__ == "__main__":
    sys.exit(main())
