"""Auswertung der S.M.A.R.T.-Daten (smartctl -x -j) – Gesamtzustand, Restlebensdauer, Kennwerte.

Die gleiche Logik gibt es in der Mac-App (Swift) und der Windows-App (C#).
"""

import json
import re
from dataclasses import dataclass, field

# Gesamtzustand
VERY_GOOD, GOOD, CAUTION, BAD, UNKNOWN = "Sehr gut", "Gut", "Bedenklich", "Schlecht", "Unbekannt"
RATING_COLORS = {          # RGB 0…1
    VERY_GOOD: (0.12, 0.56, 0.24),
    GOOD: (0.10, 0.45, 0.91),
    CAUTION: (0.86, 0.55, 0.00),
    BAD: (0.85, 0.19, 0.15),
    UNKNOWN: (0.50, 0.53, 0.55),
}

# Deutsche Namen der wichtigsten ATA-Attribute
ATTRIBUTE_NAMES = {
    1: "Lesefehlerrate", 2: "Durchsatzleistung", 3: "Anlaufzeit", 4: "Start/Stopp-Zähler",
    5: "Ersetzte Sektoren", 7: "Suchfehlerrate", 8: "Suchzeit-Leistung", 9: "Betriebsstunden",
    10: "Anlauf-Wiederholungen", 11: "Kalibrierungs-Wiederholungen", 12: "Einschaltvorgänge",
    13: "Soft-Lesefehlerrate", 160: "Unkorrigierbare Sektoren (Lesen/Schreiben)", 161: "Verfügbare Reserveblöcke",
    163: "Anfänglich defekte Blöcke", 164: "Gesamte Löschzyklen", 165: "Maximale Löschzyklen",
    166: "Minimale Löschzyklen", 167: "Durchschnittliche Löschzyklen", 168: "Maximale Löschzyklen (Spezifikation)",
    169: "Restlebensdauer", 170: "Verfügbare Reserveblöcke", 171: "Programmierfehler", 172: "Löschfehler",
    173: "Verschleißausgleich (Löschzyklen)", 174: "Unerwartete Stromausfälle", 175: "Stromausfallschutz",
    177: "Verschleißausgleich", 179: "Belegte Reserveblöcke", 180: "Freie Reserveblöcke",
    181: "Programmierfehler", 182: "Löschfehler", 183: "SATA-Herabstufungen", 184: "Ende-zu-Ende-Fehler",
    187: "Gemeldete unkorrigierbare Fehler", 188: "Befehls-Zeitüberschreitungen", 189: "Hohe Flughöhe",
    190: "Luftstromtemperatur", 191: "Erschütterungen", 192: "Notparkvorgänge", 193: "Lade-/Entladezyklen",
    194: "Temperatur", 195: "Hardware-ECC-Korrekturen", 196: "Umlagerungsereignisse",
    197: "Ausstehende Sektoren", 198: "Unkorrigierbare Sektoren", 199: "CRC-Fehler (Kabel/Adapter)",
    200: "Schreibfehlerrate", 201: "Soft-Lesefehler", 202: "Restlebensdauer", 206: "Flughöhe",
    220: "Plattenverschiebung", 222: "Belastete Stunden", 223: "Lade-/Entlade-Wiederholungen",
    224: "Lade-Reibung", 225: "Lade-/Entladezyklen", 226: "Ladezeit", 230: "Kopfamplitude",
    231: "Restlebensdauer", 232: "Verfügbare Reserve", 233: "Verschleißindikator", 234: "Durchschnittliche Löschzyklen",
    235: "Gute Blöcke / Systemblöcke", 240: "Kopf-Flugstunden", 241: "Geschriebene Daten", 242: "Gelesene Daten",
    246: "Geschriebene Daten (gesamt)", 247: "Geschriebene Seiten (Host)", 248: "Geschriebene Seiten (FTL)",
    249: "Geschriebene Daten (NAND, GiB)", 254: "Freifall-Schutz",
}

# Hersteller: (Muster am Anfang von Modellfamilie/Modell, Herstellername)
VENDORS = [
    (r"WDC\b", "Western Digital"), (r"WD_?BLACK", "Western Digital"), (r"WD\d", "Western Digital"),
    (r"WD\b", "Western Digital"), (r"Western Digital", "Western Digital"), (r"ST\d", "Seagate"),
    (r"Seagate", "Seagate"), (r"TOSHIBA", "Toshiba"), (r"KIOXIA", "Kioxia"), (r"HGST", "HGST"),
    (r"HUS|HUH|HUA", "HGST"), (r"Hitachi|HDS\d|HTS\d|HDT\d", "Hitachi"), (r"SAMSUNG|Samsung", "Samsung"),
    (r"Crucial|CT\d", "Crucial"), (r"Micron|MTFD", "Micron"), (r"INTEL|Intel|SSDSC|SSDPE", "Intel"),
    (r"KINGSTON|Kingston|SA400|SKC|SNV|SUV", "Kingston"), (r"SanDisk|SDSSD", "SanDisk"),
    (r"SK ?hynix|HFS\d|HFM\d", "SK hynix"), (r"ADATA|XPG", "ADATA"), (r"Transcend|TS\d+G", "Transcend"),
    (r"APPLE|Apple", "Apple"), (r"Maxtor", "Maxtor"), (r"FUJITSU|Fujitsu|MHV|MHW|MHZ|MJA", "Fujitsu"),
    (r"Corsair|CSSD", "Corsair"), (r"Patriot", "Patriot"), (r"PNY", "PNY"), (r"Lexar", "Lexar"),
    (r"Intenso", "Intenso"), (r"Verbatim", "Verbatim"), (r"Phison", "Phison"), (r"TEAM|Team", "Teamgroup"),
    (r"Netac", "Netac"), (r"Silicon Power|SPCC", "Silicon Power"), (r"Gigabyte|GIGABYTE|GP-", "Gigabyte"),
    (r"Sabrent", "Sabrent"), (r"LITEON|LITE-ON|Lite-On", "Lite-On"), (r"Plextor|PLEXTOR", "Plextor"),
    (r"OCZ", "OCZ"), (r"Lenovo", "Lenovo"), (r"Dell|DELL", "Dell"), (r"HP\b|HPE", "HP"),
    (r"Hikvision|HS-SSD", "Hikvision"), (r"Mushkin", "Mushkin"), (r"Goodram|GOODRAM|IR-SSD", "Goodram"),
    (r"Inland", "Inland"), (r"Kingdian", "Kingdian"), (r"Fanxiang", "Fanxiang"), (r"Biwin|BIWIN", "Biwin"),
]
VENDOR_PREFIX_TOKENS = {"WDC", "WD", "TOSHIBA", "HGST", "Hitachi", "SAMSUNG", "Samsung", "INTEL", "KINGSTON",
                        "Kingston", "SanDisk", "Crucial", "Micron", "APPLE", "Apple", "FUJITSU", "ADATA",
                        "Transcend", "Seagate", "Corsair", "PNY", "Lexar", "Intenso", "Verbatim", "Patriot"}


def detect_vendor(*names) -> str:
    for n in names:
        if not n:
            continue
        for pattern, vendor in VENDORS:
            if re.match(pattern, n.strip()):
                return vendor
    return ""


def model_number(model_name: str) -> str:
    """'WDC WD5000AAKX-00ERMA0' -> 'WD5000AAKX-00ERMA0'"""
    parts = (model_name or "").split()
    if len(parts) > 1 and parts[0] in VENDOR_PREFIX_TOKENS:
        parts = parts[1:]
    return " ".join(parts)


@dataclass
class Attribute:
    id: int
    name: str
    value: int
    worst: int
    thresh: int
    raw: int
    raw_string: str
    failed: str            # "", "now", "past"
    status: str            # "OK", "Warnung", "Fehler"


@dataclass
class SelfTest:
    kind: str
    result: str
    hours: object
    passed: object


@dataclass
class Health:
    available: bool = False
    reason: str = ""
    protocol: str = ""             # ATA, NVMe, SCSI
    vendor: str = ""
    family: str = ""
    model: str = ""                # vollständige Modellbezeichnung
    model_number: str = ""
    serial: str = ""
    firmware: str = ""
    capacity: int = 0
    is_ssd: object = None
    rpm: object = None             # int oder None
    form_factor: str = ""
    interface: str = ""            # z. B. "SATA 600 (aktuell SATA 300)"
    interface_max: str = ""
    interface_current: str = ""
    sata_version: str = ""
    ata_version: str = ""
    trim: object = None
    security: str = ""
    security_frozen: object = None
    smart_passed: object = None
    temperature: object = None
    power_on_hours: object = None
    power_cycles: object = None
    bytes_written: object = None
    bytes_read: object = None
    pending: object = None
    uncorrectable: object = None
    reallocated: object = None
    crc_errors: object = None
    life_percent: object = None    # Restlebensdauer in %
    nvme: dict = field(default_factory=dict)
    attributes: list = field(default_factory=list)
    self_tests: list = field(default_factory=list)
    self_test_running: object = None     # Restprozent eines laufenden Tests
    supports_conveyance: bool = False
    supports_self_tests: bool = False
    short_minutes: object = None
    long_minutes: object = None
    rating: str = UNKNOWN
    reasons: list = field(default_factory=list)


def _raw(attrs, *ids):
    for i in ids:
        a = attrs.get(i)
        if a is not None:
            return a["raw"].get("value")
    return None


def _stat(j, name):
    for page in (j.get("ata_device_statistics") or {}).get("pages", []) or []:
        for e in page.get("table", []) or []:
            if e.get("name") == name and isinstance(e.get("value"), int):
                return e["value"]
    return None


def _sata_label(speed: dict) -> str:
    if not speed:
        return ""
    ups, bpu = speed.get("units_per_second"), speed.get("bits_per_unit")
    if ups and bpu:
        gbit = ups * bpu / 1e9
        if abs(gbit - 1.5) < 0.2:
            return "SATA 150"
        if abs(gbit - 3.0) < 0.2:
            return "SATA 300"
        if abs(gbit - 6.0) < 0.2:
            return "SATA 600"
        return f"{gbit:g} Gbit/s"
    s = speed.get("string", "")
    return {"1.5 Gb/s": "SATA 150", "3.0 Gb/s": "SATA 300", "6.0 Gb/s": "SATA 600"}.get(s, s)


def _bytes_from_lba_attr(a, block):
    name = (a.get("name") or "").lower()
    raw = a["raw"].get("value")
    if not isinstance(raw, int):
        return None
    if "32mib" in name:
        return raw * 32 * 1024 * 1024
    if "gib" in name:
        return raw * 1024 ** 3
    if "lba" in name:
        return raw * block
    return None


def parse(j: dict, reason_if_unavailable: str = "") -> Health:
    h = Health()
    dev = j.get("device") or {}
    h.protocol = dev.get("protocol", "")
    h.model = (j.get("model_name") or j.get("scsi_model_name") or "").strip()
    h.family = (j.get("model_family") or "").strip()
    h.vendor = detect_vendor(h.family, h.model, j.get("scsi_vendor", ""))
    h.model_number = model_number(h.model)
    h.serial = (j.get("serial_number") or "").strip()
    h.firmware = (j.get("firmware_version") or "").strip()
    h.capacity = (j.get("user_capacity") or {}).get("bytes") or j.get("nvme_total_capacity") or 0

    nvme = j.get("nvme_smart_health_information_log")
    attrs_table = (j.get("ata_smart_attributes") or {}).get("table") or []
    h.available = bool(nvme or attrs_table or "smart_status" in j)
    if not h.available:
        msgs = [m.get("string", "") for m in (j.get("smartctl") or {}).get("messages", [])]
        h.reason = reason_if_unavailable or friendly_reason(msgs)
        h.rating = UNKNOWN
        return h

    block = j.get("logical_block_size") or 512
    rr = j.get("rotation_rate")
    if h.protocol == "NVMe":
        h.is_ssd, h.rpm = True, None
    elif isinstance(rr, int):
        h.is_ssd = rr == 0
        h.rpm = rr if rr > 0 else None
    h.form_factor = (j.get("form_factor") or {}).get("name", "")
    h.sata_version = (j.get("sata_version") or {}).get("string", "")
    h.ata_version = (j.get("ata_version") or {}).get("string", "")
    sp = j.get("interface_speed") or {}
    h.interface_max = _sata_label(sp.get("max"))
    h.interface_current = _sata_label(sp.get("current"))
    if h.protocol == "NVMe":
        h.interface = "NVMe (PCIe)"
    elif h.interface_max:
        h.interface = h.interface_max + (f" (aktuell {h.interface_current})"
                                         if h.interface_current and h.interface_current != h.interface_max else "")
    trim = j.get("trim")
    h.trim = trim.get("supported") if isinstance(trim, dict) else None
    sec = j.get("ata_security")
    if isinstance(sec, dict):
        h.security = sec.get("string", "")
        h.security_frozen = sec.get("frozen")
    ss = j.get("smart_status") or {}
    h.smart_passed = ss.get("passed")
    h.temperature = (j.get("temperature") or {}).get("current")
    h.power_on_hours = (j.get("power_on_time") or {}).get("hours")
    h.power_cycles = j.get("power_cycle_count")

    attrs = {a["id"]: a for a in attrs_table if "id" in a}
    for a in attrs_table:
        failed = a.get("when_failed") or ""
        thresh = a.get("thresh", 0) or 0
        value = a.get("value", 0)
        status = "OK"
        if failed in ("now", "FAILING_NOW"):
            status = "Fehler"
        elif failed in ("past", "In_the_past"):
            status = "Warnung"
        elif a["id"] in (5, 196, 197, 198) and isinstance(a["raw"].get("value"), int) and a["raw"]["value"] > 0:
            status = "Warnung"
        elif thresh and value <= thresh:
            status = "Fehler"
        h.attributes.append(Attribute(
            id=a["id"], name=ATTRIBUTE_NAMES.get(a["id"], a.get("name", "").replace("_", " ")),
            value=value, worst=a.get("worst", 0), thresh=thresh,
            raw=a["raw"].get("value"), raw_string=a["raw"].get("string", str(a["raw"].get("value", ""))),
            failed=failed, status=status))

    if nvme:
        h.nvme = nvme
        h.temperature = h.temperature if h.temperature is not None else nvme.get("temperature")
        h.power_on_hours = nvme.get("power_on_hours", h.power_on_hours)
        h.power_cycles = nvme.get("power_cycles", h.power_cycles)
        if isinstance(nvme.get("data_units_written"), int):
            h.bytes_written = nvme["data_units_written"] * 512000
        if isinstance(nvme.get("data_units_read"), int):
            h.bytes_read = nvme["data_units_read"] * 512000
        if isinstance(nvme.get("percentage_used"), int):
            h.life_percent = max(0, 100 - nvme["percentage_used"])
        h.uncorrectable = nvme.get("media_errors")
    else:
        h.pending = _raw(attrs, 197)
        h.uncorrectable = _raw(attrs, 198)
        h.reallocated = _raw(attrs, 5)
        h.crc_errors = _raw(attrs, 199)
        w = _stat(j, "Logical Sectors Written")
        r = _stat(j, "Logical Sectors Read")
        h.bytes_written = w * block if w is not None else None
        h.bytes_read = r * block if r is not None else None
        if h.bytes_written is None:
            for i in (241, 246):
                if i in attrs:
                    h.bytes_written = _bytes_from_lba_attr(attrs[i], block)
                    if h.bytes_written is not None:
                        break
        if h.bytes_read is None and 242 in attrs:
            h.bytes_read = _bytes_from_lba_attr(attrs[242], block)

    # Restlebensdauer (nur SSD)
    if h.life_percent is None and h.is_ssd:
        eu = (j.get("endurance_used") or {}).get("current_percent")
        pu = _stat(j, "Percentage Used Endurance Indicator")
        if isinstance(eu, int):
            h.life_percent = max(0, 100 - eu)
        elif isinstance(pu, int):
            h.life_percent = max(0, 100 - pu)
        else:
            for i in (231, 169, 202, 233, 177):
                if i in attrs and isinstance(attrs[i].get("value"), int) and 0 < attrs[i]["value"] <= 100:
                    h.life_percent = attrs[i]["value"]
                    break

    # Selbsttests
    data = j.get("ata_smart_data") or {}
    st = data.get("self_test") or {}
    caps = data.get("capabilities") or {}
    h.supports_self_tests = bool(caps.get("self_tests_supported")) or h.protocol == "NVMe"
    h.supports_conveyance = bool(caps.get("conveyance_self_test_supported"))
    pm = st.get("polling_minutes") or {}
    h.short_minutes, h.long_minutes = pm.get("short"), pm.get("extended")
    status = st.get("status") or {}
    if isinstance(status.get("remaining_percent"), int) or (status.get("value", 0) >> 4) == 0xF:
        h.self_test_running = status.get("remaining_percent", 0)
    nlog = j.get("nvme_self_test_log") or {}
    if (nlog.get("current_self_test_operation") or {}).get("value", 0) not in (0, None):
        h.self_test_running = 100 - (nlog.get("current_self_test_completion_percent") or 0)
    for e in (((j.get("ata_smart_self_test_log") or {}).get("standard") or {}).get("table") or [])[:20]:
        h.self_tests.append(SelfTest(kind=_test_kind((e.get("type") or {}).get("string", "")),
                                     result=_test_result((e.get("status") or {}).get("string", "")),
                                     hours=e.get("lifetime_hours"), passed=(e.get("status") or {}).get("passed")))
    for e in (nlog.get("table") or [])[:20]:
        res = (e.get("self_test_result") or {})
        h.self_tests.append(SelfTest(kind=_test_kind((e.get("self_test_code") or {}).get("string", "")),
                                     result=_test_result(res.get("string", "")),
                                     hours=e.get("power_on_hours"), passed=res.get("value") == 0))

    rate(h, nvme)
    return h


def _test_kind(s: str) -> str:
    s = s.lower()
    if "short" in s:
        return "Kurztest"
    if "extended" in s or "long" in s:
        return "Langtest"
    if "conveyance" in s:
        return "Transporttest"
    if "selective" in s:
        return "Selektiver Test"
    return s or "–"


def _test_result(s: str) -> str:
    low = s.lower()
    if "without error" in low:
        return "Ohne Fehler abgeschlossen"
    if "progress" in low:
        return "Läuft"
    if "aborted" in low or "interrupted" in low:
        return "Abgebrochen"
    if "fail" in low or "error" in low:
        return "Fehlgeschlagen (" + s + ")"
    return s or "–"


def rate(h: Health, nvme=None):
    reasons_bad, reasons_caution, reasons_good = [], [], []
    if h.smart_passed is False:
        reasons_bad.append("Das Laufwerk meldet selbst einen S.M.A.R.T.-Fehler.")
    if isinstance(h.uncorrectable, int) and h.uncorrectable > 0:
        reasons_bad.append(f"{h.uncorrectable} unkorrigierbare Sektoren/Medienfehler.")
    if nvme and nvme.get("critical_warning"):
        reasons_bad.append("Die SSD meldet eine kritische Warnung.")
    for a in h.attributes:
        if a.failed in ("now", "FAILING_NOW") or (a.thresh and a.value <= a.thresh):
            reasons_bad.append(f"Attribut {a.id} ({a.name}) unter dem Grenzwert.")
    if isinstance(h.life_percent, int) and h.life_percent <= 5:
        reasons_bad.append(f"Restlebensdauer nur noch {h.life_percent} %.")
    if isinstance(h.pending, int) and h.pending > 0:
        reasons_caution.append(f"{h.pending} ausstehende (instabile) Sektoren.")
    if isinstance(h.reallocated, int) and h.reallocated >= 50:
        reasons_caution.append(f"{h.reallocated} ersetzte Sektoren.")
    if isinstance(h.life_percent, int) and 5 < h.life_percent <= 20:
        reasons_caution.append(f"Restlebensdauer {h.life_percent} %.")
    if nvme and isinstance(nvme.get("available_spare"), int) and isinstance(nvme.get("available_spare_threshold"), int) \
            and nvme["available_spare"] <= nvme["available_spare_threshold"]:
        reasons_caution.append("Reservespeicher der SSD aufgebraucht.")
    if isinstance(h.reallocated, int) and 0 < h.reallocated < 50:
        reasons_good.append(f"{h.reallocated} ersetzte Sektoren (unkritisch).")
    if isinstance(h.life_percent, int) and 20 < h.life_percent < 90:
        reasons_good.append(f"Restlebensdauer {h.life_percent} %.")
    if any(a.failed in ("past", "In_the_past") for a in h.attributes):
        reasons_good.append("Ein Attribut lag in der Vergangenheit unter dem Grenzwert.")
    if isinstance(h.temperature, int) and h.temperature >= 60:
        reasons_good.append(f"Hohe Temperatur ({h.temperature} °C).")

    if reasons_bad:
        h.rating, h.reasons = BAD, reasons_bad + reasons_caution
    elif reasons_caution:
        h.rating, h.reasons = CAUTION, reasons_caution + reasons_good
    elif reasons_good:
        h.rating, h.reasons = GOOD, reasons_good
    else:
        h.rating, h.reasons = VERY_GOOD, ["Keine Auffälligkeiten."]


def friendly_reason(msgs) -> str:
    text = " ".join(m for m in msgs if m).lower()
    if "smartctl nicht installiert" in text:
        return "Das Paket »smartmontools« ist nicht installiert (sudo apt install smartmontools)."
    if "usb bridge" in text or "unsupported usb" in text or "please specify device type" in text:
        return "Der USB-Adapter bzw. das USB-Gehäuse gibt die Gesundheitsdaten (S.M.A.R.T.) nicht weiter."
    if "permission" in text or "operation not permitted" in text:
        return "Für das Auslesen der Gesundheitsdaten fehlen die nötigen Rechte."
    if "smart support is: unavailable" in text or "smart support is: disabled" in text:
        return "Das Laufwerk unterstützt keine Gesundheitsdaten (S.M.A.R.T.) oder hat sie abgeschaltet."
    if "no such device" in text or "open device" in text:
        return "Das Laufwerk konnte nicht geöffnet werden – ist es noch angeschlossen?"
    return "Für dieses Laufwerk sind keine Gesundheitsdaten (S.M.A.R.T.) verfügbar."


def parse_text(text: str, reason_if_unavailable: str = "") -> Health:
    try:
        return parse(json.loads(text), reason_if_unavailable)
    except (ValueError, TypeError):
        h = Health()
        h.reason = reason_if_unavailable or "Die Diagnosedaten konnten nicht gelesen werden."
        return h


# --- Anzeige-Hilfen --------------------------------------------------------------------

def fmt_count(v) -> str:
    return "–" if v is None else f"{int(v):,}".replace(",", ".")


def fmt_hours(v) -> str:
    if v is None:
        return "–"
    days = int(v) // 24
    return f"{fmt_count(v)} Std. (ca. {fmt_count(days)} Tage)" if days >= 2 else f"{fmt_count(v)} Std."


def fmt_rpm(h: Health) -> str:
    if h.rpm:
        return f"{fmt_count(h.rpm)} U/min"
    if h.is_ssd:
        return "– (SSD, keine Drehzahl)"
    return "–"


def fmt_life(h: Health) -> str:
    if h.life_percent is None:
        return "– (nur bei SSDs)" if not h.is_ssd else "– (nicht gemeldet)"
    return f"{h.life_percent} %"


def fmt_temp(v) -> str:
    return "–" if v is None else f"{v} °C"


NVME_LABELS = [
    ("critical_warning", "Kritische Warnung"), ("available_spare", "Verfügbare Reserve (%)"),
    ("available_spare_threshold", "Reserve-Grenzwert (%)"), ("percentage_used", "Abnutzung (%)"),
    ("data_units_read", "Gelesene Dateneinheiten (je 512 000 Bytes)"),
    ("data_units_written", "Geschriebene Dateneinheiten (je 512 000 Bytes)"),
    ("host_reads", "Lesebefehle"), ("host_writes", "Schreibbefehle"), ("controller_busy_time", "Controller aktiv (Min.)"),
    ("power_cycles", "Einschaltvorgänge"), ("power_on_hours", "Betriebsstunden"),
    ("unsafe_shutdowns", "Unerwartete Abschaltungen"), ("media_errors", "Medien- und Datenfehler"),
    ("num_err_log_entries", "Einträge im Fehlerprotokoll"), ("warning_temp_time", "Zeit über Warntemperatur (Min.)"),
    ("critical_comp_time", "Zeit über kritischer Temperatur (Min.)"),
]


def fmt_bytes(b) -> str:
    b = float(max(0, b or 0))
    for unit in ("Bytes", "KB", "MB", "GB", "TB", "PB"):
        if b < 1000 or unit == "PB":
            if unit == "Bytes":
                return f"{int(b)} Bytes"
            return f"{b:.1f} {unit}".replace(".", ",") if b < 100 else f"{b:.0f} {unit}"
        b /= 1000
    return ""


def fmt_bytes_long(b) -> str:
    if b is None:
        return "–"
    return f"{fmt_bytes(b)} ({fmt_count(b)} Bytes)"


def key_values(h: Health) -> list:
    """Die wichtigsten Kennwerte (Reihenfolge wie im Bericht)."""
    nvme = h.protocol == "NVMe"
    na_nvme = "– (bei NVMe nicht vorhanden)"
    rows = [
        ("Gesamtzustand", h.rating),
        ("Restlebensdauer", fmt_life(h)),
        ("Temperatur", fmt_temp(h.temperature)),
        ("Stunden eingeschaltet", fmt_hours(h.power_on_hours)),
        ("Einschaltvorgänge (Power-on-Zyklen)", fmt_count(h.power_cycles)),
        ("Bytes geschrieben", fmt_bytes_long(h.bytes_written)),
        ("Bytes gelesen", fmt_bytes_long(h.bytes_read)),
        ("Ausstehende Sektoren", na_nvme if nvme and h.pending is None else fmt_count(h.pending)),
        ("Unkorrigierbare Sektoren" + (" (Medienfehler)" if nvme else ""), fmt_count(h.uncorrectable)),
        ("Ersetzte Sektoren", na_nvme if nvme and h.reallocated is None else fmt_count(h.reallocated)),
        ("CRC-Fehler (Kabel/Adapter)", na_nvme if nvme and h.crc_errors is None else fmt_count(h.crc_errors)),
        ("Drehzahl", fmt_rpm(h)),
        ("Schnittstelle", h.interface or "–"),
    ]
    return rows


def drive_details(h: Health) -> list:
    rows = [
        ("Modellreihe", h.family or "–"),
        ("Modellnummer", h.model or "–"),
        ("Firmware", h.firmware or "–"),
        ("Kapazität", fmt_bytes_long(h.capacity) if h.capacity else "–"),
        ("Bauart", "SSD" if h.is_ssd else ("Festplatte (HDD)" if h.is_ssd is False else "–")),
        ("Protokoll", h.protocol or "–"),
    ]
    if h.form_factor:
        rows.append(("Formfaktor", h.form_factor))
    if h.ata_version:
        rows.append(("ATA-Version", h.ata_version))
    if h.sata_version:
        rows.append(("SATA-Version", h.sata_version))
    if h.trim is not None:
        rows.append(("TRIM", "unterstützt" if h.trim else "nicht unterstützt"))
    if h.security:
        rows.append(("ATA-Sicherheit", security_label(h)))
    rows.append(("S.M.A.R.T.-Selbstbewertung", {True: "Bestanden", False: "NICHT bestanden"}.get(h.smart_passed, "–")))
    return rows


def security_label(h: Health) -> str:
    s = h.security.upper()
    parts = []
    parts.append("aktiviert" if "ENABLED" in s and "DISABLED" not in s else "nicht aktiviert")
    if h.security_frozen:
        parts.append("eingefroren (frozen)")
    return ", ".join(parts)
