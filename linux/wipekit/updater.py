"""Updates aus den GitHub-Releases: .deb und Signatur (.sig) laden, Signatur mit dem öffentlichen
Schlüssel prüfen (ECDSA P-256, SHA-256), dann mit Administratorrechten installieren (pkexec apt-get)."""

import json
import os
import re
import shutil
import subprocess
import tempfile
import urllib.error
import urllib.request
from pathlib import Path

from common import VERSION

REPO = "mllsm66/WipeKit"
KEY_PATHS = [Path("/usr/share/wipekit/update-public-key.pem"),
             Path(__file__).resolve().parent / "update-public-key.pem"]


def public_key_pem():
    for p in KEY_PATHS:
        try:
            return p.read_bytes()
        except OSError:
            continue
    return None


def compare(a: str, b: str) -> int:
    pa = [int(x) for x in re.findall(r"\d+", a)]
    pb = [int(x) for x in re.findall(r"\d+", b)]
    for i in range(max(len(pa), len(pb))):
        x, y = (pa[i] if i < len(pa) else 0), (pb[i] if i < len(pb) else 0)
        if x != y:
            return -1 if x < y else 1
    return 0


def installed_as_package() -> bool:
    return Path(__file__).resolve().as_posix().startswith("/usr/") and shutil.which("apt-get") is not None


def can_install(r) -> bool:
    return bool(r and r.get("deb") and r.get("sig") and public_key_pem() and installed_as_package()
                and shutil.which("pkexec"))


def _get(url: str, timeout=30) -> bytes:
    req = urllib.request.Request(url, headers={"User-Agent": f"WipeKit/{VERSION}",
                                               "Accept": "application/vnd.github+json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read()


def check():
    """(ReleaseInfo-dict oder None, Statustext)"""
    try:
        j = json.loads(_get(f"https://api.github.com/repos/{REPO}/releases/latest"))
    except urllib.error.HTTPError as e:
        return None, ("Noch keine Version veröffentlicht." if e.code == 404 else f"GitHub antwortet mit {e.code}.")
    except Exception as e:
        return None, f"Keine Verbindung: {getattr(e, 'reason', e)}"
    v = (j.get("tag_name") or "").lstrip("v")
    if not v or compare(v, VERSION) <= 0:
        return None, "WipeKit ist aktuell."
    assets = {a.get("name"): a.get("browser_download_url") for a in j.get("assets") or []}
    deb = next((n for n in assets if n.endswith(".deb") and v in n), None)
    r = {"version": v, "page": j.get("html_url") or f"https://github.com/{REPO}/releases/latest",
         "deb": assets.get(deb) if deb else None, "sig": assets.get(deb + ".sig") if deb else None, "name": deb}
    text = f"Version {v} ist verfügbar."
    if not installed_as_package():
        text += " (Nicht als Paket installiert: bitte von GitHub laden.)"
    elif not can_install(r):
        text += " Automatisch installieren geht nicht (keine Signatur) – bitte von GitHub laden."
    return r, text


def install(r) -> str:
    """Lädt, prüft und installiert. Gibt None bei Erfolg zurück, sonst die Fehlermeldung."""
    if not can_install(r):
        return "Dieses Update lässt sich nicht automatisch installieren."
    try:
        from cryptography.exceptions import InvalidSignature
        from cryptography.hazmat.primitives import hashes, serialization
        from cryptography.hazmat.primitives.asymmetric import ec
    except ImportError:
        return "Zum Prüfen der Signatur fehlt das Paket python3-cryptography."
    try:
        deb = _get(r["deb"], timeout=300)
        sig = _get(r["sig"])
    except Exception as e:
        return f"Download fehlgeschlagen: {getattr(e, 'reason', e)}"
    try:
        key = serialization.load_pem_public_key(public_key_pem())
        key.verify(sig, deb, ec.ECDSA(hashes.SHA256()))
    except (InvalidSignature, ValueError):
        return "Die Signatur des Updates ist ungültig – es wurde nichts installiert."
    tmp = Path(tempfile.mkdtemp(prefix="wipekit-update-"))
    os.chmod(tmp, 0o755)
    path = tmp / (r.get("name") or "wipekit.deb")
    path.write_bytes(deb)
    os.chmod(path, 0o644)
    res = subprocess.run(["pkexec", "apt-get", "install", "-y", "--allow-downgrades", str(path)],
                         capture_output=True, text=True)
    shutil.rmtree(tmp, ignore_errors=True)
    if res.returncode == 126:
        return "Abgebrochen."
    if res.returncode != 0:
        return "Installation fehlgeschlagen: " + (res.stderr or res.stdout).strip()[-300:]
    return None
