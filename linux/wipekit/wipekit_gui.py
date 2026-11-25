#!/usr/bin/env python3
"""WipeKit für Linux – Oberfläche (GTK 4 + libadwaita ≥ 1.4)."""

import json
import math
import os
import platform
import shutil
import socket
import subprocess
import sys
import time
import uuid
from dataclasses import asdict
from pathlib import Path

import gi

gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
gi.require_version("Pango", "1.0")
gi.require_version("Gdk", "4.0")
from gi.repository import Adw, Gdk, Gio, GLib, Gtk, Pango  # noqa: E402

sys.path.insert(0, os.path.dirname(os.path.realpath(__file__)))
from common import (  # noqa: E402
    ACTIVE_STATES, APP_ID, APP_NAME, MODES, MODES_BY_KEY, NOT_CERTIFIED, VERSION, Disk, HelperStatus,
    fmt_bytes, fmt_clock, fmt_datetime, fmt_duration, fmt_exact_bytes, fmt_percent, fmt_short_duration,
    fmt_speed, list_disks, pid_alive, recommended_mode,
)
import health as hl  # noqa: E402
import jobfinish  # noqa: E402
import push  # noqa: E402
import reports as rp  # noqa: E402
import updater  # noqa: E402
from settings import Settings  # noqa: E402

SETTINGS = Settings()

HERE = Path(__file__).resolve().parent
HELPER = HERE / "wipekit-helper"
DIAG = HERE / "wipekit-diag"
ICON_DIR = HERE / "icons"
BASE_DIR = Path(f"/tmp/wipekit-{os.getuid()}")
CHUNK = 4 * 1024 * 1024

COLORS = {
    "accent": (0.21, 0.52, 0.89),
    "done": (0.18, 0.76, 0.49),
    "failed": (0.88, 0.11, 0.14),
    "cancelled": (1.0, 0.47, 0.0),
}
RATING_CSS = {hl.VERY_GOOD: "success", hl.GOOD: "accent", hl.CAUTION: "warning", hl.BAD: "error",
              hl.UNKNOWN: "dim-label"}

USB_HELP = ("Viele USB-Gehäuse und -Adapter geben die Gesundheitsdaten nicht weiter. WipeKit probiert "
            "automatisch alle bekannten Adapter-Typen (SAT, ASMedia, JMicron, Realtek). Klappt es trotzdem "
            "nicht: das Laufwerk intern per SATA oder NVMe anschließen oder ein USB-Gehäuse mit ASMedia-, "
            "JMicron- oder Realtek-Chip verwenden. Der Oberflächentest funktioniert auch ohne diese Daten.")


def default_reports_folder() -> Path:
    docs = GLib.get_user_special_dir(GLib.UserDirectory.DIRECTORY_DOCUMENTS) or str(Path.home() / "Documents")
    return Path(docs) / "WipeKit-Protokolle"


def reports_folder() -> Path:
    env = os.environ.get("WIPEKIT_REPORTS")
    if env:
        return Path(env)
    return SETTINGS.reports_folder or default_reports_folder()


def os_label() -> str:
    try:
        for line in Path("/etc/os-release").read_text().splitlines():
            if line.startswith("PRETTY_NAME="):
                return line.split("=", 1)[1].strip().strip('"')
    except OSError:
        pass
    return "Linux " + platform.release()


def open_path(path: Path):
    try:
        Gio.AppInfo.launch_default_for_uri(Path(path).as_uri(), None)
    except GLib.Error:
        pass


def row(title: str, subtitle: str = None, prop: bool = False) -> Adw.ActionRow:
    r = Adw.ActionRow()
    r.set_use_markup(False)
    r.set_title(title)
    if subtitle is not None:
        r.set_subtitle(subtitle)
    if prop:
        r.add_css_class("property")
    return r


def value_row(title: str, value: str, css=None) -> Adw.ActionRow:
    """Zeile mit Bezeichnung links und Wert rechts (gut lesbar, wie in den Systemeinstellungen)."""
    r = Adw.ActionRow()
    r.set_use_markup(False)
    r.set_title(title)
    v = Gtk.Label(label=value, xalign=1.0, wrap=True, selectable=True)
    v.set_max_width_chars(36)
    v.set_justify(Gtk.Justification.RIGHT)
    v.add_css_class("numeric")
    if css:
        v.add_css_class(css)
    else:
        v.add_css_class("dim-label")
    r.add_suffix(v)
    r.value_label = v
    return r


def label(text: str = "", css=(), wrap=False, xalign=0.0) -> Gtk.Label:
    lb = Gtk.Label(label=text)
    lb.set_xalign(xalign)
    lb.set_wrap(wrap)
    for c in css:
        lb.add_css_class(c)
    return lb


def load_health(path: Path):
    try:
        return hl.parse_text(path.read_text())
    except OSError:
        return None


# ---------------------------------------------------------------------------
# Fortschrittsring, Punkt und Geschwindigkeitskurve
# ---------------------------------------------------------------------------

class Ring(Gtk.DrawingArea):
    def __init__(self, size: int, line: float):
        super().__init__()
        self.fraction = 0.0
        self.color = COLORS["accent"]
        self.line = line
        self.set_content_width(size)
        self.set_content_height(size)
        self.set_draw_func(self._draw)

    def update(self, fraction: float, color):
        self.fraction = max(0.0, min(1.0, fraction or 0.0))
        self.color = color
        self.queue_draw()

    def _draw(self, area, cr, w, h, *args):
        r = min(w, h) / 2 - self.line / 2
        cx, cy = w / 2, h / 2
        cr.set_line_width(self.line)
        cr.set_source_rgba(*self.color, 0.18)
        cr.arc(cx, cy, r, 0, 2 * math.pi)
        cr.stroke()
        if self.fraction > 0:
            cr.set_line_cap(1)   # rund
            cr.set_source_rgb(*self.color)
            cr.arc(cx, cy, r, -math.pi / 2, -math.pi / 2 + 2 * math.pi * self.fraction)
            cr.stroke()


class Dot(Gtk.DrawingArea):
    def __init__(self, color, size=10):
        super().__init__()
        self.color = color
        self.set_content_width(size)
        self.set_content_height(size)
        self.set_valign(Gtk.Align.CENTER)
        self.set_draw_func(self._draw)

    def _draw(self, area, cr, w, h, *args):
        cr.set_source_rgb(*self.color)
        cr.arc(w / 2, h / 2, min(w, h) / 2, 0, 2 * math.pi)
        cr.fill()


class Curve(Gtk.DrawingArea):
    """Lesegeschwindigkeit über die Position auf dem Laufwerk."""

    def __init__(self):
        super().__init__()
        self.values = []
        self.total = 200
        self.set_content_height(150)
        self.set_hexpand(True)
        self.set_draw_func(self._draw)

    def update(self, values, total):
        self.values = values or []
        self.total = max(1, total)
        self.queue_draw()

    def _draw(self, area, cr, w, h, *args):
        try:
            c = self.get_color() if hasattr(self, "get_color") else self.get_style_context().get_color()
            fg = (c.red, c.green, c.blue)
        except Exception:
            fg = (0.5, 0.5, 0.5)
        pad_l, pad_b, pad_t = 44, 18, 8
        gw, gh = w - pad_l - 6, h - pad_b - pad_t
        vmax = max(self.values) * 1.15 if self.values else 100
        vmax = max(vmax, 1)
        cr.set_line_width(1)
        cr.set_source_rgba(*fg, 0.15)
        for i in range(5):
            y = pad_t + gh * i / 4
            cr.move_to(pad_l, y)
            cr.line_to(pad_l + gw, y)
        cr.stroke()
        cr.set_source_rgba(*fg, 0.6)
        cr.select_font_face("Sans")
        cr.set_font_size(10)
        for i in range(5):
            v = vmax * (4 - i) / 4
            txt = f"{v:.0f}"
            ext = cr.text_extents(txt)
            cr.move_to(pad_l - 6 - ext.width, pad_t + gh * i / 4 + 4)
            cr.show_text(txt)
        cr.move_to(pad_l, h - 4)
        cr.show_text("Anfang")
        ext = cr.text_extents("Ende")
        cr.move_to(pad_l + gw - ext.width, h - 4)
        cr.show_text("Ende")
        ext = cr.text_extents("MB/s")
        cr.move_to(pad_l + (gw - ext.width) / 2, h - 4)
        cr.show_text("MB/s")
        if len(self.values) < 1:
            return
        pts = [(pad_l + gw * (i + 0.5) / self.total, pad_t + gh * (1 - v / vmax)) for i, v in enumerate(self.values)]
        cr.set_source_rgba(*COLORS["accent"], 0.18)
        cr.move_to(pts[0][0], pad_t + gh)
        for x, y in pts:
            cr.line_to(x, y)
        cr.line_to(pts[-1][0], pad_t + gh)
        cr.close_path()
        cr.fill()
        cr.set_source_rgb(*COLORS["accent"])
        cr.set_line_width(1.8)
        cr.move_to(*pts[0])
        for x, y in pts[1:]:
            cr.line_to(x, y)
        cr.stroke()


def curve_total(size: int) -> int:
    bucket = max(CHUNK, size // 200) if size else CHUNK
    return max(1, math.ceil((size or 1) / bucket))


# ---------------------------------------------------------------------------
# Vorgang (Oberflächen-Seite)
# ---------------------------------------------------------------------------

class Job:
    def __init__(self, info: dict, job_dir: Path, proc=None):
        self.info = info
        self.id = info["id"]
        self.disk = Disk(**info["disk"])
        self.mode = MODES_BY_KEY[info["mode"]]
        self.purpose = info.get("purpose", "")
        self.dir = job_dir
        self.proc = proc
        self.status = None
        self.state = "starting"
        self.message = None
        self.cancel_requested = False
        self.reports = {}
        self.reported = (job_dir / "reports.json").exists()
        if self.reported:
            try:
                self.reports = json.loads((job_dir / "reports.json").read_text())
            except (OSError, ValueError):
                pass
        self.dismissed = False

    @property
    def kind(self) -> str:
        if self.mode.key.startswith("selftest"):
            return "selftest"
        if self.mode.read_only:
            return "check"
        return "wipe"

    @property
    def title(self) -> str:
        if self.kind == "selftest":
            return self.mode.title
        if self.kind == "check":
            return "Oberflächentest" if self.purpose == "scan" else "Prüfung"
        return "Löschen"

    @property
    def protocol(self):
        p = self.reports.get("pdf")
        return Path(p) if p else None

    @property
    def active(self) -> bool:
        return self.state in ACTIVE_STATES

    @property
    def fraction(self) -> float:
        s = self.status
        if not s or not s.bytesTotal:
            return 1.0 if self.state == "done" else 0.0
        return min(1.0, s.bytesDoneTotal / s.bytesTotal)

    @property
    def eta(self):
        s = self.status
        if not s or s.state != "running" or s.speed <= 1 or s.bytesTotal <= s.bytesDoneTotal:
            return None
        if self.kind == "selftest" or self.mode.purge or time.time() - s.startedAt < 15:
            return None
        return (s.bytesTotal - s.bytesDoneTotal) / s.speed

    @property
    def elapsed(self):
        s = self.status
        if not s or s.state == "starting":
            return None
        return (s.finishedAt or time.time()) - s.startedAt

    @property
    def color(self):
        if self.state == "done" and self.kind == "check" and self.status and self.status.badBlocks:
            return COLORS["failed"]
        return COLORS.get(self.state, COLORS["accent"])

    @property
    def done_text(self) -> str:
        if self.kind == "wipe":
            return "Erfolgreich gelöscht"
        if self.kind == "selftest":
            return "Ohne Fehler abgeschlossen"
        s = self.status
        if s and s.badBlocks:
            return "Unlesbare Bereiche gefunden"
        if self.purpose == "scan":
            return "Alle Sektoren lesbar"
        return "Laufwerk ist leer" if s and s.nonZeroBytes == 0 else "Laufwerk enthält Daten"

    @property
    def short_status(self) -> str:
        if self.state == "starting":
            return "Wird gestartet …"
        if self.state == "running":
            e = self.eta
            return f"{fmt_percent(self.fraction)} · noch {fmt_short_duration(e)}" if e else fmt_percent(self.fraction)
        return {"done": self.done_text, "failed": "Fehlgeschlagen", "cancelled": "Abgebrochen"}[self.state]

    def poll(self):
        """Liest status.json. Gibt True zurück, wenn sich der Zustand geändert hat."""
        before = self.state
        try:
            self.status = HelperStatus.from_json((self.dir / "status.json").read_text())
            self.state = self.status.state
            self.message = self.status.message
            if self.active and not pid_alive(self.status.pid) and time.time() - self.status.updatedAt > 10:
                self.state = "failed"
                self.message = ("Der Arbeitsprozess wurde unerwartet beendet (Absturz oder Neustart?)."
                                + (" Das Laufwerk ist nur teilweise überschrieben." if self.kind == "wipe" else ""))
        except (OSError, ValueError):
            if self.proc is not None and self.proc.poll() is not None:
                code = self.proc.returncode
                if code == 126:          # Passwortabfrage abgebrochen
                    self.dismissed = True
                    self.state = "cancelled"
                elif code == 127:
                    self.state = "failed"
                    self.message = "Keine Berechtigung – die Anmeldung als Administrator ist fehlgeschlagen."
                else:
                    self.state = "failed"
                    log = ""
                    try:
                        log = (self.dir / "pkexec.log").read_text().strip()[-300:]
                    except OSError:
                        pass
                    self.message = f"Der Arbeitsprozess konnte nicht gestartet werden (Code {code}). {log}".strip()
            elif self.proc is None and time.time() - self.info["createdAt"] > 60:
                self.state = "failed"
                self.message = "Der Arbeitsprozess konnte nicht gestartet werden."
        return before != self.state

    def request_cancel(self):
        self.cancel_requested = True
        try:
            (self.dir / "cancel").touch()
        except OSError:
            pass


# ---------------------------------------------------------------------------
# Hauptfenster
# ---------------------------------------------------------------------------

class MainWindow(Adw.ApplicationWindow):
    def __init__(self, app):
        super().__init__(application=app, title=APP_NAME)
        self.set_default_size(1060, 760)
        self.set_size_request(880, 580)
        self.app = app
        self.disks = []
        self.disk_signature = None
        self.jobs = []
        self.selection = None      # ("disk", name) | ("job", id)
        self.view = "wipe"         # "wipe" | "diag"
        self.mode_key = SETTINGS["defaultMode"] if SETTINGS["defaultMode"] in MODES_BY_KEY else "secure"
        self.updating_lists = False
        self.force_close = False
        self.job_page_refs = {}
        self.job_rows = {}
        self.disk_rings = {}
        self.health = {}           # Laufwerksname -> {"h": Health, "hpa": dict, "t": Zeit}
        self.diag_state = "idle"   # idle | loading | done | error
        self.diag_error = ""
        self.internal_confirm = False

        BASE_DIR.mkdir(mode=0o700, exist_ok=True)
        self._actions()

        # --- Seitenleiste ---------------------------------------------------
        sidebar_header = Adw.HeaderBar()
        refresh = Gtk.Button(icon_name="view-refresh-symbolic", tooltip_text="Laufwerke neu einlesen (F5)")
        refresh.connect("clicked", lambda *_: self.refresh_disks(force=True))
        sidebar_header.pack_start(refresh)
        menu = Gio.Menu()
        menu.append("Gesundheitsdaten lesen", "win.read-health")
        menu.append("Ordner mit Berichten öffnen", "win.open-reports")
        prefs = Gio.Menu()
        prefs.append("Einstellungen", "win.preferences")
        prefs.append("Nach Updates suchen …", "win.check-updates")
        menu.append_section(None, prefs)
        section = Gio.Menu()
        section.append("Über WipeKit", "app.about")
        menu.append_section(None, section)
        menu_btn = Gtk.MenuButton(icon_name="open-menu-symbolic", menu_model=menu, tooltip_text="Hauptmenü")
        if hasattr(menu_btn, "set_primary"):
            menu_btn.set_primary(True)
        sidebar_header.pack_end(menu_btn)

        self.ext_list = self._listbox()
        self.int_list = self._listbox()
        self.job_list = self._listbox()
        for lst in (self.ext_list, self.int_list, self.job_list):
            lst.connect("row-selected", self.on_row_selected)
        self.no_disks = label("Kein Laufwerk angeschlossen", css=("dim-label",))
        self.no_disks.set_margin_start(18)
        self.no_disks.set_margin_top(6)
        self.int_heading = self.section_heading("Interne Laufwerke")
        self.jobs_heading = self.section_heading("Vorgänge")

        side_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        side_box.append(self.section_heading("Externe Laufwerke"))
        side_box.append(self.no_disks)
        side_box.append(self.ext_list)
        side_box.append(self.int_heading)
        side_box.append(self.int_list)
        side_box.append(self.jobs_heading)
        side_box.append(self.job_list)
        side_scroll = Gtk.ScrolledWindow(vexpand=True)
        side_scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        side_scroll.set_child(side_box)

        self.sleep_label = label("", css=("dim-label", "caption"))
        self.sleep_icon = Gtk.Image()
        sleep_box = Gtk.Box(spacing=8, margin_start=14, margin_end=14, margin_top=8, margin_bottom=8)
        sleep_box.append(self.sleep_icon)
        sleep_box.append(self.sleep_label)

        sidebar_view = Adw.ToolbarView()
        sidebar_view.add_top_bar(sidebar_header)
        sidebar_view.set_content(side_scroll)
        sidebar_view.add_bottom_bar(sleep_box)
        sidebar_page = Adw.NavigationPage(title=APP_NAME, child=sidebar_view)

        # --- Inhalt: Umschalter »Löschen | Diagnose« ------------------------------
        self.stack = Adw.ViewStack()
        self.wipe_holder = Adw.Bin()
        self.diag_holder = Adw.Bin()
        p1 = self.stack.add_titled(self.wipe_holder, "wipe", "Löschen")
        p1.set_icon_name("wipekit-shield-symbolic")
        p2 = self.stack.add_titled(self.diag_holder, "diag", "Diagnose")
        p2.set_icon_name("wipekit-pulse-symbolic")
        self.stack.connect("notify::visible-child-name", self.on_view_changed)
        self.switcher = Adw.ViewSwitcher(stack=self.stack)
        self.switcher.set_policy(Adw.ViewSwitcherPolicy.WIDE)

        self.content_header = Adw.HeaderBar()
        reports_btn = Gtk.Button(icon_name="folder-documents-symbolic",
                                 tooltip_text="Ordner mit Protokollen und Berichten öffnen")
        reports_btn.connect("clicked", lambda *_: self.on_open_reports())
        self.content_header.pack_end(reports_btn)
        self.content_view = Adw.ToolbarView()
        self.content_view.add_top_bar(self.content_header)
        self.content_page = Adw.NavigationPage(title=APP_NAME, child=self.content_view)

        split = Adw.NavigationSplitView()
        split.set_sidebar(sidebar_page)
        split.set_content(self.content_page)
        split.set_min_sidebar_width(260)
        split.set_max_sidebar_width(340)
        self.toasts = Adw.ToastOverlay()
        self.toasts.set_child(split)
        self.set_content(self.toasts)

        self.connect("close-request", self.on_close_request)

        self.restore_jobs()
        self.refresh_disks(force=True)
        self.update_sleep_status()
        if self.selection is None:
            self.show_detail()
        GLib.timeout_add_seconds(1, self.tick)
        GLib.timeout_add_seconds(3, self.periodic_refresh)
        self.heartbeat()
        GLib.timeout_add_seconds(5, self.heartbeat)
        if not app.smoke_test:
            GLib.timeout_add_seconds(2, self.updates_on_launch)

    def heartbeat(self):
        """Zeigt dem Arbeitsprozess, dass WipeKit läuft (dann meldet sich die App selbst)."""
        try:
            (BASE_DIR / "app-alive").touch()
        except OSError:
            pass
        return True

    # --- Aktionen und Tastenkürzel --------------------------------------------------

    def _actions(self):
        def add(name, cb, accels=()):
            a = Gio.SimpleAction.new(name, None)
            a.connect("activate", lambda *_: cb())
            self.add_action(a)
            if accels:
                self.app.set_accels_for_action(f"win.{name}", list(accels))
        add("view-wipe", lambda: self.set_view("wipe"), ["<Control>1"])
        add("view-diag", lambda: self.set_view("diag"), ["<Control>2"])
        add("refresh", lambda: self.refresh_disks(force=True), ["F5", "<Control>r"])
        add("read-health", self.read_health, ["<Control><Shift>r"])
        add("open-reports", self.on_open_reports, ["<Control>o"])
        add("preferences", self.show_preferences, ["<Control>comma"])
        add("check-updates", lambda: self.check_updates(user=True))

    @staticmethod
    def _listbox():
        lb = Gtk.ListBox()
        lb.add_css_class("navigation-sidebar")
        return lb

    def set_view(self, view):
        self.stack.set_visible_child_name(view)
        if self.view != view:
            self.view = view
            self.show_detail()

    def on_view_changed(self, *_):
        v = self.stack.get_visible_child_name() or "wipe"
        if v != self.view:
            self.view = v
            self.show_detail()

    def toast(self, text, button=None, action=None):
        t = Adw.Toast(title=text)
        t.set_timeout(6)
        if button and action:
            t.set_button_label(button)
            t.connect("button-clicked", lambda *_: action())
        self.toasts.add_toast(t)

    # --- Hilfen ---------------------------------------------------------------

    @staticmethod
    def section_heading(text):
        lb = label(text, css=("heading", "dim-label"))
        lb.set_margin_start(18)
        lb.set_margin_top(14)
        lb.set_margin_bottom(4)
        return lb

    def find_disk(self, name):
        return next((d for d in self.disks if d.name == name), None)

    def find_job(self, job_id):
        return next((j for j in self.jobs if j.id == job_id), None)

    def active_job_for(self, disk):
        return next((j for j in self.jobs if j.disk.name == disk.name and j.active), None)

    def health_for(self, disk):
        e = self.health.get(disk.name)
        return e["h"] if e else None

    # --- Laufwerke ------------------------------------------------------------

    def periodic_refresh(self):
        self.refresh_disks()
        return True

    def refresh_disks(self, force=False):
        disks = list_disks()
        sig = [(d.name, d.size, d.model, tuple(d.volumes), d.is_protected, d.internal) for d in disks]
        if sig == self.disk_signature and not force:
            return
        self.disk_signature = sig
        self.disks = disks
        if self.selection and self.selection[0] == "disk" and not self.find_disk(self.selection[1]):
            self.selection = None
        if self.selection is None:
            first = next((d for d in disks if not d.internal and not d.is_protected and not self.active_job_for(d)),
                         None) or (disks[0] if disks else None)
            if first:
                self.select(("disk", first.name))
        self.rebuild_disk_list()
        self.show_detail()

    def rebuild_disk_list(self):
        self.updating_lists = True
        self.ext_list.remove_all()
        self.int_list.remove_all()
        self.disk_rings = {}
        ext = [d for d in self.disks if not d.internal]
        inner = [d for d in self.disks if d.internal]
        self.no_disks.set_visible(not ext)
        self.int_heading.set_visible(bool(inner))
        for d in self.disks:
            r = Gtk.ListBoxRow()
            r.key = ("disk", d.name)
            box = Gtk.Box(spacing=10, margin_top=6, margin_bottom=6, margin_start=4, margin_end=4)
            icon = Gtk.Image.new_from_icon_name(d.icon)
            icon.set_pixel_size(20)
            texts = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, hexpand=True)
            t = label(d.model)
            t.set_ellipsize(Pango.EllipsizeMode.END)
            texts.append(t)
            texts.append(label(f"{fmt_bytes(d.size)} · {d.name}", css=("dim-label", "caption")))
            box.append(icon)
            box.append(texts)
            h = self.health_for(d)
            if h is not None:
                dot = Dot(hl.RATING_COLORS.get(h.rating, hl.RATING_COLORS[hl.UNKNOWN]), 9)
                dot.set_tooltip_text(f"Gesamtzustand: {h.rating}")
                box.append(dot)
            job = self.active_job_for(d)
            if d.is_protected:
                lock = Gtk.Image.new_from_icon_name("changes-prevent-symbolic")
                lock.set_tooltip_text("Systemlaufwerk – geschützt")
                box.append(lock)
            elif job:
                ring = Ring(16, 2.5)
                ring.update(job.fraction, COLORS["accent"])
                box.append(ring)
                self.disk_rings[d.name] = ring
            r.set_child(box)
            (self.int_list if d.internal else self.ext_list).append(r)
            if self.selection == r.key:
                (self.int_list if d.internal else self.ext_list).select_row(r)
        self.updating_lists = False

    # --- Vorgänge in der Seitenleiste -----------------------------------------------

    def rebuild_job_list(self):
        self.updating_lists = True
        self.job_list.remove_all()
        self.job_rows = {}
        self.jobs_heading.set_visible(bool(self.jobs))
        for j in self.jobs:
            r = Gtk.ListBoxRow()
            r.key = ("job", j.id)
            box = Gtk.Box(spacing=10, margin_top=6, margin_bottom=6, margin_start=4, margin_end=4)
            ring = Ring(18, 2.5)
            icon = Gtk.Image()
            icon.set_pixel_size(18)
            texts = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, hexpand=True)
            t = label(f"{j.title} · {j.disk.model}" if j.kind != "wipe" else j.disk.model)
            t.set_ellipsize(Pango.EllipsizeMode.END)
            sub = label("", css=("dim-label", "caption"))
            texts.append(t)
            texts.append(sub)
            box.append(ring)
            box.append(icon)
            box.append(texts)
            r.set_child(box)
            self.job_list.append(r)
            self.job_rows[j.id] = (ring, icon, sub)
            if self.selection == r.key:
                self.job_list.select_row(r)
        self.update_job_rows()
        self.updating_lists = False

    def update_job_rows(self):
        for j in self.jobs:
            refs = self.job_rows.get(j.id)
            if not refs:
                continue
            ring, icon, sub = refs
            sub.set_label(j.short_status)
            if j.active:
                ring.set_visible(True)
                icon.set_visible(False)
                ring.update(j.fraction, COLORS["accent"])
            else:
                ring.set_visible(False)
                icon.set_visible(True)
                name = {"done": "object-select-symbolic", "failed": "dialog-error-symbolic",
                        "cancelled": "process-stop-symbolic"}.get(j.state, "dialog-question-symbolic")
                if j.state == "done" and j.color == COLORS["failed"]:
                    name = "dialog-warning-symbolic"
                icon.set_from_icon_name(name)

    def update_sleep_status(self):
        active = any(j.active for j in self.jobs)
        self.sleep_icon.set_from_icon_name("wipekit-awake-symbolic" if active else "wipekit-sleep-symbolic")
        self.sleep_label.set_label("Rechner bleibt wach" if active else "Ruhezustand wie gewohnt")

    # --- Auswahl ------------------------------------------------------------------

    def select(self, key):
        self.selection = key
        self.updating_lists = True
        for lst in (self.ext_list, self.int_list, self.job_list):
            lst.unselect_all()
            i = 0
            while True:
                r = lst.get_row_at_index(i)
                if r is None:
                    break
                if getattr(r, "key", None) == key:
                    lst.select_row(r)
                i += 1
        self.updating_lists = False
        self.internal_confirm = False
        if key and key[0] == "disk":
            d = self.find_disk(key[1])
            if d:
                rec = recommended_mode(d)
                if d.is_ssd is True:
                    self.mode_key = rec
                elif self.mode_key in ("ssd", "purge"):
                    dm = SETTINGS["defaultMode"]
                    self.mode_key = dm if dm in MODES_BY_KEY and dm not in ("ssd", "purge") else "secure"

    def on_row_selected(self, listbox, r):
        if self.updating_lists or r is None:
            return
        self.select(r.key)
        self.show_detail()

    # --- Detailbereich ------------------------------------------------------------

    def show_detail(self):
        self.job_page_refs = {}
        widget = None
        if self.selection and self.selection[0] == "job":
            j = self.find_job(self.selection[1])
            if j:
                widget = self.build_job_page(j)
                self.content_page.set_title(j.disk.model)
        elif self.selection and self.selection[0] == "disk":
            d = self.find_disk(self.selection[1])
            if d:
                self.content_page.set_title(d.model)
                self.content_header.set_title_widget(self.switcher)
                if self.stack.get_visible_child_name() != self.view:
                    self.stack.set_visible_child_name(self.view)
                if self.view == "diag":
                    self.wipe_holder.set_child(None)
                    self.diag_holder.set_child(self.build_diag_page(d))
                else:
                    self.diag_holder.set_child(None)
                    self.wipe_holder.set_child(self.build_disk_page(d))
                if self.content_view.get_content() is not self.stack:
                    self.content_view.set_content(self.stack)
                return
        self.content_header.set_title_widget(None)
        if widget is None:
            self.content_page.set_title(APP_NAME)
            status = Adw.StatusPage()
            status.set_icon_name("wipekit-drive-symbolic")
            if self.disks:
                status.set_title("Kein Laufwerk ausgewählt")
                status.set_description("Wähle in der Seitenleiste ein Laufwerk aus.")
            else:
                status.set_title("Kein Laufwerk angeschlossen")
                status.set_description("Schließe ein Laufwerk per USB an. Es erscheint automatisch in der Seitenleiste.")
            widget = status
        self.content_view.set_content(widget)

    @staticmethod
    def with_action_bar(page, bar_children_start, bar_children_end):
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        page.set_vexpand(True)
        box.append(page)
        if bar_children_start or bar_children_end:
            bar = Gtk.ActionBar()
            for w in bar_children_start:
                bar.pack_start(w)
            for w in bar_children_end:
                bar.pack_end(w)
            box.append(bar)
        return box

    def header_group(self, icon_name, title, subtitle):
        g = Adw.PreferencesGroup()
        hb = Gtk.Box(spacing=16)
        icon = Gtk.Image.new_from_icon_name(icon_name)
        icon.set_pixel_size(56)
        icon.add_css_class("accent")
        texts = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, valign=Gtk.Align.CENTER, spacing=4)
        t = label(title, css=("title-2",), wrap=True)
        texts.append(t)
        texts.append(label(subtitle, css=("dim-label",), wrap=True))
        hb.append(icon)
        hb.append(texts)
        g.add(hb)
        return g

    def disk_subtitle(self, d: Disk):
        parts = [fmt_bytes(d.size), d.type_label, d.bus]
        if d.internal:
            parts.append("intern")
        return " · ".join(parts)

    # Laufwerk: Löschen -----------------------------------------------------------------

    def build_disk_page(self, d: Disk):
        page = Adw.PreferencesPage()
        page.add(self.header_group(d.icon, d.model, self.disk_subtitle(d)))
        bar_start, bar_end = [], []
        job = self.active_job_for(d)

        def note(text, icon, button=None):
            g = Adw.PreferencesGroup()
            r = row(text)
            r.set_title_lines(0)
            r.add_prefix(Gtk.Image.new_from_icon_name(icon))
            if button:
                r.add_suffix(button)
            g.add(r)
            page.add(g)

        if d.is_protected or d.read_only:
            btn = Gtk.Button(label="Zur Diagnose", valign=Gtk.Align.CENTER)
            btn.connect("clicked", lambda *_: self.set_view("diag"))
            note("Auf diesem Laufwerk läuft das System. Es ist geschützt und kann nicht gelöscht werden – "
                 "die Diagnose ist aber möglich." if d.is_protected else
                 "Dieses Laufwerk ist schreibgeschützt und kann nicht gelöscht werden.",
                 "changes-prevent-symbolic", btn)
        elif job:
            btn = Gtk.Button(label="Fortschritt anzeigen", valign=Gtk.Align.CENTER)
            btn.connect("clicked", lambda *_: (self.select(("job", job.id)), self.show_detail()))
            note(f"Auf diesem Laufwerk läuft gerade: {job.title}.", "content-loading-symbolic", btn)
        else:
            if d.internal:
                note("Internes Laufwerk. Prüfe genau, ob es wirklich gelöscht werden soll – "
                     "WipeKit fragt vor dem Start noch einmal nach.", "dialog-warning-symbolic")
            h = self.health.get(d.name)
            hpa = (h or {}).get("hpa") or {}
            if hpa.get("enabled") and hpa.get("native", 0) > hpa.get("visible", 0):
                note("Auf diesem Laufwerk ist ein versteckter Bereich (HPA) aktiv. Er wird beim Überschreiben "
                     "nicht erreicht. Das Protokoll vermerkt das.", "dialog-warning-symbolic")

            modes = Adw.PreferencesGroup(title="Methode")
            first_check = None
            rec = recommended_mode(d)
            for m in MODES:
                r = row(m.title, m.tagline)
                check = Gtk.CheckButton()
                if first_check is None:
                    first_check = check
                else:
                    check.set_group(first_check)
                usable = not (m.purge and not d.direct_attached)
                check.set_active(m.key == self.mode_key and usable)
                check.connect("toggled", self.on_mode_toggled, m.key, d)
                icon = Gtk.Image.new_from_icon_name(m.icon)
                r.add_prefix(icon)
                r.add_prefix(check)
                if not usable:
                    r.set_sensitive(False)
                    r.set_subtitle("Nur bei direkt angeschlossenen SATA-/NVMe-Laufwerken")
                if m.key == rec:
                    badge = label("Empfohlen", css=("accent", "caption-heading"))
                    badge.set_valign(Gtk.Align.CENTER)
                    r.add_suffix(badge)
                if m.read_only:
                    badge = label("Ändert nichts", css=("dim-label", "caption"))
                    badge.set_valign(Gtk.Align.CENTER)
                    r.add_suffix(badge)
                r.set_activatable_widget(check)
                modes.add(r)
            page.add(modes)

            self.mode_group = Adw.PreferencesGroup()
            self.mode_best = row("Geeignet für", "", prop=True)
            self.mode_security = row("Sicherheit")
            self.mode_level = Gtk.LevelBar(min_value=0, max_value=5, valign=Gtk.Align.CENTER)
            self.mode_level.set_mode(Gtk.LevelBarMode.DISCRETE)
            self.mode_level.set_size_request(100, -1)
            self.mode_security_label = label("")
            self.mode_security.add_suffix(self.mode_level)
            self.mode_security.add_suffix(self.mode_security_label)
            self.mode_standard = row("Orientiert sich an", "", prop=True)
            self.mode_duration = row("Geschätzte Dauer", "", prop=True)
            self.mode_note = label("", css=("dim-label", "caption"), wrap=True)
            self.mode_note.set_margin_top(10)
            self.mode_hint = label("", css=("warning", "caption"), wrap=True)
            self.mode_hint.set_margin_top(6)
            for w in (self.mode_best, self.mode_security, self.mode_standard, self.mode_duration,
                      self.mode_note, self.mode_hint):
                self.mode_group.add(w)
            page.add(self.mode_group)

            self.action_warn = label("", css=("dim-label",))
            self.action_warn.set_ellipsize(Pango.EllipsizeMode.END)
            self.action_btn = Gtk.Button(label="Löschen …")
            self.action_btn.connect("clicked", lambda *_: self.confirm_start(d))
            bar_start.append(self.action_warn)
            bar_end.append(self.action_btn)
            self.update_mode_detail(d)

        info = Adw.PreferencesGroup(title="Informationen")
        info.add(value_row("Gerät", d.path))
        info.add(value_row("Kapazität", f"{fmt_bytes(d.size)} ({fmt_exact_bytes(d.size)})"))
        info.add(value_row("Anschluss", d.bus + (" (intern)" if d.internal else "")))
        info.add(value_row("Typ", d.type_label))
        if d.serial:
            info.add(value_row("Seriennummer", d.serial))
        info.add(value_row("Volumes", ", ".join(d.volumes) if d.volumes else "Keine"))
        page.add(info)
        return self.with_action_bar(page, bar_start, bar_end)

    def on_mode_toggled(self, check, key, disk):
        if check.get_active():
            self.mode_key = key
            self.update_mode_detail(disk)

    def update_mode_detail(self, d: Disk):
        m = MODES_BY_KEY[self.mode_key]
        self.mode_group.set_title(f"Über »{m.title}«")
        self.mode_group.set_description(m.explanation)
        self.mode_best.set_subtitle(m.best_for)
        self.mode_level.set_value(m.security_level)
        self.mode_security_label.set_label(m.security_label)
        self.mode_standard.set_subtitle(m.standard)
        if m.purge:
            self.mode_duration.set_subtitle("Meist Sekunden bis wenige Minuten – je nach Laufwerk")
        else:
            speed = 300e6 if d.is_ssd is True else 130e6
            units = m.work_units + (0.02 if m.verify == "samples" else 0)
            self.mode_duration.set_subtitle("ca. " + fmt_short_duration(d.size * units / speed))
        self.mode_note.set_label(m.security_note + ("" if m.read_only else "\n" + NOT_CERTIFIED))
        hint = None
        if m.read_only:
            hint = None
        elif m.purge and d.is_ssd is False:
            hint = "Bei Festplatten (HDD) kann die Firmware-Löschung Stunden dauern – »Sicher« ist meist schneller."
        elif d.is_ssd is True and m.key not in ("ssd", "purge"):
            hint = "Dieses Laufwerk ist eine SSD – dafür sind »SSD« oder »Firmware-Löschung« besser geeignet."
        elif d.is_ssd is False and m.key == "ssd":
            hint = "Dieses Laufwerk ist eine Festplatte – TRIM wird hier nicht unterstützt. »Sicher« ist die bessere Wahl."
        elif d.is_ssd is None and not m.purge:
            hint = "Der Laufwerkstyp wird vom USB-Adapter nicht gemeldet. Handelt es sich um eine SSD, wähle den Modus »SSD«."
        self.mode_hint.set_label("⚠ " + hint if hint else "")
        self.mode_hint.set_visible(hint is not None)
        if getattr(self, "action_btn", None) is not None:
            for c in ("destructive-action", "suggested-action"):
                self.action_btn.remove_css_class(c)
            if m.read_only:
                self.action_btn.set_label("Prüfen …")
                self.action_btn.add_css_class("suggested-action")
                self.action_warn.set_label("Es werden keine Daten verändert.")
            else:
                self.action_btn.set_label("Löschen …")
                self.action_btn.add_css_class("destructive-action")
                self.action_warn.set_label(f"Alle Daten auf »{d.model}« werden unwiderruflich gelöscht.")

    # Bestätigen & starten --------------------------------------------------------------

    def confirm_start(self, d: Disk):
        m = MODES_BY_KEY[self.mode_key]
        if m.read_only:
            self.ask(f"»{d.model}« prüfen?",
                     f"WipeKit liest alle Sektoren von {d.path} ({fmt_bytes(d.size)}) und prüft, ob das Laufwerk "
                     "leer ist. Es werden keine Daten verändert.",
                     "Prüfung starten", lambda: self.start_job(d, m.key), destructive=False)
            return
        heading = f"»{d.model}« löschen?"
        body = (f"Alle Daten auf {d.path} ({fmt_bytes(d.size)}) werden mit der Methode »{m.title}« "
                "unwiderruflich gelöscht. Dieser Vorgang kann nicht rückgängig gemacht werden.")
        if m.purge:
            body += ("\n\nDas Laufwerk löscht sich dabei selbst. Rechner währenddessen nicht ausschalten und das "
                     "Laufwerk nicht trennen.")
        if d.internal:
            body += (f"\n\nACHTUNG: »{d.model}« ist ein INTERNES Laufwerk dieses Rechners. Prüfe die "
                     f"Seriennummer ({d.serial or 'nicht verfügbar'}) und die Größe genau.")
            self.ask(heading, body, "Internes Laufwerk löschen", lambda: self.start_job(d, m.key, internal=True))
            return
        self.ask(heading, body, "Löschen", lambda: self.start_job(d, m.key))

    def ask(self, heading, body, confirm_label, on_confirm, destructive=True):
        if hasattr(Adw, "AlertDialog"):
            dlg = Adw.AlertDialog(heading=heading, body=body)
        else:
            dlg = Adw.MessageDialog(transient_for=self, heading=heading, body=body)
        dlg.add_response("cancel", "Abbrechen")
        dlg.add_response("ok", confirm_label)
        dlg.set_response_appearance("ok", Adw.ResponseAppearance.DESTRUCTIVE if destructive
                                    else Adw.ResponseAppearance.SUGGESTED)
        dlg.set_default_response("cancel" if destructive else "ok")
        dlg.set_close_response("cancel")
        dlg.connect("response", lambda _d, resp: on_confirm() if resp == "ok" else None)
        if hasattr(Adw, "AlertDialog"):
            dlg.present(self)
        else:
            dlg.present()

    def inform(self, heading, body):
        if hasattr(Adw, "AlertDialog"):
            dlg = Adw.AlertDialog(heading=heading, body=body)
            dlg.add_response("ok", "OK")
            dlg.present(self)
        else:
            dlg = Adw.MessageDialog(transient_for=self, heading=heading, body=body)
            dlg.add_response("ok", "OK")
            dlg.present()

    def start_job(self, d: Disk, mode_key: str, internal=False, purpose=""):
        if self.active_job_for(d):
            return
        job_id = uuid.uuid4().hex
        job_dir = BASE_DIR / job_id
        info = {"id": job_id, "disk": asdict(d), "mode": mode_key, "createdAt": time.time(), "purpose": purpose}
        args = ["pkexec", str(HELPER), "--disk", d.name, "--mode", mode_key,
                "--job-dir", str(job_dir), "--expected-size", str(d.size)]
        if internal:
            args.append("--internal-ok")
        try:
            job_dir.mkdir(mode=0o700)
            (job_dir / "job.json").write_text(json.dumps(info))
            fd = os.open(job_dir / jobfinish.SETTINGS_FILE, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
            with os.fdopen(fd, "w") as f:
                json.dump(SETTINGS.job_settings(socket.gethostname(), os_label(), VERSION), f)
            log = open(job_dir / "pkexec.log", "w")
            proc = subprocess.Popen(args, stdin=subprocess.DEVNULL, stdout=log, stderr=log, start_new_session=True)
            log.close()
        except OSError as e:
            self.inform("Vorgang konnte nicht gestartet werden", str(e))
            return
        job = Job(info, job_dir, proc)
        self.jobs.insert(0, job)
        self.rebuild_job_list()
        self.select(("job", job.id))
        self.rebuild_disk_list()
        self.show_detail()
        self.update_sleep_status()

    # Laufwerk: Diagnose ----------------------------------------------------------------

    def read_health(self):
        if self.diag_state == "loading":
            return
        self.diag_state = "loading"
        self.show_detail()
        try:
            proc = Gio.Subprocess.new(["pkexec", str(DIAG)],
                                      Gio.SubprocessFlags.STDOUT_PIPE | Gio.SubprocessFlags.STDERR_PIPE)
        except GLib.Error as e:
            self.diag_state, self.diag_error = "error", e.message
            self.show_detail()
            return

        def done(p, res):
            try:
                ok, out, err = p.communicate_utf8_finish(res)
            except GLib.Error as e:
                ok, out, err = False, "", e.message
            code = p.get_exit_status() if p.get_if_exited() else -1
            if code == 126:
                self.diag_state = "idle"          # Passwortabfrage abgebrochen
            elif code != 0 or not out:
                self.diag_state = "error"
                self.diag_error = (err or "").strip()[-300:] or f"Fehlercode {code}"
            else:
                self.apply_diag(out)
            self.rebuild_disk_list()
            self.show_detail()
        proc.communicate_utf8_async(None, None, done)

    def apply_diag(self, text):
        try:
            data = json.loads(text)
        except ValueError:
            self.diag_state, self.diag_error = "error", "Ungültige Antwort des Diagnoseprozesses."
            return
        now = time.time()
        for name, e in (data.get("disks") or {}).items():
            h = hl.parse(e.get("smart") or {})
            if d := self.find_disk(name):
                if d.transport == "usb" and not h.available and not e.get("missing"):
                    h.reason += " " + USB_HELP
            self.health[name] = {"h": h, "hpa": e.get("hpa") or {}, "t": now}
        self.diag_state = "done"

    def build_diag_page(self, d: Disk):
        entry = self.health.get(d.name)
        if entry is None:
            sp = Adw.StatusPage()
            sp.set_icon_name("wipekit-pulse-symbolic")
            if self.diag_state == "loading":
                sp.set_title("Gesundheitsdaten werden gelesen …")
                sp.set_description("Das dauert bei vielen Laufwerken ein paar Sekunden.")
                spin = Gtk.Spinner(spinning=True, halign=Gtk.Align.CENTER)
                spin.set_size_request(32, 32)
                sp.set_child(spin)
            else:
                sp.set_title("Gesundheit prüfen")
                desc = ("WipeKit liest die S.M.A.R.T.-Daten (Gesundheitsdaten) aller Laufwerke. "
                        "Dafür ist einmal das Administrator-Passwort nötig. Es werden keine Daten verändert.")
                if self.diag_state == "error":
                    desc += f"\n\nLetzter Versuch fehlgeschlagen: {self.diag_error}"
                sp.set_description(desc)
                btn = Gtk.Button(label="Gesundheitsdaten lesen", halign=Gtk.Align.CENTER)
                btn.add_css_class("pill")
                btn.add_css_class("suggested-action")
                btn.connect("clicked", lambda *_: self.read_health())
                sp.set_child(btn)
            return sp

        h = entry["h"]
        page = Adw.PreferencesPage()
        page.add(self.header_group(d.icon, d.model, self.disk_subtitle(d)))

        # Übersicht: drei Kacheln
        g = Adw.PreferencesGroup()
        tiles = Gtk.Box(spacing=12, homogeneous=True)

        def tile(caption, value, css, sub=""):
            b = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
            b.add_css_class("card")
            inner = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=2, margin_top=14, margin_bottom=14,
                            margin_start=16, margin_end=16)
            inner.append(label(caption, css=("caption-heading", "dim-label")))
            v = label(value, css=("title-1", css) if css else ("title-1",))
            inner.append(v)
            if sub:
                s = label(sub, css=("caption", "dim-label"), wrap=True)
                inner.append(s)
            b.append(inner)
            return b
        tiles.append(tile("GESAMTZUSTAND", h.rating, RATING_CSS.get(h.rating)))
        if h.is_ssd:
            tiles.append(tile("RESTLEBENSDAUER", f"{h.life_percent} %" if h.life_percent is not None else "–",
                              None, "laut Laufwerk"))
        else:
            tiles.append(tile("BETRIEBSSTUNDEN", hl.fmt_count(h.power_on_hours) if h.power_on_hours is not None
                              else "–", None, "Stunden eingeschaltet"))
        tiles.append(tile("TEMPERATUR", hl.fmt_temp(h.temperature), None, "aktuell"))
        g.add(tiles)
        page.add(g)

        bar_start, bar_end = [], []
        job = self.active_job_for(d)
        if not h.available:
            ng = Adw.PreferencesGroup(title="Keine Gesundheitsdaten")
            ng.set_description(h.reason or "Für dieses Laufwerk sind keine Daten verfügbar.")
            page.add(ng)
        else:
            rg = Adw.PreferencesGroup(title="Bewertung")
            for reason in h.reasons:
                r = row(reason)
                r.set_title_lines(0)
                icon = "object-select-symbolic" if h.rating == hl.VERY_GOOD else "dialog-information-symbolic"
                if h.rating in (hl.CAUTION, hl.BAD):
                    icon = "dialog-warning-symbolic"
                img = Gtk.Image.new_from_icon_name(icon)
                img.add_css_class(RATING_CSS.get(h.rating, "dim-label"))
                r.add_prefix(img)
                rg.add(r)
            page.add(rg)

            kg = Adw.PreferencesGroup(title="Kennwerte")
            for k, v in hl.key_values(h)[1:]:
                css = None
                if k.startswith(("Ausstehende", "Unkorrigierbare")) and v not in ("0", "–") and not v.startswith("–"):
                    css = "error" if k.startswith("Unkorrigierbare") else "warning"
                kg.add(value_row(k, v, css))
            page.add(kg)

        # Tests
        tg = Adw.PreferencesGroup(title="Tests")
        tg.set_description("Selbsttests führt das Laufwerk selbst aus. Der Oberflächentest liest jeden Sektor. "
                           "Keiner der Tests verändert Daten.")
        tests = []
        if h.available and h.supports_self_tests:
            tests.append(("selftest-short", "Kurztest",
                          f"Elektronik und Teile der Oberfläche · ca. {h.short_minutes or 2} Min."))
            tests.append(("selftest-long", "Langtest",
                          f"Gesamte Oberfläche durch das Laufwerk · ca. {self.fmt_minutes(h.long_minutes)}"))
            if h.supports_conveyance:
                tests.append(("selftest-conveyance", "Transporttest", "Transportschäden erkennen · ca. 2 Min."))
        tests.append(("scan", "Oberflächentest",
                      f"WipeKit liest jeden Sektor und misst die Geschwindigkeit · ca. "
                      f"{fmt_short_duration(d.size / (400e6 if d.is_ssd else 130e6))}"))
        for key, title, sub in tests:
            r = row(title, sub)
            b = Gtk.Button(label="Starten", valign=Gtk.Align.CENTER)
            b.set_sensitive(job is None)
            if job is not None:
                b.set_tooltip_text("Auf diesem Laufwerk läuft bereits ein Vorgang.")
            if key == "scan":
                b.connect("clicked", lambda *_: self.start_job(d, "check", purpose="scan"))
            else:
                b.connect("clicked", lambda _b, k=key: self.start_job(d, k))
            r.add_suffix(b)
            tg.add(r)
        page.add(tg)

        if h.available:
            dg = Adw.PreferencesGroup(title="Laufwerk")
            for k, v in hl.drive_details(h):
                dg.add(value_row(k, v))
            hpa = entry.get("hpa") or {}
            if hpa:
                txt = ("aktiv – " + fmt_bytes((hpa["native"] - hpa["visible"]) * 512) + " versteckt"
                       if hpa.get("enabled") and hpa["native"] > hpa["visible"] else "nicht aktiv")
                dg.add(value_row("Versteckter Bereich (HPA)", txt, "warning" if txt.startswith("aktiv") else None))
            page.add(dg)

            if h.attributes:
                ag = Adw.PreferencesGroup(title="S.M.A.R.T.-Attribute")
                ex = Adw.ExpanderRow(title=f"{len(h.attributes)} Attribute anzeigen")
                warn = sum(1 for a in h.attributes if a.status != "OK")
                ex.set_subtitle("alle unauffällig" if not warn else f"{warn} auffällig")
                for a in h.attributes:
                    r = row(f"{a.id:>3} · {a.name}",
                            f"Wert {a.value} · Schlechtester {a.worst} · Grenzwert {a.thresh} · Rohwert {a.raw_string}")
                    st = label(a.status, css=({"OK": "dim-label", "Warnung": "warning", "Fehler": "error"}[a.status],))
                    st.set_valign(Gtk.Align.CENTER)
                    r.add_suffix(st)
                    ex.add_row(r)
                ex.set_expanded(warn > 0)
                ag.add(ex)
                page.add(ag)
            if h.nvme:
                ng = Adw.PreferencesGroup(title="NVMe-Gesundheitswerte")
                ex = Adw.ExpanderRow(title="Alle Werte anzeigen")
                for key, lab in hl.NVME_LABELS:
                    if key in h.nvme:
                        v = h.nvme[key]
                        ex.add_row(value_row(lab, hl.fmt_count(v) if isinstance(v, int) else str(v)))
                ng.add(ex)
                page.add(ng)
            if h.self_tests:
                sg = Adw.PreferencesGroup(title="Selbsttest-Protokoll des Laufwerks")
                for t in h.self_tests[:8]:
                    r = row(t.kind, t.result)
                    r.add_suffix(label(f"Std. {hl.fmt_count(t.hours)}" if t.hours is not None else "",
                                       css=("dim-label", "numeric")))
                    sg.add(r)
                page.add(sg)

        bar_start.append(label(f"Gelesen {fmt_clock(entry['t'])}", css=("dim-label",)))
        upd = Gtk.Button(label="Aktualisieren")
        upd.connect("clicked", lambda *_: self.read_health())
        save = Gtk.Button(label="Bericht speichern")
        save.add_css_class("suggested-action")
        save.connect("clicked", lambda *_: self.save_health_report(d))
        bar_end += [save, upd]
        return self.with_action_bar(page, bar_start, bar_end)

    @staticmethod
    def fmt_minutes(m):
        if not m:
            return "1–2 Std."
        return fmt_short_duration(m * 60)

    def save_health_report(self, d: Disk):
        entry = self.health.get(d.name)
        h = entry["h"] if entry else None
        ident = rp.identity_from(h, d.model, d.size, d.serial)
        r = rp.health_report(h, ident, VERSION, os_label(), socket.gethostname(), meta=SETTINGS.meta)
        try:
            pdf, txt = rp.save(r, reports_folder())
        except OSError as e:
            self.inform("Bericht konnte nicht gespeichert werden", str(e))
            return
        self.toast(f"Gesundheitsbericht gespeichert: {pdf.name}", "Öffnen", lambda: open_path(pdf))

    # Vorgang ------------------------------------------------------------------------------

    def build_job_page(self, j: Job):
        page = Adw.PreferencesPage()
        refs = {"job": j, "state": j.state}

        hero = Adw.PreferencesGroup()
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=14, halign=Gtk.Align.CENTER)
        box.set_margin_top(12)
        box.set_margin_bottom(12)
        overlay = Gtk.Overlay()
        ring = Ring(150, 10)
        overlay.set_child(ring)
        center = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, halign=Gtk.Align.CENTER, valign=Gtk.Align.CENTER)
        pct = label("", css=("title-1", "numeric"), xalign=0.5)
        step = label("", css=("dim-label", "caption"), xalign=0.5)
        result_icon = Gtk.Image()
        result_icon.set_pixel_size(52)
        center.append(pct)
        center.append(result_icon)
        center.append(step)
        overlay.add_overlay(center)
        headline = label("", css=("title-2",), xalign=0.5)
        subline = label("", css=("dim-label",), xalign=0.5, wrap=True)
        subline.set_justify(Gtk.Justification.CENTER)
        for w in (overlay, headline, subline):
            box.append(w)
        hero.add(box)
        page.add(hero)
        refs.update(ring=ring, pct=pct, step=step, headline=headline, subline=subline, result_icon=result_icon)

        if j.kind == "check":
            cg = Adw.PreferencesGroup(title="Lesegeschwindigkeit")
            curve = Curve()
            frame = Gtk.Box(margin_top=6, margin_bottom=6)
            frame.add_css_class("card")
            curve.set_margin_top(10)
            curve.set_margin_bottom(6)
            curve.set_margin_start(6)
            curve.set_margin_end(10)
            frame.append(curve)
            cg.add(frame)
            page.add(cg)
            refs["curve"] = curve
            fg = Adw.PreferencesGroup(title="Befund")
            refs["r_bad"] = value_row("Unlesbare Bereiche", "–")
            refs["r_data"] = value_row("Daten gefunden", "–")
            fg.add(refs["r_bad"])
            fg.add(refs["r_data"])
            page.add(fg)

        bar_start, bar_end = [], []
        if j.active:
            prog = Adw.PreferencesGroup(title="Fortschritt")
            keys = [("r_step", "Schritt")]
            if j.kind != "selftest" and not j.mode.purge:
                keys += [("r_done", "Verarbeitet"), ("r_speed", "Geschwindigkeit"), ("r_eta", "Verbleibend"),
                         ("r_end", "Voraussichtlich fertig")]
            keys.append(("r_elapsed", "Läuft seit"))
            for key, title in keys:
                r = row(title, "–", prop=True)
                prog.add(r)
                refs[key] = r
            page.add(prog)
            sleep = Adw.PreferencesGroup(description="Sobald der Vorgang beendet ist, geht der Rechner wieder "
                                                     "wie gewohnt in den Ruhezustand.")
            r = row("Ruhezustand", "–", prop=True)
            sleep.add(r)
            refs["r_sleep"] = r
            page.add(sleep)

            hint = label("Du kannst WipeKit schließen – der Vorgang läuft im Hintergrund weiter.", css=("dim-label",))
            hint.set_ellipsize(Pango.EllipsizeMode.END)
            bar_start.append(hint)
            cancel = Gtk.Button(label="Abbrechen …")
            body = ("Das Laufwerk ist danach nur teilweise überschrieben." if j.kind == "wipe"
                    else "Der Vorgang wird beendet. Es werden keine Daten verändert.")
            cancel.connect("clicked", lambda *_: self.ask(
                f"{j.title} abbrechen?", body, "Abbrechen bestätigen",
                lambda: (j.request_cancel(), self.update_job_page())))
            if j.mode.purge:
                cancel.set_sensitive(False)
                cancel.set_tooltip_text("Eine laufende Firmware-Löschung kann nicht abgebrochen werden.")
            bar_end.append(cancel)
            refs["cancel"] = cancel
        else:
            res = Adw.PreferencesGroup(title="Ergebnis")
            res.set_description(("⚠ " if j.state == "failed" else "") + (j.message or "–"))
            s = j.status
            if j.elapsed is not None:
                res.add(value_row("Dauer", fmt_duration(j.elapsed)))
            if j.kind == "wipe":
                if j.mode.verify == "full":
                    v = s.verified if s else None
                    res.add(value_row("Prüfung", {True: "Bestanden", False: "Nicht bestanden"}.get(v, "Nicht durchgeführt")))
                if s and s.sampleResult:
                    res.add(value_row("Stichproben" if not j.mode.purge else "Prüfmarken", s.sampleResult))
                if s and s.purgeMethod:
                    res.add(value_row("Firmware-Befehl", s.purgeMethod))
                if j.mode.trims:
                    res.add(value_row("TRIM", (s.trimResult if s else None) or "Nicht durchgeführt"))
                if s and s.hiddenArea:
                    res.add(value_row("Versteckte Bereiche", s.hiddenArea))
            if s and s.finishedAt:
                res.add(value_row("Beendet", fmt_datetime(s.finishedAt)))
            page.add(res)

            remove = Gtk.Button(label="Entfernen")
            remove.connect("clicked", lambda *_: self.remove_job(j))
            bar_end.append(remove)
            if j.reports.get("pdf"):
                pg = Adw.PreferencesGroup(title="Protokolle")
                pg.set_description(f"Gespeichert in {reports_folder()} (Unterordner »PDF« und »Text«).")
                items = [("pdf", j.reports.get("title", "Protokoll")), ("pdf_full", "Ausführliches Protokoll")]
                for key, title in items:
                    p = j.reports.get(key)
                    if not p:
                        continue
                    r = row(title, Path(p).name)
                    r.set_subtitle_lines(1)
                    ob = Gtk.Button(label="PDF öffnen", valign=Gtk.Align.CENTER)
                    ob.connect("clicked", lambda _b, p=p: open_path(Path(p)))
                    tb = Gtk.Button(label="Text", valign=Gtk.Align.CENTER)
                    tb.connect("clicked", lambda _b, p=j.reports.get(key.replace("pdf", "txt")): open_path(Path(p)))
                    r.add_suffix(ob)
                    r.add_suffix(tb)
                    pg.add(r)
                page.add(pg)
                fb = Gtk.Button(label="Ordner öffnen")
                fb.connect("clicked", lambda *_: self.on_open_reports())
                bar_start.append(fb)
                op = Gtk.Button(label="Protokoll öffnen")
                op.add_css_class("suggested-action")
                op.connect("clicked", lambda *_: open_path(j.protocol))
                bar_end.insert(0, op)
            elif j.kind == "selftest":
                db = Gtk.Button(label="Zur Diagnose")
                db.add_css_class("suggested-action")
                db.connect("clicked", lambda *_: self.go_to_diag(j.disk.name))
                bar_end.insert(0, db)

        info = Adw.PreferencesGroup(title="Laufwerk")
        info.add(value_row("Name", j.disk.model))
        info.add(value_row("Gerät", j.disk.path))
        info.add(value_row("Kapazität", fmt_bytes(j.disk.size)))
        info.add(value_row("Vorgang", f"{j.title if j.kind != 'wipe' else j.mode.title} · {j.mode.tagline}"))
        page.add(info)

        self.job_page_refs = refs
        self.update_job_page()
        return self.with_action_bar(page, bar_start, bar_end)

    def go_to_diag(self, name):
        if self.find_disk(name):
            self.select(("disk", name))
            self.view = "diag"
            self.stack.set_visible_child_name("diag")
            self.show_detail()

    def update_job_page(self):
        refs = self.job_page_refs
        j = refs.get("job")
        if not j:
            return
        refs["ring"].update(1.0 if j.state == "done" else j.fraction, j.color)
        s = j.status
        icon = refs["result_icon"]
        if j.active:
            refs["pct"].set_visible(True)
            icon.set_visible(False)
            refs["pct"].set_label(fmt_percent(j.fraction))
            refs["step"].set_label(f"Schritt {s.step} von {s.stepCount}" if s and s.state == "running"
                                   and s.stepCount > 1 else "")
        else:
            refs["pct"].set_visible(False)
            icon.set_visible(True)
            bad = j.state == "done" and j.color == COLORS["failed"]
            icon.set_from_icon_name("dialog-warning-symbolic" if bad else {
                "done": "object-select-symbolic", "failed": "window-close-symbolic"}.get(
                j.state, "media-playback-stop-symbolic"))
            for c in ("success", "error", "warning"):
                icon.remove_css_class(c)
            icon.add_css_class("error" if bad else {"done": "success", "failed": "error"}.get(j.state, "warning"))
            refs["step"].set_label("")
        running = {"wipe": "Wird gelöscht", "check": f"{j.title} läuft", "selftest": f"{j.title} läuft"}[j.kind]
        done = {"wipe": "Löschen abgeschlossen", "check": f"{j.title} abgeschlossen",
                "selftest": f"{j.title} abgeschlossen"}[j.kind]
        refs["headline"].set_label({"starting": "Wird vorbereitet", "running": running, "done": done,
                                    "failed": f"{j.title} fehlgeschlagen",
                                    "cancelled": f"{j.title} abgebrochen"}[j.state])
        if j.state == "starting":
            refs["subline"].set_label(s.phase if s else "Bitte das Administrator-Passwort eingeben.")
        elif j.state == "running":
            refs["subline"].set_label(s.phase if s else "")
        elif j.state == "done":
            refs["subline"].set_label(f"»{j.disk.model}« wurde sicher gelöscht." if j.kind == "wipe"
                                      else (j.message or j.disk.model))
        else:
            refs["subline"].set_label(j.disk.model)

        if "curve" in refs and s is not None:
            refs["curve"].update(s.curve or [], curve_total(s.diskSize or j.disk.size))
            self._set_value(refs["r_bad"], "keine" if not s.badBlocks else f"{s.badBlocks} (je 64 KiB)",
                            "error" if s.badBlocks else None)
            if s.nonZeroBytes is not None:
                self._set_value(refs["r_data"], "keine – nur Nullen" if s.nonZeroBytes == 0 else
                                f"{fmt_bytes(s.nonZeroBytes)} enthalten Daten")

        if j.active and "r_step" in refs:
            if s and s.state == "running":
                refs["r_step"].set_subtitle(f"{s.step} von {s.stepCount} · {s.phase}" if s.stepCount > 1 else s.phase)
                if "r_done" in refs:
                    refs["r_done"].set_subtitle(f"{fmt_bytes(s.bytesDoneTotal)} von {fmt_bytes(s.bytesTotal)}")
                    refs["r_speed"].set_subtitle(fmt_speed(s.speed) if s.speed > 0 else "–")
                    eta = j.eta
                    refs["r_eta"].set_subtitle(f"ca. {fmt_duration(eta)}" if eta else "Wird berechnet …")
                    refs["r_end"].set_subtitle(fmt_clock(time.time() + eta) if eta else "–")
                refs["r_elapsed"].set_subtitle(fmt_duration(j.elapsed) if j.elapsed else "–")
            else:
                refs["r_step"].set_subtitle(s.phase if s else "Warte auf Administrator-Passwort …")
            refs["r_sleep"].set_subtitle("Verhindert – Bildschirm darf ausgehen"
                                         if s and s.sleepPrevented else "Wird vorbereitet …")
            if j.cancel_requested:
                refs["cancel"].set_label("Wird abgebrochen …")
                refs["cancel"].set_sensitive(False)
            elif not j.mode.purge:
                refs["cancel"].set_sensitive(s is not None)

    @staticmethod
    def _set_value(r, text, css=None):
        lab = getattr(r, "value_label", None)
        if lab is None:
            return
        lab.set_label(text)
        for c in ("error", "warning", "dim-label"):
            lab.remove_css_class(c)
        lab.add_css_class(css or "dim-label")

    # --- Zyklus ---------------------------------------------------------------------------

    def tick(self):
        changed = False
        for j in list(self.jobs):
            if j.active and j.poll():
                changed = True
                if j.dismissed:
                    self.remove_job(j, silent=True)
                    continue
                if not j.active:
                    self.job_finished(j)
        if changed:
            self.rebuild_job_list()
            self.rebuild_disk_list()
            self.update_sleep_status()
            if self.selection and self.selection[0] == "job":
                self.show_detail()
        else:
            self.update_job_rows()
            self.update_job_page()
            for j in self.jobs:
                ring = self.disk_rings.get(j.disk.name)
                if ring is not None and j.active:
                    ring.update(j.fraction, COLORS["accent"])
        return True

    def remove_job(self, j: Job, silent=False):
        if j.active and not j.dismissed:
            return
        self.jobs = [x for x in self.jobs if x.id != j.id]
        shutil.rmtree(j.dir, ignore_errors=True)
        if self.selection == ("job", j.id):
            self.selection = None
            first = next((d for d in self.disks if not d.is_protected and not self.active_job_for(d)), None)
            if first:
                self.select(("disk", first.name))
        self.rebuild_job_list()
        self.rebuild_disk_list()
        self.update_sleep_status()
        self.show_detail()

    def restore_jobs(self):
        if not BASE_DIR.exists():
            return
        jobs = []
        for d in BASE_DIR.iterdir():
            try:
                info = json.loads((d / "job.json").read_text())
            except (OSError, ValueError):
                continue
            if not (d / "status.json").exists():
                if time.time() - info.get("createdAt", 0) > 120:
                    shutil.rmtree(d, ignore_errors=True)
                continue
            try:
                j = Job(info, d)
            except (KeyError, TypeError):
                continue
            j.poll()
            if not j.active and not j.reported:
                self.job_finished(j, notify=False)
            jobs.append(j)
        jobs.sort(key=lambda x: x.info.get("createdAt", 0), reverse=True)
        self.jobs = jobs
        self.rebuild_job_list()
        running = next((j for j in jobs if j.active), None)
        if running:
            self.select(("job", running.id))
            self.show_detail()

    # --- Abschluss & Protokolle -----------------------------------------------------------

    def job_finished(self, j: Job, notify=True):
        if j.reported:
            return
        j.reported = True
        after = load_health(j.dir / "smart-after.json")
        if after is not None and after.available:
            prev = self.health.get(j.disk.name) or {}
            self.health[j.disk.name] = {"h": after, "hpa": prev.get("hpa") or {}, "t": time.time()}
        fin = None
        try:
            fin = json.loads((j.dir / jobfinish.FINISH_FILE).read_text())
        except (OSError, ValueError):
            fin = None
        if j.kind != "selftest":
            try:
                j.reports = self.collect_reports(j, fin) if fin and fin.get("files") else self.write_reports(j)
            except Exception as e:  # Protokoll darf den Ablauf nie stören
                j.reports = {"error": str(e)}
        if fin and fin.get("notifiedLocal"):
            notify = False   # hat sich schon selbst gemeldet, als WipeKit geschlossen war
        try:
            (j.dir / "reports.json").write_text(json.dumps(j.reports))
        except OSError:
            pass
        d = j.disk
        if j.state == "done":
            title = {"wipe": "Löschen abgeschlossen"}.get(j.kind, f"{j.title} abgeschlossen")
            text = (f"{d.model} ({d.path}, {fmt_bytes(d.size)}) wurde erfolgreich gelöscht." if j.kind == "wipe"
                    else f"{d.model}: {j.message or j.done_text}")
            if j.elapsed:
                text += f"\nDauer: {fmt_duration(j.elapsed)}"
            if j.kind == "wipe" and j.status and j.status.verified:
                text += ("\nPrüfung: bestanden – jeder Sektor ist überschrieben." if j.mode.verify == "full"
                         else f"\nKontrolle: {j.status.sampleResult}")
            if j.mode.trims and j.status and j.status.trimResult:
                text += f"\nTRIM: {j.status.trimResult}"
        elif j.state == "cancelled":
            title = f"{j.title} abgebrochen"
            text = f"{d.model} ({d.path}): {j.message or ''}"
        else:
            title = f"{j.title} fehlgeschlagen"
            text = f"{d.model} ({d.path}): {j.message or 'Unbekannter Fehler'}"
        if fin and fin.get("push") not in (None, "ok"):
            text += f"\n\nBenachrichtigung aufs Handy fehlgeschlagen: {fin['push']}"
        if notify:
            if SETTINGS["notifyLocal"]:
                n = Gio.Notification.new(title)
                n.set_body(text)
                self.app.send_notification(j.id, n)
            if SETTINGS["playSound"] and shutil.which("canberra-gtk-play"):
                event = "complete" if j.state == "done" else "dialog-warning"
                subprocess.Popen(["canberra-gtk-play", "-i", event], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            self.inform(title, text)

    def collect_reports(self, j: Job, fin: dict) -> dict:
        """Vom Arbeitsprozess erstellte Protokolle in den Berichte-Ordner kopieren"""
        folder = reports_folder()
        out = {}
        for i, f in enumerate(fin.get("files", [])):
            src_p, src_t = Path(f["pdf"]), Path(f["txt"])
            (folder / "PDF").mkdir(parents=True, exist_ok=True)
            (folder / "Text").mkdir(parents=True, exist_ok=True)
            dp = rp.unique(folder / "PDF" / src_p.name)
            dt = rp.unique(folder / "Text" / src_t.name)
            shutil.copyfile(src_p, dp)
            shutil.copyfile(src_t, dt)
            if i == 0:
                out.update(pdf=str(dp), txt=str(dt), title=f["title"])
            else:
                out.update(pdf_full=str(dp), txt_full=str(dt))
        return out

    def write_reports(self, j: Job) -> dict:
        s = j.status
        before = load_health(j.dir / "smart-before.json")
        after = load_health(j.dir / "smart-after.json")
        settings = SETTINGS.job_settings(socket.gethostname(), os_label(), VERSION)
        out = {}
        for i, (title, r) in enumerate(jobfinish.build_reports(j.info, s, j.state, j.message, before, after, settings)):
            pdf, txt = rp.save(r, reports_folder(), serial=SETTINGS["serialInFileName"])
            if i == 0:
                out.update(pdf=str(pdf), txt=str(txt), title=title)
            else:
                out.update(pdf_full=str(pdf), txt_full=str(txt))
        return out

    # --- Sonstiges ---------------------------------------------------------------------------

    # --- Einstellungen und Updates ------------------------------------------------------

    def show_preferences(self, page=0):
        win = Adw.PreferencesWindow(transient_for=self, modal=True, title="Einstellungen")
        win.set_default_size(620, 680)
        st = SETTINGS

        def switch(group, title, key, subtitle=None):
            row = Adw.SwitchRow(title=title)
            if subtitle:
                row.set_subtitle(subtitle)
            row.set_active(bool(st[key]))
            row.connect("notify::active", lambda r, *_: st.__setitem__(key, r.get_active()))
            group.add(row)
            return row

        def entry(group, title, key):
            row = Adw.EntryRow(title=title)
            row.set_text(st[key] or "")
            row.connect("changed", lambda r: st.__setitem__(key, r.get_text()))
            group.add(row)
            return row

        # Allgemein
        general = Adw.PreferencesPage(title="Allgemein", icon_name="preferences-system-symbolic")
        g = Adw.PreferencesGroup(title="Mitteilungen",
                                 description="Auch wenn WipeKit geschlossen ist, meldet sich der Vorgang beim Ende.")
        switch(g, "Mitteilung, wenn ein Vorgang endet", "notifyLocal")
        switch(g, "Ton abspielen", "playSound")
        general.add(g)
        g = Adw.PreferencesGroup(title="Löschen")
        keys = [m.key for m in MODES if not m.read_only and not m.key.startswith("selftest")]
        combo = Adw.ComboRow(title="Vorausgewählte Methode",
                             model=Gtk.StringList.new([MODES_BY_KEY[k].title for k in keys]))
        combo.set_selected(keys.index(st["defaultMode"]) if st["defaultMode"] in keys else keys.index("secure"))
        combo.connect("notify::selected", lambda r, *_: st.__setitem__("defaultMode", keys[r.get_selected()]))
        g.add(combo)
        general.add(g)
        g = Adw.PreferencesGroup()
        reset = Adw.ActionRow(title="Alle Einstellungen zurücksetzen",
                              subtitle="Auch gespeicherte Zugangsdaten. Berichte bleiben erhalten.")
        btn = Gtk.Button(label="Zurücksetzen", valign=Gtk.Align.CENTER, css_classes=["destructive-action"])
        btn.connect("clicked", lambda *_: (st.reset(), win.close()))
        reset.add_suffix(btn)
        g.add(reset)
        general.add(g)
        win.add(general)

        # Berichte
        rep = Adw.PreferencesPage(title="Berichte", icon_name="x-office-document-symbolic")
        g = Adw.PreferencesGroup(title="Speicherort", description="In den Unterordnern »PDF« und »Text«.")
        where = Adw.ActionRow(title=str(reports_folder()))
        choose = Gtk.Button(label="Ändern …", valign=Gtk.Align.CENTER)

        def pick(*_):
            dlg = Gtk.FileDialog(title="Ordner für Berichte")
            dlg.set_initial_folder(Gio.File.new_for_path(str(reports_folder())))

            def done(d, res):
                try:
                    f = d.select_folder_finish(res)
                except GLib.Error:
                    return
                if f and f.get_path():
                    st["reportsPath"] = f.get_path()
                    where.set_title(f.get_path())
            dlg.select_folder(win, None, done)
        choose.connect("clicked", pick)
        where.add_suffix(choose)
        g.add(where)
        switch(g, "Ausführliches Löschprotokoll zusätzlich", "detailedReport")
        switch(g, "Seriennummer im Dateinamen", "serialInFileName")
        rep.add(g)
        g = Adw.PreferencesGroup(title="Angaben im Protokoll")
        entry(g, "Durchgeführt von", "operatorName")
        entry(g, "Firma / Organisation", "organization")
        entry(g, "Notiz (z. B. Auftragsnummer)", "reportNote")
        rep.add(g)
        win.add(rep)

        # Benachrichtigungen
        push_page = self._push_page(win)
        win.add(push_page)

        # Updates
        upd = Adw.PreferencesPage(title="Updates", icon_name="software-update-available-symbolic")
        g = Adw.PreferencesGroup(title="Updates",
                                 description="Updates kommen aus den Releases auf GitHub und werden vor der Installation "
                                             "anhand ihrer digitalen Signatur geprüft. Während ein Vorgang läuft, wird "
                                             "nichts installiert.")
        status_row = Adw.ActionRow(title=f"Installiert: WipeKit {VERSION}", subtitle=self.update_status_text)
        check = Gtk.Button(label="Jetzt suchen", valign=Gtk.Align.CENTER)
        install = Gtk.Button(label="Installieren", valign=Gtk.Align.CENTER, css_classes=["suggested-action"], visible=False)

        def do_check(*_):
            check.set_sensitive(False)
            status_row.set_subtitle("Suche nach Updates …")

            def worker():
                r, text = updater.check()

                def ui():
                    self.update_status_text = text
                    status_row.set_subtitle(text)
                    check.set_sensitive(True)
                    install.set_visible(updater.can_install(r))
                    if r:
                        install.set_label(f"Version {r['version']} installieren")
                        install.release = r
                    return False
                GLib.idle_add(ui)
            import threading
            threading.Thread(target=worker, daemon=True).start()

        def do_install(*_):
            r = getattr(install, "release", None)
            if r:
                win.close()
                self.install_update(r)
        check.connect("clicked", do_check)
        install.connect("clicked", do_install)
        status_row.add_suffix(install)
        status_row.add_suffix(check)
        g.add(status_row)
        auto = switch(g, "Beim Start automatisch suchen", "autoCheck")
        inst = switch(g, "Updates automatisch installieren", "autoInstall")
        inst.set_sensitive(st["autoCheck"])
        auto.connect("notify::active", lambda r, *_: inst.set_sensitive(r.get_active()))
        upd.add(g)
        win.add(upd)

        pages = [general, rep, push_page, upd]
        if 0 <= page < len(pages):
            win.set_visible_page(pages[page])
        win.present()
        return win

    def _push_page(self, win):
        st = SETTINGS
        c = st.push
        page = Adw.PreferencesPage(title="Benachrichtigungen", icon_name="preferences-system-notifications-symbolic")
        g = Adw.PreferencesGroup(title="Aufs Handy", description="Eine Nachricht, wenn ein Vorgang endet – auch wenn "
                                                                   "WipeKit geschlossen ist.")
        services = [("off", "Aus"), ("hub", "WipeKit Hub (selbst gehostet)"), ("ntfy", "ntfy"), ("discord", "Discord")]
        combo = Adw.ComboRow(title="Dienst", model=Gtk.StringList.new([t for _, t in services]))
        combo.set_selected(next((i for i, (k, _) in enumerate(services) if k == c["service"]), 0))
        g.add(combo)
        page.add(g)
        detail = Adw.PreferencesGroup()
        events = Adw.PreferencesGroup(title="Benachrichtigen bei")
        page.add(detail)
        page.add(events)
        rows = []

        def save():
            st.save()

        def text_row(title, key, secret=False):
            row = Adw.PasswordEntryRow(title=title) if secret else Adw.EntryRow(title=title)
            row.set_text(c.get(key, ""))
            row.connect("changed", lambda r: (c.__setitem__(key, r.get_text().strip()), save()))
            detail.add(row)
            rows.append((detail, row))
            return row

        def fill():
            for grp, row in rows:
                grp.remove(row)
            rows.clear()
            svc = c["service"]
            detail.set_title({"hub": "WipeKit Hub", "ntfy": "ntfy", "discord": "Discord"}.get(svc, ""))
            detail.set_description({
                "hub": "Protokolle und Laufwerksdaten werden auf diesem Rechner verschlüsselt. Nur dein Browser kann sie "
                       "mit deinem Hub-Passwort öffnen – der Server selbst nicht. Lesbar ist für ihn nur die kurze "
                       "Mitteilung. Den Kopplungscode gibt es im Hub unter »Geräte«.",
                "ntfy": "Gesendet wird nur die kurze Mitteilung, keine Protokolle. Auf dem öffentlichen Server ntfy.sh "
                        "kann jeder mitlesen, der das Thema kennt – wähle einen langen, zufälligen Namen oder einen "
                        "eigenen Server mit Anmeldung. Auch ein eigener Server leitet für das iPhone einen Weckruf "
                        "(ohne Inhalt) über ntfy.sh.",
                "discord": "Gesendet wird nur die kurze Mitteilung, keine Protokolle – Discord-Nachrichten sind nicht "
                           "Ende-zu-Ende-verschlüsselt. Den Webhook legst du in Discord unter Kanal bearbeiten › "
                           "Integrationen an.",
            }.get(svc, ""))
            if svc == "hub":
                text_row("Kopplungscode (WKHUB1:…)", "hubCode", secret=True)
                sw = Adw.SwitchRow(title="Protokolle (PDF) mitsenden", active=c["hubFiles"])
                sw.connect("notify::active", lambda r, *_: (c.__setitem__("hubFiles", r.get_active()), save()))
                detail.add(sw)
                rows.append((detail, sw))
            elif svc == "ntfy":
                text_row("Server", "ntfyServer")
                text_row("Thema (langer, zufälliger Name)", "ntfyTopic")
                auths = [("none", "Keine Anmeldung"), ("token", "Zugriffstoken"), ("password", "Benutzer und Passwort")]
                ac = Adw.ComboRow(title="Anmeldung", model=Gtk.StringList.new([t for _, t in auths]))
                ac.set_selected(next((i for i, (k, _) in enumerate(auths) if k == c["ntfyAuth"]), 0))
                ac.connect("notify::selected", lambda r, *_: (c.__setitem__("ntfyAuth", auths[r.get_selected()][0]),
                                                               save(), fill()))
                detail.add(ac)
                rows.append((detail, ac))
                if c["ntfyAuth"] == "password":
                    text_row("Benutzer", "ntfyUser")
                if c["ntfyAuth"] != "none":
                    text_row("Token" if c["ntfyAuth"] == "token" else "Passwort", "ntfySecret", secret=True)
            elif svc == "discord":
                text_row("Webhook-Adresse", "discordWebhook", secret=True)
            if svc == "off":
                return
            for key, title in (("onDone", "Erfolgreich beendet"), ("onFailed", "Fehlgeschlagen"),
                               ("onCancelled", "Abgebrochen"), ("onWarning", "Gesundheitswarnung")):
                sw = Adw.SwitchRow(title=title, active=bool(c[key]))
                sw.connect("notify::active", lambda r, *_, k=key: (c.__setitem__(k, r.get_active()), save()))
                events.add(sw)
                rows.append((events, sw))
            test = Adw.ActionRow(title="Testnachricht", subtitle="")
            b = Gtk.Button(label="Senden", valign=Gtk.Align.CENTER)

            def send(*_):
                save()
                if not push.is_configured(c):
                    test.set_subtitle("Bitte zuerst alle Angaben ausfüllen.")
                    return
                b.set_sensitive(False)
                test.set_subtitle("Wird gesendet …")

                def worker():
                    ev = {"kind": "test", "state": "done", "warning": False,
                          "notice": f"Testnachricht von WipeKit auf »{socket.gethostname()}« – alles eingerichtet.",
                          "meta": {"host": socket.gethostname(), "platform": os_label(), "app": VERSION}, "files": []}
                    r = push.send(c, ev, force=True)
                    GLib.idle_add(lambda: (test.set_subtitle(r or "Gesendet."), b.set_sensitive(True), False)[-1])
                import threading
                threading.Thread(target=worker, daemon=True).start()
            b.connect("clicked", send)
            test.add_suffix(b)
            events.add(test)
            rows.append((events, test))

        def on_service(r, *_):
            c["service"] = services[r.get_selected()][0]
            save()
            fill()
        combo.connect("notify::selected", on_service)
        fill()
        return page

    update_status_text = "Noch nicht gesucht."

    def updates_on_launch(self):
        if not SETTINGS["updatesAsked"]:
            SETTINGS["updatesAsked"] = True

            def answer(yes):
                SETTINGS["autoCheck"] = yes
                SETTINGS["autoInstall"] = yes
                if yes:
                    self.check_updates(user=False)
            self.ask("Automatisch nach Updates suchen?",
                     "WipeKit kann bei jedem Start auf GitHub nach einer neuen Version suchen und sie installieren. "
                     "Updates werden vorher anhand ihrer Signatur geprüft. Das lässt sich jederzeit in den "
                     "Einstellungen ändern.", "Automatisch suchen", lambda: answer(True), destructive=False)
            return False
        if SETTINGS["autoCheck"]:
            self.check_updates(user=False)
        return False

    def check_updates(self, user=False):
        import threading

        def worker():
            r, text = updater.check()

            def ui():
                self.update_status_text = text
                if r is None:
                    if user:
                        self.inform("Keine neue Version", text)
                    return False
                if not user and SETTINGS["autoInstall"] and updater.can_install(r):
                    self.install_update(r)
                    return False
                if updater.can_install(r):
                    self.ask(f"WipeKit {r['version']} ist verfügbar", f"Installiert ist Version {VERSION}.",
                             "Installieren", lambda: self.install_update(r), destructive=False)
                else:
                    self.inform(f"WipeKit {r['version']} ist verfügbar", text + f"\n\n{r['page']}")
                return False
            GLib.idle_add(ui)
        threading.Thread(target=worker, daemon=True).start()

    def install_update(self, r):
        import threading
        if any(j.active for j in self.jobs):
            GLib.timeout_add_seconds(60, lambda: (self.install_update(r), False)[-1])
            return

        def worker():
            err = updater.install(r)

            def ui():
                if err is None:
                    # Neu starten mit der neuen Version
                    os.execv(sys.executable, [sys.executable] + sys.argv)
                elif err != "Abgebrochen.":
                    self.inform("Update fehlgeschlagen", err)
                return False
            GLib.idle_add(ui)
        threading.Thread(target=worker, daemon=True).start()

    def on_open_reports(self, *_):
        folder = reports_folder()
        folder.mkdir(parents=True, exist_ok=True)
        open_path(folder)

    def on_close_request(self, *_):
        active = sum(1 for j in self.jobs if j.active)
        if active == 0 or self.force_close:
            return False

        def really_close():
            self.force_close = True
            self.close()

        self.ask("Ein Vorgang läuft noch" if active == 1 else f"{active} Vorgänge laufen noch",
                 "Der Vorgang wird im Hintergrund fortgesetzt und der Rechner bleibt wach. "
                 "Öffne WipeKit erneut, um den Fortschritt zu sehen.", "Beenden", really_close, destructive=False)
        return True


class WipeKitApp(Adw.Application):
    def __init__(self, smoke_test=False):
        flags = (Gio.ApplicationFlags.DEFAULT_FLAGS if hasattr(Gio.ApplicationFlags, "DEFAULT_FLAGS")
                 else Gio.ApplicationFlags.FLAGS_NONE)
        if smoke_test:
            flags |= Gio.ApplicationFlags.NON_UNIQUE
        super().__init__(application_id=APP_ID, flags=flags)
        self.window = None
        self.smoke_test = smoke_test
        self.smoke_ok = False
        about = Gio.SimpleAction.new("about", None)
        about.connect("activate", lambda *_: self.show_about())
        self.add_action(about)

    def show_about(self):
        kw = dict(application_name=APP_NAME, application_icon=APP_ID, version=VERSION,
                  developer_name="mllsm66", license_type=Gtk.License.MIT_X11,
                  website="https://github.com/mllsm66/WipeKit",
                  comments="Laufwerke sicher löschen und ihre Gesundheit prüfen.")
        if hasattr(Adw, "AboutDialog"):
            dlg = Adw.AboutDialog(**kw)
        else:
            dlg = Adw.AboutWindow(transient_for=self.window, **kw)
        dlg.add_legal_section(
            "smartmontools", "Copyright © 2002–2025 Bruce Allen, Christian Franke u. a. – "
            "wird als eigenständiges Programm aufgerufen (Paket »smartmontools«).", Gtk.License.GPL_2_0, None)
        dlg.add_legal_section("hdparm", "Copyright © Mark Lord – eigenständiges Programm (Paket »hdparm«).",
                              Gtk.License.CUSTOM, "BSD-artige Lizenz des hdparm-Projekts.")
        dlg.add_legal_section("nvme-cli", "Copyright © nvme-cli-Projekt – eigenständiges Programm (Paket »nvme-cli«).",
                              Gtk.License.GPL_2_0, None)
        dlg.add_legal_section("Helvetica-Zeichenbreiten (PDF)", "Copyright © 1985–1997 Adobe Systems Incorporated. "
                              "Metrikdaten aus den Adobe Core-14-AFM-Dateien.", Gtk.License.CUSTOM,
                              "Frei verwendbar laut Adobe-Lizenzhinweis zu den AFM-Dateien.")
        if hasattr(Adw, "AboutDialog"):
            dlg.present(self.window)
        else:
            dlg.present()

    def do_activate(self):
        display = Gdk.Display.get_default()
        if display is not None and ICON_DIR.is_dir():
            Gtk.IconTheme.get_for_display(display).add_search_path(str(ICON_DIR))
        if not self.window:
            self.window = MainWindow(self)
        self.window.present()
        if self.smoke_test:
            GLib.timeout_add(500, self.run_smoke_test)

    def run_smoke_test(self):
        """Nur für die automatische Prüfung (CI): alle Ansichten einmal aufbauen."""
        import tempfile
        import traceback
        w = self.window
        shots = os.environ.get("WIPEKIT_SHOTS")
        fixtures = Path(os.environ.get("WIPEKIT_FIXTURES", HERE.parent.parent / "tests" / "fixtures"))

        def settle():
            ctx = GLib.MainContext.default()
            end = time.time() + 0.6
            while time.time() < end:
                while ctx.pending():
                    ctx.iteration(False)
                time.sleep(0.02)

        def shot(name):
            if not shots:
                return
            settle()
            try:
                os.makedirs(shots, exist_ok=True)
                paintable = Gtk.WidgetPaintable.new(w)
                snap = Gtk.Snapshot()
                paintable.snapshot(snap, w.get_width(), w.get_height())
                node = snap.to_node()
                if node is None:
                    print("Screenshot leer:", name)
                    return
                tex = w.get_renderer().render_texture(node, None)
                tex.save_to_png(os.path.join(shots, name + ".png"))
            except Exception as e:  # Screenshots sind optional
                print("Screenshot fehlgeschlagen:", name, e)

        try:
            w.disks = [
                Disk("sdx", "/dev/sdx", "Samsung SSD 970 EVO Plus 1TB", 1_000_204_886_016, "USB", True, ["Daten"],
                     [], [], False, False, "usb", "S4EWNX0R123456A"),
                Disk("sdy", "/dev/sdy", "WDC WD5000AAKX-00ERMA0", 500_107_862_016, "USB", False, [], [], [], False,
                     False, "usb", "WD-WCC2EXX12345"),
                Disk("sdz", "/dev/sdz", "Unbekannter USB-Stick", 64_000_000_000, "USB", None, [], [], [], False,
                     False, "usb", ""),
                Disk("sda", "/dev/sda", "Crucial CT500MX500SSD1", 500_107_862_016, "SATA", True, [], [], [], False,
                     True, "sata", "2034E4A1B2C3"),
                Disk("nvme0n1", "/dev/nvme0n1", "System", 256_000_000_000, "NVMe", True, [], ["/"], [], True,
                     True, "nvme", "X"),
            ]
            fx = {"sdx": "nvme", "sdy": "hdd", "sda": "ssd", "sdz": "usb-unsupported", "nvme0n1": "nvme"}
            payload = {"disks": {}}
            for name, f in fx.items():
                try:
                    payload["disks"][name] = {"smart": json.loads((fixtures / f"smartctl-{f}.json").read_text()),
                                              "hpa": {"visible": 976773168, "native": 976773168, "enabled": False}}
                except OSError:
                    pass
            w.rebuild_disk_list()
            for view in ("wipe", "diag"):
                w.set_view(view)
                for d in w.disks:
                    w.select(("disk", d.name))
                    w.show_detail()
                    if view == "wipe" and not d.is_protected:
                        for m in MODES:
                            w.mode_key = m.key
                            w.update_mode_detail(d)
            w.diag_state = "loading"
            w.show_detail()
            w.apply_diag(json.dumps(payload))
            w.rebuild_disk_list()
            w.set_view("wipe")
            w.select(("disk", "sdx"))
            w.show_detail()
            shot("linux-1-ssd")
            w.select(("disk", "sdy"))
            w.mode_key = "secure"
            w.show_detail()
            shot("linux-2-festplatte")
            for d in w.disks:
                w.select(("disk", d.name))
                w.set_view("diag")
                w.show_detail()
            w.select(("disk", "sdy"))
            w.show_detail()
            shot("linux-5-diagnose")
            w.select(("disk", "sda"))
            w.show_detail()
            shot("linux-6-diagnose-ssd")
            w.set_view("wipe")
            w.show_detail()
            tmp = Path(tempfile.mkdtemp())
            reports_env = os.environ.get("WIPEKIT_REPORTS")
            if reports_env:
                globals()["reports_folder"] = lambda: Path(reports_env)
            else:
                globals()["reports_folder"] = lambda: tmp / "reports"
            for key, state, purpose in [("ssd", "starting", ""), ("ssd", "running", ""), ("secure", "done", ""),
                                        ("quick", "failed", ""), ("ssd", "cancelled", ""), ("check", "running", "scan"),
                                        ("check", "done", "scan"), ("check", "done", ""), ("purge", "done", ""),
                                        ("selftest-long", "running", ""), ("selftest-short", "done", "")]:
                disk = w.disks[1]
                info = {"id": f"smoke-{key}-{state}-{purpose}", "disk": asdict(disk), "mode": key,
                        "createdAt": time.time() - 100, "purpose": purpose}
                jd = tmp / info["id"]
                jd.mkdir()
                for nm in ("smart-before.json", "smart-after.json"):
                    try:
                        shutil.copy(fixtures / "smartctl-hdd.json", jd / nm)
                    except OSError:
                        pass
                st = HelperStatus(state=state, pid=os.getpid(), phase="Durchgang 1: Zufallsdaten schreiben", step=1,
                                  stepCount=4, diskSize=disk.size, bytesDoneTotal=10**11, bytesTotal=15 * 10**11,
                                  speed=1.4e8, startedAt=time.time() - 100, updatedAt=time.time(), verified=True,
                                  trimResult="durchgeführt", message="Testmeldung",
                                  sampleResult="bestanden – 2080 Stellen gelesen und verglichen",
                                  purgeMethod="ATA Secure Erase (Enhanced)" if key == "purge" else None,
                                  hiddenArea="Kein versteckter Bereich (HPA) aktiv.",
                                  curve=[140 - (i * 0.35) for i in range(150)] if key == "check" else None,
                                  nonZeroBytes=0 if key == "check" else None, badBlocks=0,
                                  finishedAt=None if state in ACTIVE_STATES else time.time())
                (jd / "status.json").write_text(st.to_json())
                j = Job(info, jd)
                j.poll()
                w.jobs.insert(0, j)
                w.rebuild_job_list()
                w.select(("job", j.id))
                w.show_detail()
                w.update_job_page()
                if key == "ssd" and state == "running":
                    w.update_sleep_status()
                    shot("linux-3-loeschen")
                if key == "check" and state == "running":
                    shot("linux-7-oberflaechentest")
                if not j.active:
                    w.job_finished(j, notify=False)
                    w.show_detail()
                    if key == "secure" and state == "done":
                        shot("linux-4-fertig")
                    if j.kind != "selftest" and not j.reports.get("pdf"):
                        raise RuntimeError(f"Kein Protokoll für {info['id']}: {j.reports}")
            w.update_sleep_status()
            w.tick()
            self.smoke_ok = True
            print("SMOKE TEST OK")
        except Exception:
            traceback.print_exc()
        self.quit()
        return False


def main():
    if not hasattr(Adw, "NavigationSplitView"):
        print("WipeKit benötigt libadwaita 1.4 oder neuer (z. B. Ubuntu 24.04, Fedora 39, Debian 13).",
              file=sys.stderr)
        return 1
    if "--smoke-test" in sys.argv:
        app = WipeKitApp(smoke_test=True)
        app.run([sys.argv[0]])
        return 0 if app.smoke_ok else 1
    return WipeKitApp().run(sys.argv)


if __name__ == "__main__":
    sys.exit(main())
