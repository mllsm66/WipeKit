"""Minimaler PDF-Schreiber für die WipeKit-Berichte (ohne Fremdbibliotheken).

Verwendet die PDF-Standardschriften Helvetica/Helvetica-Bold (nicht eingebettet, WinAnsi-Kodierung).
Die Zeichenbreiten stammen aus den Adobe Core-14-AFM-Dateien:
Copyright (c) 1985, 1987, 1989, 1990, 1997 Adobe Systems Incorporated. All Rights Reserved.
(siehe THIRD-PARTY-NOTICES.md)
"""

import time

A4 = (595.28, 841.89)

# Breiten der Zeichen 32…255 (Windows-1252) in 1/1000 Schriftgröße
HELVETICA = [
    278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556,
    556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556, 1015, 667, 667, 722, 722, 667, 611, 778,
    722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278,
    278, 278, 469, 556, 333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
    556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584, 761, 556, 0, 222, 556,
    333, 1000, 556, 556, 333, 1000, 667, 333, 1000, 0, 611, 0, 0, 222, 222, 333, 333, 350, 556, 1000,
    333, 1000, 500, 333, 944, 0, 500, 667, 278, 333, 556, 556, 556, 556, 260, 556, 333, 737, 370, 556,
    584, 333, 737, 333, 400, 584, 333, 333, 333, 556, 537, 278, 333, 333, 365, 556, 834, 834, 834, 611,
    667, 667, 667, 667, 667, 667, 1000, 722, 667, 667, 667, 667, 278, 278, 278, 278, 722, 722, 778, 778,
    778, 778, 778, 584, 778, 722, 722, 722, 722, 667, 667, 611, 556, 556, 556, 556, 556, 556, 889, 500,
    556, 556, 556, 556, 278, 278, 278, 278, 556, 556, 556, 556, 556, 556, 556, 584, 611, 556, 556, 556,
    556, 500, 556, 500
]
HELVETICA_BOLD = [
    278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556,
    556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611, 975, 722, 722, 722, 722, 667, 611, 778,
    722, 278, 556, 722, 611, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333,
    278, 333, 584, 556, 333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
    611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584, 761, 556, 0, 278, 556,
    500, 1000, 556, 556, 333, 1000, 667, 333, 1000, 0, 611, 0, 0, 278, 278, 500, 500, 350, 556, 1000,
    333, 1000, 556, 333, 944, 0, 500, 667, 278, 333, 556, 556, 556, 556, 280, 556, 333, 737, 370, 556,
    584, 333, 737, 333, 400, 584, 333, 333, 333, 611, 556, 278, 333, 333, 365, 556, 834, 834, 834, 611,
    722, 722, 722, 722, 722, 722, 1000, 722, 667, 667, 667, 667, 278, 278, 278, 278, 722, 722, 778, 778,
    778, 778, 778, 584, 778, 722, 722, 722, 722, 667, 667, 611, 556, 556, 556, 556, 556, 556, 889, 556,
    556, 556, 556, 556, 278, 278, 278, 278, 611, 611, 611, 611, 611, 611, 611, 584, 611, 611, 611, 611,
    611, 556, 611, 556
]

REPLACE = {"≈": "ca.", "→": "->", "≥": ">=", "≤": "<=", "✓": "OK", "✗": "X", "\u2009": " ", "\u202f": " ",
           "\u00a0": " ", "−": "-", "×": "x"}


def to_ansi(s: str) -> bytes:
    out = bytearray()
    for ch in str(s):
        ch = REPLACE.get(ch, ch)
        for c in ch:
            try:
                b = c.encode("cp1252")
            except UnicodeEncodeError:
                b = b"?"
            out += b
    return bytes(out)


def text_width(s: str, size: float, bold: bool = False) -> float:
    table = HELVETICA_BOLD if bold else HELVETICA
    w = 0
    for c in to_ansi(s):
        w += table[c - 32] if 32 <= c <= 255 else 0
    return w * size / 1000.0


def _esc(data: bytes) -> bytes:
    return data.replace(b"\\", b"\\\\").replace(b"(", b"\\(").replace(b")", b"\\)")


def _num(v: float) -> str:
    s = f"{v:.2f}".rstrip("0").rstrip(".")
    return "0" if s in ("-0", "") else s


def jpeg_size(data: bytes):
    i = 2
    while i < len(data) - 9:
        if data[i] != 0xFF:
            i += 1
            continue
        marker = data[i + 1]
        length = (data[i + 2] << 8) | data[i + 3]
        if marker in (0xC0, 0xC1, 0xC2):
            return (data[i + 7] << 8) | data[i + 8], (data[i + 5] << 8) | data[i + 6]
        i += 2 + length
    return 0, 0


class Canvas:
    """Zeichenfläche mit Ursprung oben links (y wächst nach unten)."""

    def __init__(self, page_size=A4):
        self.w, self.h = page_size
        self.pages = []
        self.cur = None
        self.image = None

    def new_page(self):
        self.cur = bytearray()
        self.pages.append(self.cur)

    def _op(self, s: str):
        self.cur += s.encode("latin-1") + b"\n"

    @staticmethod
    def _rgb(c):
        return " ".join(_num(x) for x in c)

    def text(self, x, y, s, size=10, bold=False, color=(0, 0, 0)):
        self._op(f"BT /{'F2' if bold else 'F1'} {_num(size)} Tf {self._rgb(color)} rg "
                 f"{_num(x)} {_num(self.h - y)} Td")
        self.cur += b"(" + _esc(to_ansi(s)) + b") Tj ET\n"

    def text_right(self, x_right, y, s, size=10, bold=False, color=(0, 0, 0)):
        self.text(x_right - text_width(s, size, bold), y, s, size, bold, color)

    def rect(self, x, y, w, h, fill=None, stroke=None, radius=0.0, line_width=0.5):
        x0, y0 = x, self.h - y - h
        r = min(radius, w / 2, h / 2)
        if r > 0:
            k = 0.5523 * r
            p = (f"{_num(x0 + r)} {_num(y0)} m {_num(x0 + w - r)} {_num(y0)} l "
                 f"{_num(x0 + w - r + k)} {_num(y0)} {_num(x0 + w)} {_num(y0 + r - k)} {_num(x0 + w)} {_num(y0 + r)} c "
                 f"{_num(x0 + w)} {_num(y0 + h - r)} l "
                 f"{_num(x0 + w)} {_num(y0 + h - r + k)} {_num(x0 + w - r + k)} {_num(y0 + h)} {_num(x0 + w - r)} {_num(y0 + h)} c "
                 f"{_num(x0 + r)} {_num(y0 + h)} l "
                 f"{_num(x0 + r - k)} {_num(y0 + h)} {_num(x0)} {_num(y0 + h - r + k)} {_num(x0)} {_num(y0 + h - r)} c "
                 f"{_num(x0)} {_num(y0 + r)} l "
                 f"{_num(x0)} {_num(y0 + r - k)} {_num(x0 + r - k)} {_num(y0)} {_num(x0 + r)} {_num(y0)} c h")
        else:
            p = f"{_num(x0)} {_num(y0)} {_num(w)} {_num(h)} re"
        ops = []
        if fill is not None:
            ops.append(f"{self._rgb(fill)} rg")
        if stroke is not None:
            ops.append(f"{self._rgb(stroke)} RG {_num(line_width)} w")
        paint = "B" if fill is not None and stroke is not None else ("f" if fill is not None else "S")
        self._op(" ".join(ops) + f" {p} {paint}")

    def line(self, x1, y1, x2, y2, color=(0.8, 0.8, 0.8), width=0.5):
        self._op(f"{self._rgb(color)} RG {_num(width)} w {_num(x1)} {_num(self.h - y1)} m "
                 f"{_num(x2)} {_num(self.h - y2)} l S")

    def jpeg(self, data: bytes, x, y, w, h):
        self.image = data
        self._op(f"q {_num(w)} 0 0 {_num(h)} {_num(x)} {_num(self.h - y - h)} cm /Im1 Do Q")

    def build(self, title: str = "WipeKit-Bericht", subject: str = "") -> bytes:
        objs = {}
        n_pages = len(self.pages)
        page_ids = [7 + 2 * i for i in range(n_pages)]
        objs[1] = b"<< /Type /Catalog /Pages 2 0 R /Lang (de-DE) /ViewerPreferences << /DisplayDocTitle true >> >>"
        objs[2] = ("<< /Type /Pages /Count %d /Kids [%s] >>" % (
            n_pages, " ".join(f"{i} 0 R" for i in page_ids))).encode()
        objs[3] = b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"
        objs[4] = b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"
        res = b"<< /Font << /F1 3 0 R /F2 4 0 R >>"
        if self.image:
            iw, ih = jpeg_size(self.image)
            objs[5] = (f"<< /Type /XObject /Subtype /Image /Width {iw} /Height {ih} /ColorSpace /DeviceRGB "
                       f"/BitsPerComponent 8 /Filter /DCTDecode /Length {len(self.image)} >>\nstream\n").encode() \
                + self.image + b"\nendstream"
            res += b" /XObject << /Im1 5 0 R >>"
        res += b" >>"
        stamp = time.strftime("D:%Y%m%d%H%M%S")

        def u16(s):
            return "<FEFF" + s.encode("utf-16-be").hex().upper() + ">"
        objs[6] = (f"<< /Title {u16(title)} /Subject {u16(subject)} /Creator (WipeKit) /Producer (WipeKit) "
                   f"/CreationDate ({stamp}) >>").encode()
        for i, content in enumerate(self.pages):
            pid = page_ids[i]
            objs[pid] = (f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {_num(self.w)} {_num(self.h)}] "
                         f"/Contents {pid + 1} 0 R /Resources ").encode() + res + b" >>"
            objs[pid + 1] = f"<< /Length {len(content)} >>\nstream\n".encode() + bytes(content) + b"\nendstream"
        out = bytearray(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")
        max_id = max(objs)
        offsets = [0] * (max_id + 1)
        for oid in range(1, max_id + 1):
            if oid not in objs:
                objs[oid] = b"null"
            offsets[oid] = len(out)
            out += f"{oid} 0 obj\n".encode() + objs[oid] + b"\nendobj\n"
        xref = len(out)
        out += f"xref\n0 {max_id + 1}\n0000000000 65535 f \n".encode()
        for oid in range(1, max_id + 1):
            out += f"{offsets[oid]:010d} 00000 n \n".encode()
        out += f"trailer\n<< /Size {max_id + 1} /Root 1 0 R /Info 6 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode()
        return bytes(out)
