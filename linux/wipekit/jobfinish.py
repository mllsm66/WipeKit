"""Abschluss eines Vorgangs – läuft im Arbeitsprozess (root), damit Protokolle, Mitteilung und
Benachrichtigung aufs Handy auch dann kommen, wenn WipeKit geschlossen ist.

Die App schreibt beim Start <job-dir>/job-settings.json (nur für den Benutzer lesbar). Der Arbeitsprozess
erstellt die Protokolle in <job-dir>/reports, schreibt <job-dir>/finish.json und löscht die Einstellungen
danach wieder. Die App kopiert die Protokolle anschließend in den Berichte-Ordner.
"""

import json
import os
import subprocess
import time
from datetime import datetime, timezone
from pathlib import Path

import health as hl
import push
import reports as rp
from common import MODES_BY_KEY, Disk, fmt_bytes

SETTINGS_FILE = "job-settings.json"
FINISH_FILE = "finish.json"


def title_for(mode, purpose: str) -> str:
    if mode.key.startswith("selftest"):
        return mode.title
    if mode.read_only:
        return "Oberflächentest" if purpose == "scan" else "Prüfung"
    return "Löschen"


def verification_text(mode, s) -> str:
    if mode.verify == "full":
        v = s.verified if s else None
        if v is True:
            return ("bestanden – alle Sektoren gelesen und mit den geschriebenen Zufallsdaten verglichen"
                    if mode.passes and mode.passes[-1] == "seeded" else "bestanden – alle Sektoren gelesen, alle enthalten Nullen")
        return "NICHT bestanden" if v is False else "nicht durchgeführt"
    if mode.verify == "samples":
        return (s.sampleResult if s and s.sampleResult else "nicht durchgeführt")
    return "nicht vorgesehen"


def load_health(path: Path):
    try:
        text = path.read_text()
        return hl.parse_text(text) if text.strip() else None
    except (OSError, ValueError):
        return None


def build_reports(info: dict, s, state: str, message: str, before, after, settings: dict):
    """[(Titel, Report)] für einen Vorgang"""
    d = Disk(**info["disk"])
    mode = MODES_BY_KEY[info["mode"]]
    purpose = info.get("purpose", "")
    started = s.startedAt if s and s.startedAt else info["createdAt"]
    ended = s.finishedAt if s and s.finishedAt else time.time()
    w = rp.WipeInfo(
        mode_title=title_for(mode, purpose) if mode.read_only else mode.title, method=mode.method_description,
        standard=mode.standard, state=state, started=started, ended=ended,
        bytes_written=(s.bytesDoneTotal if s else 0) if not mode.purge else 0,
        message=message or "", verification=verification_text(mode, s),
        trim=((s.trimResult if s else None) or "nicht durchgeführt") if mode.trims else "",
        device=d.path, bus=d.bus + (" (intern)" if d.internal else ""), type_label=d.type_label,
        volumes=d.volumes, exact_size=(s.diskSize if s and s.diskSize else d.size),
        hidden_area=(s.hiddenArea if s and s.hiddenArea else ""), host=settings.get("host", ""),
        platform=settings.get("platform", ""), app_version=settings.get("appVersion", ""), read_only=mode.read_only,
        bad_blocks=(s.badBlocks if s else None) if mode.read_only else None,
        non_zero=(s.nonZeroBytes if s else None) if mode.read_only else None,
        purge_method=(s.purgeMethod if s and s.purgeMethod else ""),
        meta=[tuple(m) for m in settings.get("meta", []) if len(m) == 2])
    ident = rp.identity_from(before or after, d.model, d.size, d.serial)
    if mode.read_only:
        return [("Prüfprotokoll", rp.wipe_report(w, ident, before, after, detailed=True))]
    out = [("Löschprotokoll", rp.wipe_report(w, ident, before, after, detailed=False))]
    if settings.get("detailed", True):
        out.append(("Löschprotokoll (ausführlich)", rp.wipe_report(w, ident, before, after, detailed=True)))
    return out


def notice(info: dict, state: str, message: str, s, h):
    """(Titel, Text, Warnung) – kurze Mitteilung"""
    d = Disk(**info["disk"])
    mode = MODES_BY_KEY[info["mode"]]
    t = title_for(mode, info.get("purpose", ""))
    ident = rp.identity_from(h, d.model, d.size, d.serial)
    label = " ".join(x for x in (("" if ident.vendor == "Unbekannt" else ident.vendor), ident.model) if x)
    what = f"{label or d.model} ({fmt_bytes(d.size)})"
    warning = bool(h is not None and h.available and h.rating in (hl.BAD, hl.CAUTION))
    if state == "done":
        if mode.read_only:
            if s and s.badBlocks:
                body = f"{what}: {s.badBlocks} unlesbare Bereiche gefunden"
            elif info.get("purpose") == "scan":
                body = f"{what}: Oberflächentest bestanden"
            else:
                body = f"{what}: " + ("Laufwerk ist leer" if s and s.nonZeroBytes == 0 else "Laufwerk enthält Daten")
        else:
            body = f"{what}: erfolgreich gelöscht"
    elif state == "cancelled":
        body = f"{what}: {t} abgebrochen"
    else:
        body = f"{what}: {t} fehlgeschlagen" + (f" – {message[:120]}" if message else "")
    if warning:
        body += f" · Gesundheit: {h.rating}"
    head = ((f"{t} abgeschlossen" if mode.read_only else "Löschen abgeschlossen") if state == "done"
            else (f"{t} abgebrochen" if state == "cancelled" else f"{t} fehlgeschlagen"))
    return head, body, warning


def hub_meta(info: dict, state: str, s, h, settings: dict) -> dict:
    d = Disk(**info["disk"])
    mode = MODES_BY_KEY[info["mode"]]
    ident = rp.identity_from(h, d.model, d.size, d.serial)
    typ = "unknown"
    if h is not None and h.available:
        typ = "nvme" if h.protocol == "NVMe" else ("ssd" if h.is_ssd is True else ("hdd" if h.is_ssd is False else "unknown"))
    elif d.is_ssd is True:
        typ = "ssd"
    elif d.is_ssd is False:
        typ = "hdd"
    if typ == "unknown" and d.transport == "usb" and d.size < 600_000_000_000:
        typ = "usb"
    started = s.startedAt if s and s.startedAt else info["createdAt"]
    ended = s.finishedAt if s and s.finishedAt else time.time()
    iso = lambda t: datetime.fromtimestamp(t, timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")  # noqa: E731
    m = {"vendor": ident.vendor, "model": ident.model, "serial": ident.serial, "size": d.size, "type": typ,
         "mode": title_for(mode, info.get("purpose", "")) if mode.read_only else mode.title,
         "method": mode.method_description,
         "result": ("Abgeschlossen" if mode.read_only else "Erfolgreich gelöscht") if state == "done"
         else ("Abgebrochen" if state == "cancelled" else "Fehlgeschlagen"),
         "rating": h.rating if h is not None else "Unbekannt", "started": iso(started), "ended": iso(ended),
         "duration": int(max(0, ended - started)), "host": settings.get("host", ""),
         "platform": settings.get("platform", ""), "app": settings.get("appVersion", "")}
    if state != "done" and s and s.message:
        m["message"] = s.message
    return m


def app_running(job_dir: Path) -> bool:
    """Die App meldet sich alle paar Sekunden („app-alive“ im übergeordneten Ordner)."""
    try:
        return time.time() - (job_dir.parent / "app-alive").stat().st_mtime < 20
    except OSError:
        return False


def notify_as_user(uid: int, title: str, body: str):
    """Mitteilung in der Sitzung des Benutzers (aus dem root-Prozess, wenn WipeKit geschlossen ist)"""
    if uid <= 0:
        return
    env = {"DBUS_SESSION_BUS_ADDRESS": f"unix:path=/run/user/{uid}/bus", "PATH": "/usr/bin:/bin"}
    for cmd in (["notify-send", "--app-name=WipeKit", "--icon=io.github.mllsm66.WipeKit", title, body],
                ["gdbus", "call", "--session", "--dest", "org.freedesktop.Notifications",
                 "--object-path", "/org/freedesktop/Notifications", "--method", "org.freedesktop.Notifications.Notify",
                 "WipeKit", "0", "io.github.mllsm66.WipeKit", title, body, "[]", "{}", "10000"]):
        try:
            r = subprocess.run(["setpriv", f"--reuid={uid}", "--regid={}".format(_gid(uid)), "--init-groups", *cmd],
                               env=env, capture_output=True, timeout=15)
            if r.returncode == 0:
                return
        except (OSError, subprocess.TimeoutExpired):
            continue


def _gid(uid: int) -> int:
    try:
        import pwd
        return pwd.getpwuid(uid).pw_gid
    except (KeyError, ImportError):
        return uid


def finish(job_dir: Path, status, state: str, message: str, write_status):
    """Protokolle erstellen, Status schreiben, dann melden. Gibt das finish-Objekt zurück."""
    settings = None
    try:
        settings = json.loads((job_dir / SETTINGS_FILE).read_text())
        info = json.loads((job_dir / "job.json").read_text())
    except (OSError, ValueError):
        write_status()
        return None
    fin = {"files": [], "notifiedLocal": False, "push": None}
    before = load_health(job_dir / "smart-before.json")
    after = load_health(job_dir / "smart-after.json")
    h = after if (after is not None and after.available) else before
    pdfs = []
    uid = int(settings.get("uid", 0))
    out = job_dir / "reports"
    try:
        for title, r in build_reports(info, status, state, message, before, after, settings):
            pdf, txt = rp.save(r, out, serial=settings.get("serialInFileName", True))
            fin["files"].append({"title": title, "pdf": str(pdf), "txt": str(txt)})
            pdfs.append((pdf.name, pdf.read_bytes()))
        if uid > 0:
            for p in [out, *out.rglob("*")]:
                os.chown(p, uid, -1)
    except Exception as e:  # Protokolle dürfen den Ablauf nie stören
        fin["error"] = str(e)
    fin["notifiedLocal"] = bool(settings.get("notifyLocal", True)) and not app_running(job_dir)
    _write(job_dir, fin)
    write_status()

    head, body, warning = notice(info, state, message, status, h)
    if fin["notifiedLocal"]:
        notify_as_user(uid, head, body)
    cfg = settings.get("push") or {}
    if cfg.get("service", "off") != "off":
        mode = MODES_BY_KEY[info["mode"]]
        ev = {"kind": "check" if mode.read_only else "wipe", "state": state, "warning": warning, "notice": body,
              "meta": hub_meta(info, state, status, h, settings), "files": pdfs}
        fin["push"] = push.send(cfg, ev) or "ok"
        _write(job_dir, fin)
    try:
        (job_dir / SETTINGS_FILE).unlink()   # Zugangsdaten nicht liegen lassen
    except OSError:
        pass
    return fin


def _write(job_dir: Path, fin: dict):
    tmp = job_dir / (FINISH_FILE + ".tmp")
    try:
        fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o644)
        with os.fdopen(fd, "w") as f:
            json.dump(fin, f)
        os.replace(tmp, job_dir / FINISH_FILE)
    except OSError:
        pass
