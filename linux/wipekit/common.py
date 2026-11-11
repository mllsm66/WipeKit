"""Gemeinsame Daten und Funktionen für WipeKit (Linux) – GUI und Lösch-Prozess."""

import json
import os
import subprocess
from dataclasses import dataclass, field, asdict
from datetime import datetime, timedelta

APP_NAME = "WipeKit"
APP_ID = "io.github.mllsm66.WipeKit"
VERSION = "1.0.0"

# ---------------------------------------------------------------------------
# Löschmodi
# ---------------------------------------------------------------------------

RANDOM, SEEDED, ZEROS = "random", "seeded", "zeros"


@dataclass(frozen=True)
class WipeMode:
    key: str
    title: str
    icon: str
    tagline: str
    passes: tuple
    verifies: bool
    trims: bool
    explanation: str
    best_for: str
    security_level: int
    security_note: str
    method_description: str

    @property
    def work_units(self) -> int:
        return len(self.passes) + (1 if self.verifies else 0)

    @property
    def step_count(self) -> int:
        return self.work_units + (1 if self.trims else 0)

    @property
    def security_label(self) -> str:
        return "Sehr hoch" if self.security_level >= 4 else "Hoch"


MODES = [
    WipeMode(
        "quick", "Schnell", "media-seek-forward-symbolic", "1 Durchgang · ohne Prüfung",
        (RANDOM,), False, False,
        "Überschreibt jeden Sektor des Laufwerks einmal mit kryptografisch erzeugten Zufallsdaten. "
        "Die bisherigen Daten lassen sich danach weder mit Software noch mit Labormethoden wiederherstellen.",
        "Festplatten (HDD), wenn es schnell gehen soll", 3,
        "Entspricht für Festplatten dem Standard NIST SP 800-88 »Clear«. Ob wirklich jeder Sektor "
        "beschrieben wurde, wird nicht kontrolliert.",
        "1 Durchgang Zufallsdaten",
    ),
    WipeMode(
        "secure", "Sicher", "security-high-symbolic", "2 Durchgänge · mit Prüfung",
        (RANDOM, ZEROS), True, False,
        "Überschreibt das Laufwerk zuerst mit Zufallsdaten und anschließend mit Nullen. Danach wird "
        "jeder Sektor gelesen und geprüft, ob er tatsächlich überschrieben wurde.",
        "Festplatten mit sensiblen Daten – die empfohlene Standardwahl", 4,
        "Die Prüfung stellt sicher, dass kein Bereich ausgelassen wurde, und erkennt defekte Sektoren "
        "sowie unzuverlässige USB-Adapter.",
        "2 Durchgänge (Zufallsdaten, Nullen) + vollständige Lese-Verifikation",
    ),
    WipeMode(
        "maximum", "Maximal", "channel-secure-symbolic", "3 Durchgänge · mit Prüfung",
        (RANDOM, RANDOM, ZEROS), True, False,
        "Überschreibt das Laufwerk zweimal mit Zufallsdaten und einmal mit Nullen. Danach wird jeder "
        "Sektor gelesen und geprüft.",
        "Aufträge, die mehrfaches Überschreiben ausdrücklich verlangen", 4,
        "Bei modernen Festplatten nicht messbar sicherer als »Sicher«, erfüllt aber Vorgaben mit "
        "mehreren Durchgängen (angelehnt an DoD 5220.22-M).",
        "3 Durchgänge (Zufallsdaten, Zufallsdaten, Nullen) + vollständige Lese-Verifikation",
    ),
    WipeMode(
        "ssd", "SSD", "drive-harddisk-solidstate-symbolic", "2 Durchgänge · Prüfung · TRIM",
        (RANDOM, SEEDED), True, True,
        "Füllt die SSD zweimal vollständig mit Zufallsdaten und prüft den zweiten Durchgang Sektor für "
        "Sektor. Zum Schluss werden alle Speicherblöcke per TRIM an den SSD-Controller freigegeben, "
        "damit er sie intern löscht.",
        "SSDs, USB-Sticks und Speicherkarten", 3,
        "Zweifaches Vollschreiben erreicht bei den meisten SSDs auch die versteckten Reservebereiche. "
        "Eine vollständige Garantie bietet nur das Firmware-Kommando »Secure Erase« des Herstellers.",
        "2 Durchgänge Zufallsdaten + vollständige Lese-Verifikation + TRIM aller Blöcke",
    ),
]
MODES_BY_KEY = {m.key: m for m in MODES}


def pattern_label(p: str) -> str:
    return "Nullen schreiben" if p == ZEROS else "Zufallsdaten schreiben"


# ---------------------------------------------------------------------------
# Status (wird vom Lösch-Prozess als status.json geschrieben)
# ---------------------------------------------------------------------------

ACTIVE_STATES = ("starting", "running")


@dataclass
class HelperStatus:
    state: str = "starting"
    pid: int = 0
    phase: str = "Vorbereitung …"
    step: int = 0
    stepCount: int = 0
    diskSize: int = 0
    bytesDoneTotal: int = 0
    bytesTotal: int = 0
    speed: float = 0.0
    startedAt: float = 0.0
    updatedAt: float = 0.0
    finishedAt: float = None
    message: str = None
    verified: bool = None
    trimResult: str = None
    sleepPrevented: bool = False

    def to_json(self) -> str:
        return json.dumps(asdict(self))

    @staticmethod
    def from_json(text: str) -> "HelperStatus":
        data = json.loads(text)
        st = HelperStatus()
        for k, v in data.items():
            if hasattr(st, k):
                setattr(st, k, v)
        return st


# ---------------------------------------------------------------------------
# Laufwerke
# ---------------------------------------------------------------------------

EXTERNAL_TRANSPORTS = ("usb", "ieee1394", "mmc", "sdio")
CRITICAL_MOUNTS = ("/", "/boot", "/boot/efi", "/efi", "/home", "/usr", "/var", "[SWAP]")


@dataclass
class Disk:
    name: str               # z. B. "sdb"
    path: str               # z. B. "/dev/sdb"
    model: str
    size: int
    bus: str
    is_ssd: object          # True / False / None (unbekannt)
    volumes: list = field(default_factory=list)
    mountpoints: list = field(default_factory=list)
    holders: list = field(default_factory=list)   # aktive crypt/lvm-Geräte darunter
    is_protected: bool = False

    @property
    def type_label(self) -> str:
        if self.is_ssd is True:
            return "SSD"
        if self.is_ssd is False:
            return "Festplatte"
        return "Typ unbekannt"

    @property
    def icon(self) -> str:
        if self.bus == "USB":
            return "drive-harddisk-usb-symbolic"
        if self.bus in ("SD", "MMC"):
            return "media-flash-symbolic"
        return "drive-harddisk-symbolic"


def _bool(v) -> bool:
    if isinstance(v, bool):
        return v
    return str(v).strip() in ("1", "true", "True")


def _walk(node):
    for child in node.get("children") or []:
        yield child
        yield from _walk(child)


def list_external_disks() -> list:
    """Entspricht ungefähr `diskutil list external physical` auf dem Mac."""
    try:
        out = subprocess.run(
            ["lsblk", "-J", "-b", "-o",
             "NAME,PATH,SIZE,TYPE,TRAN,ROTA,MODEL,VENDOR,RM,HOTPLUG,MOUNTPOINT,LABEL"],
            capture_output=True, text=True, timeout=15, check=True,
        ).stdout
        devices = json.loads(out).get("blockdevices", [])
    except Exception:
        return []

    disks = []
    for d in devices:
        if d.get("type") != "disk":
            continue
        tran = (d.get("tran") or "").lower()
        hotplug = _bool(d.get("hotplug")) or _bool(d.get("rm"))
        if tran not in EXTERNAL_TRANSPORTS and not hotplug:
            continue
        try:
            size = int(d.get("size") or 0)
        except ValueError:
            size = 0
        if size <= 0:
            continue   # z. B. leerer Kartenleser

        rota = _bool(d.get("rota"))
        if not rota:
            is_ssd = True
        elif tran == "usb":
            is_ssd = None      # USB-Adapter melden oft fälschlich »rotierend«
        else:
            is_ssd = False

        volumes, mounts, holders = [], [], []
        protected = False
        for child in _walk(d):
            if child.get("label"):
                volumes.append(child["label"])
            mp = child.get("mountpoint")
            if mp:
                mounts.append(mp)
                if mp in CRITICAL_MOUNTS:
                    protected = True
            if child.get("type") in ("crypt", "lvm", "raid0", "raid1", "raid5", "raid10", "dm"):
                holders.append(child.get("path") or child.get("name"))
        if d.get("mountpoint") in CRITICAL_MOUNTS:
            protected = True

        model = " ".join(x for x in [(d.get("vendor") or "").strip(), (d.get("model") or "").strip()] if x)
        bus = {"usb": "USB", "ieee1394": "FireWire", "mmc": "SD", "sdio": "SD"}.get(tran, tran.upper() or "?")
        disks.append(Disk(
            name=d.get("name", ""), path=d.get("path") or "/dev/" + d.get("name", ""),
            model=model or "Unbekanntes Laufwerk", size=size, bus=bus, is_ssd=is_ssd,
            volumes=volumes, mountpoints=mounts, holders=holders, is_protected=protected,
        ))
    disks.sort(key=lambda x: x.name)
    return disks


def recommended_mode(disk: Disk) -> str:
    return "ssd" if disk.is_ssd is True else "secure"


# ---------------------------------------------------------------------------
# Formatierung
# ---------------------------------------------------------------------------

def fmt_bytes(b) -> str:
    b = float(max(0, b or 0))
    for unit in ("Bytes", "KB", "MB", "GB", "TB", "PB"):
        if b < 1000 or unit == "PB":
            if unit == "Bytes":
                return f"{int(b)} Bytes"
            return f"{b:.1f} {unit}".replace(".", ",") if b < 100 else f"{b:.0f} {unit}"
        b /= 1000
    return ""


def fmt_exact_bytes(b) -> str:
    return f"{int(b):,}".replace(",", ".") + " Bytes"


def fmt_speed(s) -> str:
    return fmt_bytes(s) + "/s"


def fmt_duration(t) -> str:
    s = int(round(max(0, t or 0)))
    h, m, sec = s // 3600, (s % 3600) // 60, s % 60
    if h:
        return f"{h} Std. {m} Min."
    if m:
        return f"{m} Min. {sec} Sek."
    return f"{sec} Sek."


def fmt_short_duration(t) -> str:
    s = int(round(max(0, t or 0)))
    h, m = s // 3600, (s % 3600) // 60
    if h:
        return f"{h} Std. {m} Min."
    if m:
        return f"{m} Min."
    return "< 1 Min."


WEEKDAYS = ["Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag", "Sonntag"]


def fmt_clock(ts: float) -> str:
    d = datetime.fromtimestamp(ts)
    today = datetime.now().date()
    if d.date() == today:
        prefix = "heute"
    elif d.date() == today + timedelta(days=1):
        prefix = "morgen"
    else:
        prefix = WEEKDAYS[d.weekday()]
    return f"{prefix}, {d:%H:%M} Uhr"


def fmt_datetime(ts: float) -> str:
    return datetime.fromtimestamp(ts).strftime("%d.%m.%Y, %H:%M:%S Uhr")


def fmt_percent(fraction: float) -> str:
    p = max(0.0, min(1.0, fraction or 0.0)) * 100
    if p >= 10 or p == 0:
        return f"{int(p)} %"
    return f"{p:.1f} %".replace(".", ",")


def pid_alive(pid: int) -> bool:
    if not pid or pid <= 0:
        return False
    try:
        os.kill(pid, 0)
        return True
    except PermissionError:
        return True    # läuft, gehört aber root
    except OSError:
        return False
