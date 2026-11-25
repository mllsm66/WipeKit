"""Einstellungen der Linux-App: ~/.config/wipekit/settings.json (nur für den Benutzer lesbar).

Zugangsdaten für die Benachrichtigungen liegen – wenn vorhanden – im Schlüsselbund der Sitzung
(Secret Service, z. B. GNOME-Schlüsselbund oder KWallet); sonst in derselben, geschützten Datei.
"""

import json
import os
from pathlib import Path

from push import DEFAULT_PUSH

SECRET_KEYS = ("hubCode", "ntfySecret", "discordWebhook")

DEFAULTS = {
    "notifyLocal": True,
    "playSound": True,
    "defaultMode": "secure",
    "reportsPath": "",
    "detailedReport": True,
    "serialInFileName": True,
    "operatorName": "",
    "organization": "",
    "reportNote": "",
    "autoCheck": False,
    "autoInstall": False,
    "updatesAsked": False,
}


def _path() -> Path:
    base = os.environ.get("XDG_CONFIG_HOME") or str(Path.home() / ".config")
    return Path(base) / "wipekit" / "settings.json"


class _Keyring:
    """Secret Service über libsecret (gir1.2-secret-1), falls installiert"""

    def __init__(self):
        self.ok = False
        try:
            import gi
            gi.require_version("Secret", "1")
            from gi.repository import Secret
            self.Secret = Secret
            self.schema = Secret.Schema.new("io.github.mllsm66.WipeKit", Secret.SchemaFlags.NONE,
                                            {"key": Secret.SchemaAttributeType.STRING})
            Secret.password_lookup_sync(self.schema, {"key": "probe"}, None)
            self.ok = True
        except Exception:
            self.ok = False

    def get(self, key: str) -> str:
        try:
            return self.Secret.password_lookup_sync(self.schema, {"key": key}, None) or ""
        except Exception:
            return ""

    def set(self, key: str, value: str):
        try:
            if value:
                self.Secret.password_store_sync(self.schema, {"key": key}, self.Secret.COLLECTION_DEFAULT,
                                                f"WipeKit: {key}", value, None)
            else:
                self.Secret.password_clear_sync(self.schema, {"key": key}, None)
        except Exception:
            pass


class Settings:
    def __init__(self):
        self.data = dict(DEFAULTS)
        self.push = dict(DEFAULT_PUSH)
        self.keyring = _Keyring()
        self.load()

    def load(self):
        try:
            raw = json.loads(_path().read_text())
        except (OSError, ValueError):
            raw = {}
        for k in DEFAULTS:
            if k in raw:
                self.data[k] = raw[k]
        self.push.update({k: v for k, v in (raw.get("push") or {}).items() if k in DEFAULT_PUSH})
        if self.keyring.ok:
            for k in SECRET_KEYS:
                self.push[k] = self.keyring.get(k) or self.push.get(k, "")

    def save(self):
        p = _path()
        p.parent.mkdir(parents=True, exist_ok=True)
        push_saved = dict(self.push)
        if self.keyring.ok:
            for k in SECRET_KEYS:
                self.keyring.set(k, self.push.get(k, ""))
                push_saved[k] = ""
        tmp = p.with_suffix(".tmp")
        fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "w") as f:
            json.dump({**self.data, "push": push_saved}, f, indent=2, ensure_ascii=False)
        os.replace(tmp, p)

    def reset(self):
        self.data = dict(DEFAULTS, updatesAsked=True)
        self.push = dict(DEFAULT_PUSH)
        self.save()

    def __getitem__(self, k):
        return self.data[k]

    def __setitem__(self, k, v):
        self.data[k] = v
        self.save()

    @property
    def reports_folder(self):
        return Path(self.data["reportsPath"]) if self.data["reportsPath"] else None

    @property
    def meta(self) -> list:
        rows = []
        for key, label in (("operatorName", "Durchgeführt von"), ("organization", "Firma / Organisation"),
                           ("reportNote", "Notiz")):
            v = (self.data.get(key) or "").strip()
            if v:
                rows.append((label, v))
        return rows

    def job_settings(self, host: str, platform: str, version: str) -> dict:
        """Für den Arbeitsprozess (wird nach dem Vorgang gelöscht)"""
        return {"detailed": self.data["detailedReport"], "serialInFileName": self.data["serialInFileName"],
                "meta": [list(m) for m in self.meta], "notifyLocal": self.data["notifyLocal"],
                "playSound": self.data["playSound"], "push": dict(self.push), "host": host, "platform": platform,
                "appVersion": version, "uid": os.getuid()}
