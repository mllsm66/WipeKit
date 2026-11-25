"""Gemeinsame Daten und Funktionen für WipeKit (Linux) – GUI und Lösch-Prozess."""

import json
import os
import re
import subprocess
from dataclasses import dataclass, field, asdict
from datetime import datetime, timedelta

APP_NAME = "WipeKit"
APP_ID = "io.github.mllsm66.WipeKit"
VERSION = "1.2.0"

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
    verify: str              # "full" (jeder Sektor), "samples" (Stichproben) oder ""
    trims: bool
    explanation: str
    best_for: str
    security_level: int      # 0 … 5
    security_note: str
    method_description: str
    standard: str
    purge: bool = False      # Firmware-Löschung durch das Laufwerk selbst
    read_only: bool = False  # ändert keine Daten (»Nur prüfen«)

    @property
    def verifies(self) -> bool:
        return self.verify == "full"

    @property
    def work_units(self) -> int:
        return len(self.passes) + (1 if self.verify == "full" or self.read_only else 0)

    @property
    def step_count(self) -> int:
        if self.purge:
            return 3
        return max(1, len(self.passes) + (1 if self.verify or self.read_only else 0) + (1 if self.trims else 0))

    @property
    def security_label(self) -> str:
        return {0: "Keine Löschung", 3: "Hoch", 4: "Sehr hoch", 5: "Höchste Stufe"}.get(self.security_level, "Hoch")


CLEAR = "NIST SP 800-88 Rev. 2 – »Clear« (Überschreiben)"
NOT_CERTIFIED = "WipeKit ist nicht zertifiziert und steht in keiner Verbindung zu NIST oder anderen Normungsstellen."
MODES = [
    WipeMode(
        "quick", "Schnell", "wipekit-quick-symbolic", "1 Durchgang · Stichproben-Prüfung",
        (RANDOM,), "samples", False,
        "Überschreibt jeden Sektor des Laufwerks einmal mit kryptografisch erzeugten Zufallsdaten. "
        "Anschließend werden Anfang, Ende und rund 2.000 zufällig verteilte Stellen gelesen und mit den "
        "geschriebenen Daten verglichen.",
        "Festplatten (HDD), wenn es schnell gehen soll", 3,
        "Orientiert sich für Festplatten am Vorgehen »Clear« (NIST SP 800-88). Die Stichproben erkennen typische Fehler wie "
        "einen defekten USB-Adapter – eine Lücke an einer einzelnen Stelle kann aber nur die vollständige "
        "Prüfung der Modi »Sicher« und »Maximal« ausschließen.",
        "1 Durchgang Zufallsdaten + Stichproben-Prüfung (Anfang, Ende, ca. 2.000 Stellen)", CLEAR,
    ),
    WipeMode(
        "secure", "Sicher", "wipekit-shield-symbolic", "2 Durchgänge · vollständige Prüfung",
        (RANDOM, ZEROS), "full", False,
        "Überschreibt das Laufwerk zuerst mit Zufallsdaten und anschließend mit Nullen. Danach wird "
        "jeder Sektor gelesen und geprüft, ob er tatsächlich überschrieben wurde.",
        "Festplatten mit sensiblen Daten – die empfohlene Standardwahl", 4,
        "Die Prüfung stellt sicher, dass kein Bereich ausgelassen wurde, und erkennt defekte Sektoren "
        "sowie unzuverlässige USB-Adapter.",
        "2 Durchgänge (Zufallsdaten, Nullen) + vollständige Lese-Verifikation", CLEAR,
    ),
    WipeMode(
        "maximum", "Maximal", "wipekit-lock-symbolic", "3 Durchgänge · vollständige Prüfung",
        (RANDOM, RANDOM, ZEROS), "full", False,
        "Überschreibt das Laufwerk zweimal mit Zufallsdaten und einmal mit Nullen. Danach wird jeder "
        "Sektor gelesen und geprüft.",
        "Aufträge, die mehrfaches Überschreiben ausdrücklich verlangen", 4,
        "Bei modernen Festplatten nicht messbar sicherer als »Sicher«, erfüllt aber Vorgaben mit "
        "mehreren Durchgängen (angelehnt an DoD 5220.22-M).",
        "3 Durchgänge (Zufallsdaten, Zufallsdaten, Nullen) + vollständige Lese-Verifikation",
        CLEAR + ", mehrfach (angelehnt an DoD 5220.22-M)",
    ),
    WipeMode(
        "ssd", "SSD", "wipekit-ssd-symbolic", "2 Durchgänge · Prüfung · TRIM",
        (RANDOM, SEEDED), "full", True,
        "Füllt die SSD zweimal vollständig mit Zufallsdaten und prüft den zweiten Durchgang Sektor für "
        "Sektor. Zum Schluss werden alle Speicherblöcke per TRIM an den SSD-Controller freigegeben, "
        "damit er sie intern löscht.",
        "SSDs, USB-Sticks und Speicherkarten (auch über USB)", 3,
        "Zweifaches Vollschreiben erreicht bei den meisten SSDs auch die versteckten Reservebereiche. "
        "Eine Garantie für diese Bereiche gibt nur die »Firmware-Löschung«.",
        "2 Durchgänge Zufallsdaten + vollständige Lese-Verifikation + TRIM aller Blöcke",
        CLEAR + " – für SSDs nach bestem Bemühen",
    ),
    WipeMode(
        "purge", "Firmware-Löschung", "wipekit-chip-symbolic", "Löschbefehl des Laufwerks · Stichproben",
        (), "samples", False,
        "Lässt das Laufwerk sich selbst löschen: WipeKit sendet den eingebauten Löschbefehl "
        "(NVMe/ATA »Sanitize« oder »Secure Erase«). Dabei werden auch Reserve- und Verwaltungsbereiche "
        "gelöscht, die kein Programm von außen erreicht. Vorher werden Prüfmarken geschrieben, "
        "anschließend wird kontrolliert, dass sie verschwunden sind.",
        "Interne SATA- und NVMe-SSDs – die gründlichste Methode für SSDs", 5,
        "Orientiert sich am Vorgehen »Purge« nach NIST SP 800-88 / IEEE 2883. Funktioniert nur bei direkt angeschlossenen "
        "Laufwerken (SATA/NVMe) – USB-Adapter leiten die Befehle meist nicht zuverlässig weiter.",
        "Firmware-Löschbefehl des Laufwerks + Kontrolle von Prüfmarken",
        "NIST SP 800-88 Rev. 2 / IEEE 2883 – »Purge« (Firmware-Löschung)", purge=True,
    ),
    WipeMode(
        "check", "Nur prüfen", "wipekit-check-symbolic", "Liest alles · ändert nichts",
        (), "", False,
        "Liest das gesamte Laufwerk und prüft, ob es leer ist (nur Nullen). Es werden keine Daten "
        "verändert. So lässt sich zum Beispiel eine Löschung mit einem anderen Programm kontrollieren. "
        "Nebenbei werden unlesbare Stellen gezählt und die Lesegeschwindigkeit aufgezeichnet.",
        "Kontrolle eines bereits gelöschten oder neuen Laufwerks", 0,
        "Nach »Sicher« und »Maximal« ist ein Laufwerk leer (nur Nullen). Nach »Schnell« und »SSD« "
        "enthält es Zufallsdaten und gilt deshalb hier als »nicht leer« – das ist dann kein Fehler.",
        "Vollständiges Lesen aller Sektoren, keine Schreibzugriffe", "– (keine Löschung, nur Kontrolle)",
        read_only=True,
    ),
]
MODES_BY_KEY = {m.key: m for m in MODES}

# S.M.A.R.T.-Selbsttests laufen als Vorgang wie ein Löschvorgang, ändern aber keine Daten
SELFTEST_MODES = [
    WipeMode("selftest-short", "Kurztest", "wipekit-pulse-symbolic", "S.M.A.R.T.-Selbsttest, ca. 2 Minuten",
             (), "", False, "Das Laufwerk prüft seine Elektronik und einen kleinen Teil der Oberfläche selbst.",
             "Schneller Gesundheitscheck", 0, "", "S.M.A.R.T.-Kurztest", "", read_only=True),
    WipeMode("selftest-long", "Langtest", "wipekit-pulse-symbolic", "S.M.A.R.T.-Selbsttest der gesamten Oberfläche",
             (), "", False, "Das Laufwerk liest seine gesamte Oberfläche selbst und meldet fehlerhafte Stellen.",
             "Gründlicher Gesundheitscheck", 0, "", "S.M.A.R.T.-Langtest", "", read_only=True),
    WipeMode("selftest-conveyance", "Transporttest", "wipekit-pulse-symbolic", "S.M.A.R.T.-Selbsttest nach Transport",
             (), "", False, "Erkennt Schäden, die beim Transport entstanden sein können.",
             "Nach Versand oder Sturz", 0, "", "S.M.A.R.T.-Transporttest", "", read_only=True),
]
MODES_BY_KEY.update({m.key: m for m in SELFTEST_MODES})


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
    sampleResult: str = None        # Ergebnis der Stichproben-Prüfung
    hiddenArea: str = None          # HPA-Prüfung
    purgeMethod: str = None         # verwendeter Firmware-Befehl
    nonZeroBytes: int = None        # »Nur prüfen«: Bytes, die nicht 0 sind
    badBlocks: int = 0              # unlesbare 64-KiB-Bereiche
    curve: list = None              # Lesegeschwindigkeit je 0,5 % (MB/s)
    indeterminate: bool = False     # Fortschritt unbekannt (Firmware arbeitet)

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
    internal: bool = False
    transport: str = ""     # lsblk TRAN, z. B. usb, sata, nvme
    serial: str = ""
    read_only: bool = False

    @property
    def type_label(self) -> str:
        if self.is_ssd is True:
            return "SSD"
        if self.is_ssd is False:
            return "Festplatte"
        return "Typ unbekannt"

    @property
    def icon(self) -> str:
        if self.bus in ("SD", "MMC"):
            return "wipekit-sd-symbolic"
        if self.transport == "usb" and self.is_ssd is True and self.size < 300e9:
            return "wipekit-usb-symbolic"
        return "wipekit-drive-symbolic"

    @property
    def direct_attached(self) -> bool:
        """SATA/NVMe direkt am Mainboard – Voraussetzung für die Firmware-Löschung."""
        return self.transport in ("sata", "ata", "nvme", "sas") or self.name.startswith("nvme")


def _bool(v) -> bool:
    if isinstance(v, bool):
        return v
    return str(v).strip() in ("1", "true", "True")


def _walk(node):
    for child in node.get("children") or []:
        yield child
        yield from _walk(child)


def list_disks(include_loop: bool = False) -> list:
    """Alle Laufwerke – externe (USB, SD …) und interne. include_loop nur für automatische Tests."""
    try:
        out = subprocess.run(
            ["lsblk", "-J", "-b", "-o",
             "NAME,PATH,SIZE,TYPE,TRAN,ROTA,MODEL,VENDOR,SERIAL,RM,RO,HOTPLUG,MOUNTPOINT,LABEL"],
            capture_output=True, text=True, timeout=15, check=True,
        ).stdout
        devices = json.loads(out).get("blockdevices", [])
    except Exception:
        return []

    disks = []
    for d in devices:
        name = d.get("name", "")
        is_test_loop = include_loop and d.get("type") == "loop"
        if d.get("type") != "disk" and not is_test_loop:
            continue
        if name.startswith(("zram", "ram")) or re.search(r"(boot\d+|rpmb)$", name):
            continue
        tran = (d.get("tran") or "").lower()
        hotplug = _bool(d.get("hotplug")) or _bool(d.get("rm"))
        internal = not is_test_loop and tran not in EXTERNAL_TRANSPORTS and not hotplug
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
        # Dateisystem direkt auf dem Gerät (ohne Partitionstabelle, z. B. bei manchen USB-Sticks)
        if d.get("label"):
            volumes.append(d["label"])
        if d.get("mountpoint"):
            mounts.append(d["mountpoint"])
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
        bus = {"usb": "USB", "ieee1394": "FireWire", "mmc": "SD", "sdio": "SD", "sata": "SATA", "ata": "ATA",
               "nvme": "NVMe", "sas": "SAS", "virtio": "Virtuell"}.get(tran, tran.upper() or "?")
        if is_test_loop:
            bus = "Test"
        disks.append(Disk(
            name=name, path=d.get("path") or "/dev/" + name,
            model=model or "Unbekanntes Laufwerk", size=size, bus=bus, is_ssd=is_ssd,
            volumes=volumes, mountpoints=mounts, holders=holders, is_protected=protected,
            internal=internal, transport=tran, serial=(d.get("serial") or "").strip(),
            read_only=_bool(d.get("ro")),
        ))
    disks.sort(key=lambda x: (x.internal, x.name))
    return disks


def list_external_disks(include_loop: bool = False) -> list:
    return [d for d in list_disks(include_loop) if not d.internal]


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
