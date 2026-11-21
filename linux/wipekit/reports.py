"""WipeKit-Berichte: Gesundheitsbericht, Löschprotokoll (kurz) und Löschprotokoll (ausführlich).

Jeder Bericht wird als PDF und als Text gespeichert:
  ~/Dokumente/WipeKit-Protokolle/PDF/…pdf
  ~/Dokumente/WipeKit-Protokolle/Text/…txt
Dateiname: JJJJ-MM-TT_HH-MM_HERSTELLER-MODELL-GRÖSSEGB-SERIENNUMMER_Typ
"""

import os
import re
import time
import unicodedata
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

import health as hl
import pdfdoc

KIND_HEALTH = ("Gesundheitsbericht", "Gesundheitsbericht")
KIND_WIPE = ("Löschprotokoll", "Loeschprotokoll")
KIND_WIPE_FULL = ("Löschprotokoll (ausführlich)", "Loeschprotokoll-ausfuehrlich")
KIND_CHECK = ("Prüfprotokoll", "Pruefprotokoll")

GREEN = (0.12, 0.56, 0.24)
ORANGE = (0.86, 0.55, 0.00)
RED = (0.85, 0.19, 0.15)


# ---------------------------------------------------------------------------
# Berichtsmodell
# ---------------------------------------------------------------------------

@dataclass
class Table:
    headers: list
    rows: list
    widths: list            # Anteile an der Breite (Summe 1)
    align: list             # "l" oder "r"


@dataclass
class Section:
    title: str
    blocks: list = field(default_factory=list)   # ("kv", rows) | ("table", Table) | ("text", str) | ("bullets", list)


@dataclass
class Identity:
    vendor: str = ""
    model: str = ""
    size: int = 0
    serial: str = ""


@dataclass
class Report:
    kind: tuple
    created: float
    identity: Identity
    rating: str
    rating_note: str = ""
    banner: tuple = None    # (Text, Farbe)
    sections: list = field(default_factory=list)
    footer: str = ""


@dataclass
class WipeInfo:
    """Alles, was das Löschprotokoll über den Löschvorgang wissen muss."""
    mode_title: str
    method: str
    standard: str
    state: str                 # done | cancelled | failed
    started: float
    ended: float
    bytes_written: int = 0
    message: str = ""
    verification: str = ""
    trim: str = ""
    device: str = ""           # z. B. /dev/sdb, disk4, PhysicalDrive2
    bus: str = ""
    type_label: str = ""
    volumes: list = field(default_factory=list)
    exact_size: int = 0
    hidden_area: str = ""
    host: str = ""
    platform: str = ""         # z. B. "Linux", "macOS 26.1"
    app_version: str = ""
    read_only: bool = False    # »Nur prüfen« / Oberflächentest
    bad_blocks: int = None
    non_zero: int = None
    purge_method: str = ""


def identity_from(h, model="", size=0, serial="") -> Identity:
    """Hersteller/Modell/Größe/Seriennummer – bevorzugt aus den S.M.A.R.T.-Daten, sonst vom Betriebssystem."""
    if h is not None and h.model:
        return Identity(h.vendor or hl.detect_vendor(model) or "Unbekannt",
                        h.model_number or h.model, h.capacity or size, h.serial or serial)
    m = (model or "").strip()
    vendor = hl.detect_vendor(m)
    return Identity(vendor or "Unbekannt", hl.model_number(m) or m or "Unbekannt", size, serial or "")


def size_gb(size: int) -> str:
    return f"{int(round((size or 0) / 1e9))}GB"


def _slug(s: str) -> str:
    s = (s or "").replace("ß", "SS").replace("ä", "AE").replace("ö", "OE").replace("ü", "UE") \
        .replace("Ä", "AE").replace("Ö", "OE").replace("Ü", "UE")
    s = unicodedata.normalize("NFKD", s).encode("ascii", "ignore").decode()
    s = re.sub(r"[^A-Za-z0-9]+", "-", s).strip("-").upper()
    return re.sub(r"-{2,}", "-", s)


def base_name(ident: Identity, when: float) -> str:
    parts = [_slug(ident.vendor if ident.vendor != "Unbekannt" else ""), _slug(ident.model),
             size_gb(ident.size) if ident.size else "", _slug(ident.serial)]
    name = "-".join(p for p in parts if p) or "LAUFWERK"
    return time.strftime("%Y-%m-%d_%H-%M", time.localtime(when)) + "_" + name[:150]


def file_name(report: Report, ext: str) -> str:
    return f"{base_name(report.identity, report.created)}_{report.kind[1]}.{ext}"


def reports_folder() -> Path:
    docs = None
    try:
        import subprocess
        out = subprocess.run(["xdg-user-dir", "DOCUMENTS"], capture_output=True, text=True, timeout=3)
        if out.returncode == 0 and out.stdout.strip() and out.stdout.strip() != str(Path.home()):
            docs = out.stdout.strip()
    except (OSError, ValueError, Exception):
        docs = None
    return Path(docs or (Path.home() / "Documents")) / "WipeKit-Protokolle"


def save(report: Report, folder: Path = None) -> tuple:
    """Speichert PDF und Text, gibt (pdf_pfad, txt_pfad) zurück."""
    folder = folder or reports_folder()
    pdf_dir, txt_dir = folder / "PDF", folder / "Text"
    pdf_dir.mkdir(parents=True, exist_ok=True)
    txt_dir.mkdir(parents=True, exist_ok=True)
    pdf_path = _unique(pdf_dir / file_name(report, "pdf"))
    txt_path = _unique(txt_dir / file_name(report, "txt"))
    pdf_path.write_bytes(render_pdf(report))
    txt_path.write_text(render_text(report), encoding="utf-8")
    return pdf_path, txt_path


def _unique(p: Path) -> Path:
    if not p.exists():
        return p
    for i in range(2, 100):
        q = p.with_name(f"{p.stem}_{i}{p.suffix}")
        if not q.exists():
            return q
    return p


# ---------------------------------------------------------------------------
# Inhalte
# ---------------------------------------------------------------------------

def fmt_dt(ts) -> str:
    return datetime.fromtimestamp(ts).strftime("%d.%m.%Y, %H:%M Uhr") if ts else "–"


def fmt_dt_sec(ts) -> str:
    return datetime.fromtimestamp(ts).strftime("%d.%m.%Y, %H:%M:%S Uhr") if ts else "–"


def fmt_duration(t) -> str:
    s = int(round(max(0, t or 0)))
    h, m, sec = s // 3600, (s % 3600) // 60, s % 60
    if h:
        return f"{h} Std. {m} Min."
    if m:
        return f"{m} Min. {sec} Sek."
    return f"{sec} Sek."


UNAVAILABLE_HELP = ("Hinweis: Viele USB-Gehäuse und -Adapter geben die Gesundheitsdaten nicht weiter. "
                    "Abhilfe: das Laufwerk direkt (SATA/NVMe intern), über Thunderbolt/USB4 oder über einen "
                    "Adapter mit ASMedia-, JMicron- oder Realtek-Chip anschließen.")


def _health_sections(h, title_suffix="", with_tables=True) -> list:
    if h is None or not h.available:
        reason = (h.reason if h is not None and h.reason else "Keine S.M.A.R.T.-Daten verfügbar.")
        return [Section("Gesundheit" + title_suffix, [("text", reason), ("text", UNAVAILABLE_HELP)])]
    secs = [Section("Kennwerte" + title_suffix, [("kv", hl.key_values(h))]),
            Section("Bewertung" + title_suffix, [("bullets", h.reasons)]),
            Section("Laufwerk" + title_suffix, [("kv", hl.drive_details(h))])]
    if not with_tables:
        return secs
    if h.attributes:
        rows = [[f"{a.id}", a.name, str(a.value), str(a.worst), str(a.thresh), a.raw_string, a.status]
                for a in h.attributes]
        secs.append(Section("S.M.A.R.T.-Attribute" + title_suffix, [("table", Table(
            ["ID", "Attribut", "Wert", "Schlechtester", "Grenzwert", "Rohwert", "Status"], rows,
            [0.06, 0.36, 0.08, 0.13, 0.11, 0.16, 0.10], ["r", "l", "r", "r", "r", "r", "l"]))]))
    if h.nvme:
        rows = []
        for key, label in hl.NVME_LABELS:
            if key in h.nvme:
                v = h.nvme[key]
                rows.append((label, hl.fmt_count(v) if isinstance(v, int) else str(v)))
        temps = h.nvme.get("temperature_sensors") or []
        for i, t in enumerate(temps, 1):
            rows.append((f"Temperatursensor {i}", hl.fmt_temp(t)))
        secs.append(Section("NVMe-Gesundheitswerte" + title_suffix, [("kv", rows)]))
    if h.self_tests:
        rows = [[t.kind, t.result, hl.fmt_count(t.hours) if t.hours is not None else "–"] for t in h.self_tests]
        secs.append(Section("Selbsttest-Protokoll des Laufwerks" + title_suffix, [("table", Table(
            ["Test", "Ergebnis", "Bei Betriebsstunde"], rows, [0.25, 0.53, 0.22], ["l", "l", "r"]))]))
    return secs


def health_report(h, ident: Identity, app_version="", platform="", host="", created=None) -> Report:
    r = Report(KIND_HEALTH, created or time.time(), ident,
               h.rating if h is not None else hl.UNKNOWN,
               (h.reasons[0] if h is not None and h.available and h.reasons else
                (h.reason if h is not None else "")))
    r.sections = _health_sections(h)
    r.sections.append(Section("Bericht", [("kv", [
        ("Erstellt", fmt_dt_sec(r.created)), ("Rechner", host or "–"),
        ("System", platform or "–"), ("Programm", f"WipeKit {app_version}".strip())])]))
    r.footer = f"WipeKit {app_version}".strip()
    return r


def _wipe_result(w: WipeInfo):
    if w.read_only:
        if w.state == "done":
            if w.bad_blocks:
                return "Unlesbare Bereiche gefunden", RED
            if w.non_zero == 0:
                return "Laufwerk ist leer (nur Nullen)", GREEN
            return "Laufwerk enthält Daten", ORANGE
        if w.state == "cancelled":
            return "Prüfung abgebrochen", ORANGE
        return "Prüfung fehlgeschlagen", RED
    if w.state == "done":
        return "Erfolgreich gelöscht", GREEN
    if w.state == "cancelled":
        return "Abgebrochen – das Laufwerk wurde nur teilweise überschrieben", ORANGE
    return "Fehlgeschlagen", RED


def wipe_report(w: WipeInfo, ident: Identity, before=None, after=None, detailed=False, created=None) -> Report:
    h = after if (after is not None and after.available) else before
    result, color = _wipe_result(w)
    kind = KIND_CHECK if w.read_only else (KIND_WIPE_FULL if detailed else KIND_WIPE)
    r = Report(kind, created or w.ended or time.time(), ident,
               h.rating if h is not None else hl.UNKNOWN,
               (h.reasons[0] if h is not None and h.available and h.reasons else
                (h.reason if h is not None else "")),
               banner=(result, color))
    duration = max(0.0, (w.ended or 0) - (w.started or 0))
    avg = w.bytes_written / duration if duration > 0 else 0
    rows = [("Ergebnis", result), ("Methode", w.mode_title), ("Ablauf", w.method), ("Einordnung", w.standard)]
    if w.purge_method:
        rows.append(("Firmware-Befehl", w.purge_method))
    rows += [("Beginn", fmt_dt_sec(w.started)), ("Ende", fmt_dt_sec(w.ended)), ("Dauer", fmt_duration(duration)),
             ("Ø Geschwindigkeit", hl.fmt_bytes(avg) + "/s" if avg and not w.purge_method else "–")]
    if w.read_only:
        if w.non_zero is not None:
            rows.append(("Inhalt", "leer – alle Sektoren enthalten Nullen" if w.non_zero == 0 else
                         f"{hl.fmt_bytes_long(w.non_zero)} enthalten Daten"))
        if w.bad_blocks is not None:
            rows.append(("Unlesbare Bereiche", "keine" if not w.bad_blocks else f"{w.bad_blocks} (je 64 KiB)"))
    else:
        rows.append(("Verifikation", w.verification or "–"))
    if w.trim:
        rows.append(("TRIM", w.trim))
    if w.hidden_area:
        rows.append(("Versteckte Bereiche", w.hidden_area))
    if w.message:
        rows.append(("Meldung", w.message))
    r.sections.append(Section("Prüfung" if w.read_only else "Löschvorgang", [("kv", rows)]))
    r.sections.append(Section("Laufwerk", [("kv", [
        ("Kennung", w.device or "–"), ("Anschluss", w.bus or "–"), ("Typ", w.type_label or "–"),
        ("Kapazität", hl.fmt_bytes_long(w.exact_size or ident.size)),
        ("Frühere Volumes", ", ".join(w.volumes) if w.volumes else "–")])]))
    if detailed or w.read_only:
        if not w.read_only and before is not None and after is not None and before.available and after.available:
            kb, ka = hl.key_values(before), hl.key_values(after)
            rows = [[label, vb, va] for (label, vb), (_, va) in zip(kb, ka)]
            r.sections.append(Section("Kennwerte vor und nach dem Löschen", [("table", Table(
                ["Wert", "Vor dem Löschen", "Nach dem Löschen"], rows, [0.36, 0.32, 0.32], ["l", "l", "l"]))]))
            r.sections.append(Section("Bewertung", [("bullets", after.reasons)]))
            r.sections.append(Section("Laufwerksdaten", [("kv", hl.drive_details(after))]))
            r.sections += _health_sections(after, " (nach dem Löschen)", True)[3:]
        else:
            secs = _health_sections(h, "", True)
            for s in secs:
                if s.title == "Laufwerk":
                    s.title = "Laufwerksdaten"
            r.sections += secs
    r.sections.append(Section("Protokoll", [("kv", [
        ("Erstellt", fmt_dt_sec(r.created)), ("Rechner", w.host or "–"), ("System", w.platform or "–"),
        ("Programm", f"WipeKit {w.app_version}".strip())])]))
    r.footer = f"WipeKit {w.app_version}".strip()
    return r


# ---------------------------------------------------------------------------
# Text
# ---------------------------------------------------------------------------

TXT_W = 78


def _wrap(text: str, width: int) -> list:
    words, lines, cur = str(text).split(), [], ""
    for w in words:
        while len(w) > width:
            if cur:
                lines.append(cur)
                cur = ""
            lines.append(w[:width])
            w = w[width:]
        if not cur:
            cur = w
        elif len(cur) + 1 + len(w) <= width:
            cur += " " + w
        else:
            lines.append(cur)
            cur = w
    if cur or not lines:
        lines.append(cur)
    return lines


def _kv_text(rows, label_w=None) -> list:
    label_w = label_w or min(38, max(len(k) for k, _ in rows) + 2)
    out = []
    for k, v in rows:
        vl = _wrap(v, TXT_W - label_w)
        out.append(f"{(k + ':').ljust(label_w)}{vl[0]}")
        out += [" " * label_w + x for x in vl[1:]]
    return out


def render_text(r: Report) -> str:
    L = ["WIPEKIT-BERICHT", r.kind[0], f"Erstellt am {fmt_dt(r.created)}", "=" * TXT_W, ""]
    if r.banner:
        L += [f"ERGEBNIS: {r.banner[0].upper()}", ""]
    i = r.identity
    L += ["ÜBERSICHT", "-" * TXT_W]
    L += _kv_text([("Hersteller", i.vendor or "Unbekannt"), ("Modell", i.model or "Unbekannt"),
                   ("Größe", hl.fmt_bytes(i.size) if i.size else "–"), ("Seriennummer", i.serial or "–"),
                   ("Gesamtzustand", r.rating.upper() + (f" – {r.rating_note}" if r.rating_note else ""))], 18)
    L.append("")
    for s in r.sections:
        L += [s.title.upper(), "-" * TXT_W]
        for b in s.blocks:
            if b[0] == "kv":
                L += _kv_text(b[1])
            elif b[0] == "text":
                L += _wrap(b[1], TXT_W)
            elif b[0] == "bullets":
                for item in b[1]:
                    wl = _wrap(item, TXT_W - 2)
                    L += ["- " + wl[0]] + ["  " + x for x in wl[1:]]
            elif b[0] == "table":
                t = b[1]
                widths = [min(60, max(len(str(x[ci])) for x in [t.headers] + t.rows)) for ci in range(len(t.headers))]

                def fmt_row(cells):
                    out = []
                    for c, w, a in zip(cells, widths, t.align):
                        c = str(c)
                        c = c if len(c) <= w else c[:w - 1] + "…"
                        out.append(c.rjust(w) if a == "r" else c.ljust(w))
                    return "  ".join(out).rstrip()
                L.append(fmt_row(t.headers))
                L.append("  ".join("-" * w for w in widths))
                L += [fmt_row(row) for row in t.rows]
            L.append("")
    L += ["-" * TXT_W, f"Erstellt mit {r.footer or 'WipeKit'}"]
    return "\n".join(L) + "\n"


# ---------------------------------------------------------------------------
# PDF
# ---------------------------------------------------------------------------

INK = (0.11, 0.11, 0.12)
SECONDARY = (0.43, 0.43, 0.45)
SEPARATOR = (0.84, 0.84, 0.86)
CARD = (0.96, 0.96, 0.97)
WHITE = (1, 1, 1)
M = 48.0           # Seitenrand
FOOT = 40.0


def _tint(c, a=0.12):
    return tuple(1 - a * (1 - x) for x in c)


def _wrap_w(text, size, bold, width) -> list:
    words, lines, cur = str(text).split(" "), [], ""
    for w in words:
        cand = w if not cur else cur + " " + w
        if pdfdoc.text_width(cand, size, bold) <= width:
            cur = cand
            continue
        if cur:
            lines.append(cur)
        cur = w
        while pdfdoc.text_width(cur, size, bold) > width and len(cur) > 1:
            k = len(cur)
            while k > 1 and pdfdoc.text_width(cur[:k], size, bold) > width:
                k -= 1
            lines.append(cur[:k])
            cur = cur[k:]
    lines.append(cur)
    return lines


class _Layout:
    def __init__(self, logo: bytes):
        self.c = pdfdoc.Canvas()
        self.W, self.H = self.c.w, self.c.h
        self.logo = logo
        self.y = 0.0
        self.page_no = 0
        self.new_page()

    @property
    def bottom(self):
        return self.H - FOOT - 14

    def new_page(self):
        self.c.new_page()
        self.page_no += 1
        self.y = M

    def ensure(self, h):
        if self.y + h > self.bottom:
            self.new_page()
            return True
        return False


def render_pdf(r: Report, logo: bytes = None) -> bytes:
    if logo is None:
        logo = _load_logo()
    L = _Layout(logo)
    c, W = L.c, L.W
    cw = W - 2 * M

    # Kopf
    if logo:
        c.jpeg(logo, M, M - 4, 46, 46)
    tx = M + (58 if logo else 0)
    c.text(tx, M + 17, "WipeKit-Bericht", 21, True, INK)
    c.text(tx, M + 36, r.kind[0], 12, False, SECONDARY)
    c.text_right(W - M, M + 14, "Erstellt am", 8.5, False, SECONDARY)
    c.text_right(W - M, M + 28, fmt_dt(r.created), 10, True, INK)
    L.y = M + 62

    # Ergebnis-Banner
    if r.banner:
        text, color = r.banner
        c.rect(M, L.y, cw, 34, fill=_tint(color, 0.13), radius=9)
        c.rect(M + 14, L.y + 12, 10, 10, fill=color, radius=5)
        c.text(M + 32, L.y + 21.5, text, 12, True, color)
        L.y += 46

    # Übersicht
    i = r.identity
    pad = 16
    col = (cw - 2 * pad) / 2
    cells = [("HERSTELLER", i.vendor or "Unbekannt"), ("MODELL", i.model or "Unbekannt"),
             ("GRÖSSE", hl.fmt_bytes(i.size) if i.size else "–"), ("SERIENNUMMER", i.serial or "–")]
    wrapped = [_wrap_w(v, 12.5, True, col - 10)[:2] for _, v in cells]
    row_h = [8 + 13 + 15 * max(len(wrapped[0]), len(wrapped[1])),
             8 + 13 + 15 * max(len(wrapped[2]), len(wrapped[3]))]
    note_lines = _wrap_w(r.rating_note, 9.5, False, cw - 2 * pad - 120)[:3] if r.rating_note else []
    rating_h = 14 + max(22, 13 * len(note_lines) + 4)
    card_h = pad + row_h[0] + row_h[1] + 6 + rating_h + pad - 6
    c.rect(M, L.y, cw, card_h, fill=CARD, radius=12)
    y = L.y + pad
    for rr in range(2):
        for cc in range(2):
            k = rr * 2 + cc
            x = M + pad + cc * col
            c.text(x, y + 8, cells[k][0], 7.5, True, SECONDARY)
            for li, line in enumerate(wrapped[k]):
                c.text(x, y + 24 + li * 15, line, 12.5, True, INK)
        y += row_h[rr]
    c.line(M + pad, y, M + cw - pad, y, SEPARATOR, 0.6)
    y += 12
    c.text(M + pad, y + 8, "GESAMTZUSTAND", 7.5, True, SECONDARY)
    color = hl.RATING_COLORS.get(r.rating, hl.RATING_COLORS[hl.UNKNOWN])
    pw = pdfdoc.text_width(r.rating, 11, True) + 22
    c.rect(M + pad, y + 14, pw, 20, fill=color, radius=10)
    c.text(M + pad + 11, y + 28, r.rating, 11, True, WHITE)
    for li, line in enumerate(note_lines):
        c.text(M + pad + 120, y + 15 + li * 13 if len(note_lines) > 1 else y + 28, line, 9.5, False, SECONDARY)
    L.y += card_h + 22

    for s in r.sections:
        _section(L, s, cw)

    # Fußzeile
    total = len(c.pages)
    for n, page in enumerate(c.pages, 1):
        c.cur = page
        c.line(M, L.H - FOOT, W - M, L.H - FOOT, SEPARATOR, 0.5)
        c.text(M, L.H - FOOT + 14, f"{r.footer or 'WipeKit'} · {r.kind[0]}", 8, False, SECONDARY)
        c.text_right(W - M, L.H - FOOT + 14, f"Seite {n} von {total}", 8, False, SECONDARY)
    return c.build("WipeKit-Bericht – " + r.kind[0], f"{i.vendor} {i.model} {i.serial}".strip())


def _section_title(L, title):
    L.ensure(18 + 40)
    L.c.text(M, L.y + 11, title, 12.5, True, INK)
    L.y += 20


def _section(L, s: Section, cw):
    _section_title(L, s.title)
    for b in s.blocks:
        if b[0] == "kv":
            _kv(L, b[1], cw)
        elif b[0] == "table":
            _table(L, b[1], cw)
        elif b[0] == "text":
            for line in _wrap_w(b[1], 9.5, False, cw):
                L.ensure(13)
                L.c.text(M, L.y + 10, line, 9.5, False, SECONDARY)
                L.y += 13
            L.y += 4
        elif b[0] == "bullets":
            for item in b[1]:
                lines = _wrap_w(item, 9.5, False, cw - 14)
                L.ensure(13 * len(lines))
                L.c.rect(M + 2, L.y + 5.5, 4, 4, fill=SECONDARY, radius=2)
                for line in lines:
                    L.c.text(M + 14, L.y + 10, line, 9.5, False, INK)
                    L.y += 13
            L.y += 4
    L.y += 14


class _Card:
    """Abgerundete Karte, die über Seitenumbrüche hinweg fortgesetzt wird."""

    def __init__(self, L, cw):
        self.L, self.cw = L, cw
        self.start = L.y
        self.ops = []
        self.count = 0

    def flush(self):
        L, c = self.L, self.L.c
        if self.count:
            ops = c.cur
            c.cur = bytearray()
            c.rect(M, self.start, self.cw, L.y - self.start, fill=WHITE, stroke=SEPARATOR, radius=9, line_width=0.7)
            bg = bytes(c.cur)
            c.cur = ops
            c.cur[self.mark:self.mark] = bg
        self.count = 0

    def begin(self):
        self.start = self.L.y
        self.mark = len(self.L.c.cur)
        self.count = 0

    def row(self, h, draw, header_again=None):
        L = self.L
        if L.y + h > L.bottom:
            self.flush()
            L.new_page()
            self.begin()
            if header_again:
                header_again()
        if self.count and not getattr(self, "_skip_sep", False):
            L.c.line(M + 12, L.y, M + self.cw - 12, L.y, SEPARATOR, 0.5)
        self._skip_sep = False
        draw(L.y)
        L.y += h
        self.count += 1


def _kv(L, rows, cw):
    label_w = 205
    card = _Card(L, cw)
    card.begin()
    for k, v in rows:
        llines = _wrap_w(k, 9.5, False, label_w - 20)
        vlines = _wrap_w(v, 9.5, False, cw - label_w - 14)
        n = max(len(llines), len(vlines))
        h = 10 + 13 * n

        def draw(y, llines=llines, vlines=vlines, k=k, v=v):
            for li, line in enumerate(llines):
                L.c.text(M + 12, y + 15 + li * 13, line, 9.5, False, SECONDARY)
            if k == "Gesamtzustand" and v in hl.RATING_COLORS:
                col = hl.RATING_COLORS[v]
                L.c.rect(M + label_w, y + 7.5, 7, 7, fill=col, radius=3.5)
                L.c.text(M + label_w + 12, y + 15, v, 9.5, True, col)
                return
            bold = k in ("Ergebnis",)
            for li, line in enumerate(vlines):
                L.c.text(M + label_w, y + 15 + li * 13, line, 9.5, bold, INK)
        card.row(h, draw)
    card.flush()
    L.y += 2


def _table(L, t: Table, cw):
    inner = cw - 24
    xs, acc = [], M + 12
    for w in t.widths:
        xs.append(acc)
        acc += w * inner
    widths = [w * inner for w in t.widths]
    size = 8.5
    card = _Card(L, cw)

    def header():
        def draw(y):
            for x, w, a, text in zip(xs, widths, t.align, t.headers):
                text = _fit(text, 7.5, True, w - 6)
                if a == "r":
                    L.c.text_right(x + w - 6, y + 14, text, 7.5, True, SECONDARY)
                else:
                    L.c.text(x, y + 14, text, 7.5, True, SECONDARY)
        draw(L.y)
        L.y += 21
        card.count += 1

    L.ensure(21 + 22)
    card.begin()
    header()
    for row in t.rows:
        cells = [_wrap_w(str(v), size, False, w - 6)[:3] for v, w in zip(row, widths)]
        n = max(len(x) for x in cells)
        h = 9 + 12 * n

        def draw(y, cells=cells, row=row):
            for x, w, a, lines, raw in zip(xs, widths, t.align, cells, row):
                color = INK
                bold = False
                if raw in ("Warnung",):
                    color, bold = ORANGE, True
                elif raw in ("Fehler",) or str(raw).startswith("Fehlgeschlagen"):
                    color, bold = RED, True
                for li, line in enumerate(lines):
                    if a == "r":
                        L.c.text_right(x + w - 6, y + 14 + li * 12, line, size, bold, color)
                    else:
                        L.c.text(x, y + 14 + li * 12, line, size, bold, color)
        card.row(h, draw, header_again=header)
    card.flush()
    L.y += 2


def _fit(text, size, bold, width):
    if pdfdoc.text_width(text, size, bold) <= width:
        return text
    while text and pdfdoc.text_width(text + "…", size, bold) > width:
        text = text[:-1]
    return text + "…"


def _load_logo():
    here = Path(__file__).resolve().parent
    for p in (here / "report-logo.jpg", here.parent.parent / "assets" / "report-logo.jpg",
              Path("/usr/share/wipekit/report-logo.jpg")):
        try:
            return p.read_bytes()
        except OSError:
            continue
    return None
