"""Benachrichtigungen aufs Handy: WipeKit Hub (Ende-zu-Ende-verschlüsselt), ntfy, Discord.

Format und Verschlüsselung: docs/hub-protocol.md. Läuft auch im Arbeitsprozess (root), daher ohne GTK.
"""

import base64
import json
import os
import urllib.error
import urllib.parse
import urllib.request
import uuid
from datetime import datetime, timezone

from common import VERSION

DEFAULT_PUSH = {
    "service": "off",            # off | hub | ntfy | discord
    "onDone": True, "onFailed": True, "onCancelled": True, "onWarning": True,
    "hubCode": "", "hubFiles": True,
    "ntfyServer": "https://ntfy.sh", "ntfyTopic": "", "ntfyAuth": "none", "ntfyUser": "", "ntfySecret": "",
    "discordWebhook": "",
}


def valid_discord(s: str) -> bool:
    try:
        u = urllib.parse.urlparse((s or "").strip())
    except ValueError:
        return False
    h = (u.hostname or "").lower()
    return (u.scheme == "https" and (h in ("discord.com", "discordapp.com") or h.endswith(".discord.com"))
            and u.path.startswith("/api/webhooks/"))


def parse_hub_code(code: str):
    """'WKHUB1:…' -> {'url', 'token', 'key'} oder None"""
    t = (code or "").strip()
    if not t.startswith("WKHUB1:"):
        return None
    try:
        b = t[7:].replace("-", "+").replace("_", "/")
        b += "=" * (-len(b) % 4)
        j = json.loads(base64.b64decode(b))
        url, token = j.get("url", ""), j.get("token", "")
        key = base64.b64decode(j.get("key", ""))
        if not url.startswith(("https://", "http://")) or not token or len(key) != 65 or key[0] != 4:
            return None
        return {"url": url.rstrip("/"), "token": token, "key": key}
    except (ValueError, TypeError):
        return None


def is_configured(c: dict) -> bool:
    s = c.get("service", "off")
    if s == "hub":
        return parse_hub_code(c.get("hubCode", "")) is not None
    if s == "ntfy":
        return c.get("ntfyServer", "").startswith(("https://", "http://")) and bool(c.get("ntfyTopic", "").strip())
    if s == "discord":
        return valid_discord(c.get("discordWebhook", ""))
    return False


def seal(plain: bytes, pub: bytes) -> bytes:
    """„WKE1“: ECDH P-256 + HKDF-SHA256 + AES-256-GCM (Paket python3-cryptography)"""
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import ec
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
    from cryptography.hazmat.primitives.kdf.hkdf import HKDF

    recipient = ec.EllipticCurvePublicKey.from_encoded_point(ec.SECP256R1(), pub)
    eph = ec.generate_private_key(ec.SECP256R1())
    eph_pub = eph.public_key().public_bytes(serialization.Encoding.X962, serialization.PublicFormat.UncompressedPoint)
    secret = eph.exchange(ec.ECDH(), recipient)
    key = HKDF(algorithm=hashes.SHA256(), length=32, salt=eph_pub + pub, info=b"WipeKit Hub v1").derive(secret)
    nonce = os.urandom(12)
    return b"WKE1" + eph_pub + nonce + AESGCM(key).encrypt(nonce, plain, b"WKE1")


def _request(req: urllib.request.Request):
    req.add_header("User-Agent", f"WipeKit/{VERSION}")
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            if 200 <= r.status < 300:
                return None
            return f"Server antwortet mit {r.status}."
    except urllib.error.HTTPError as e:
        body = ""
        try:
            body = e.read(200).decode("utf-8", "replace")
        except OSError:
            pass
        return f"Server antwortet mit {e.code}. {body}".strip()
    except (urllib.error.URLError, OSError, ValueError) as e:
        return str(getattr(e, "reason", e))


def send(c: dict, event: dict, force=False):
    """event = {kind, state, warning, notice, meta: dict, files: [(name, bytes)]}. None = Erfolg."""
    c = {**DEFAULT_PUSH, **(c or {})}
    if not force:
        st, warn = event.get("state"), event.get("warning")
        wanted = ((st == "done" and c["onDone"]) or (st == "failed" and c["onFailed"])
                  or (st == "cancelled" and c["onCancelled"]) or (warn and c["onWarning"]))
        if not wanted:
            return None
    try:
        if c["service"] == "hub":
            return _send_hub(c, event)
        if c["service"] == "ntfy":
            return _send_ntfy(c, event)
        if c["service"] == "discord":
            return _send_discord(c, event)
        return "Keine Benachrichtigung eingerichtet."
    except ImportError:
        return "Für den WipeKit Hub fehlt das Paket python3-cryptography."
    except Exception as e:  # nie den Ablauf stören
        return str(e)


def _send_hub(c, e):
    hub = parse_hub_code(c["hubCode"])
    if hub is None:
        return "Der Kopplungscode für den Hub ist ungültig."
    files = e.get("files", []) if c["hubFiles"] else []
    meta = dict(e.get("meta", {}))
    meta["files"] = [n for n, _ in files]
    body = {
        "v": 1, "id": str(uuid.uuid4()), "created": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "kind": e.get("kind", "wipe"), "state": e.get("state", "done"), "warning": bool(e.get("warning")),
        "notice": e.get("notice", ""),
        "meta": base64.b64encode(seal(json.dumps(meta).encode(), hub["key"])).decode(),
        "files": [base64.b64encode(seal(data, hub["key"])).decode() for _, data in files],
    }
    req = urllib.request.Request(hub["url"] + "/api/v1/events", data=json.dumps(body).encode(), method="POST",
                                 headers={"Authorization": f"Bearer {hub['token']}", "Content-Type": "application/json"})
    return _request(req)


def _send_ntfy(c, e):
    server, topic = c["ntfyServer"].strip().rstrip("/"), c["ntfyTopic"].strip()
    if not server or not topic:
        return "Server oder Thema für ntfy fehlt."
    st, warn = e.get("state"), e.get("warning")
    headers = {"Title": "WipeKit",
               "Tags": "white_check_mark" if st == "done" and not warn else ("warning" if st == "cancelled" else "x")}
    if st == "failed" or warn:
        headers["Priority"] = "high"
    if c["ntfyAuth"] == "token":
        headers["Authorization"] = f"Bearer {c['ntfySecret']}"
    elif c["ntfyAuth"] == "password":
        headers["Authorization"] = "Basic " + base64.b64encode(f"{c['ntfyUser']}:{c['ntfySecret']}".encode()).decode()
    req = urllib.request.Request(f"{server}/{urllib.parse.quote(topic)}", data=e.get("notice", "").encode(),
                                 method="POST", headers=headers)
    return _request(req)


def _send_discord(c, e):
    if not valid_discord(c["discordWebhook"]):
        return "Die Webhook-Adresse für Discord ist ungültig."
    st, warn = e.get("state"), e.get("warning")
    color = 0x1F8F3D if st == "done" and not warn else (0xDB8C00 if st == "cancelled" or warn else 0xD93026)
    body = {"username": "WipeKit", "allowed_mentions": {"parse": []},
            "embeds": [{"title": "WipeKit", "description": e.get("notice", ""), "color": color}]}
    req = urllib.request.Request(c["discordWebhook"].strip(), data=json.dumps(body).encode(), method="POST",
                                 headers={"Content-Type": "application/json"})
    return _request(req)
