#!/usr/bin/env python3
"""WipeKit für Linux – Oberfläche (GTK 4 + libadwaita ≥ 1.4)."""

import json
import math
import os
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
    ACTIVE_STATES, APP_ID, APP_NAME, MODES, MODES_BY_KEY, SEEDED, VERSION, Disk, HelperStatus,
    fmt_bytes, fmt_clock, fmt_datetime, fmt_duration, fmt_exact_bytes, fmt_percent, fmt_short_duration,
    fmt_speed, list_external_disks, pid_alive, recommended_mode,
)

HELPER = Path(__file__).resolve().parent / "wipekit-helper"
ICON_DIR = Path(__file__).resolve().parent / "icons"
BASE_DIR = Path(f"/tmp/wipekit-{os.getuid()}")

COLORS = {
    "accent": (0.21, 0.52, 0.89),
    "done": (0.18, 0.76, 0.49),
    "failed": (0.88, 0.11, 0.14),
    "cancelled": (1.0, 0.47, 0.0),
}


def protocol_folder() -> Path:
    docs = GLib.get_user_special_dir(GLib.UserDirectory.DIRECTORY_DOCUMENTS) or str(Path.home() / "Documents")
    return Path(docs) / "WipeKit-Protokolle"


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


def label(text: str = "", css=(), wrap=False, xalign=0.0) -> Gtk.Label:
    lb = Gtk.Label(label=text)
    lb.set_xalign(xalign)
    lb.set_wrap(wrap)
    for c in css:
        lb.add_css_class(c)
    return lb


# ---------------------------------------------------------------------------
# Fortschrittsring
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


# ---------------------------------------------------------------------------
# Löschvorgang (Oberflächen-Seite)
# ---------------------------------------------------------------------------

class Job:
    def __init__(self, info: dict, job_dir: Path, proc=None):
        self.info = info
        self.id = info["id"]
        self.disk = Disk(**info["disk"])
        self.mode = MODES_BY_KEY[info["mode"]]
        self.dir = job_dir
        self.proc = proc
        self.status = None
        self.state = "starting"
        self.message = None
        self.cancel_requested = False
        self.reported = (job_dir / "protocol_path").exists()
        self.protocol = None
        if self.reported:
            try:
                self.protocol = Path((job_dir / "protocol_path").read_text().strip())
            except OSError:
                pass
        self.dismissed = False

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
        if time.time() - s.startedAt < 15:
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
        return COLORS.get(self.state, COLORS["accent"])

    @property
    def short_status(self) -> str:
        if self.state == "starting":
            return "Wird gestartet …"
        if self.state == "running":
            e = self.eta
            return f"{fmt_percent(self.fraction)} · noch {fmt_short_duration(e)}" if e else fmt_percent(self.fraction)
        return {"done": "Erfolgreich gelöscht", "failed": "Fehlgeschlagen", "cancelled": "Abgebrochen"}[self.state]

    def poll(self):
        """Liest status.json. Gibt True zurück, wenn sich der Zustand geändert hat."""
        before = self.state
        try:
            self.status = HelperStatus.from_json((self.dir / "status.json").read_text())
            self.state = self.status.state
            self.message = self.status.message
            if self.active and not pid_alive(self.status.pid) and time.time() - self.status.updatedAt > 10:
                self.state = "failed"
                self.message = ("Der Löschprozess wurde unerwartet beendet (Absturz oder Neustart?). "
                                "Das Laufwerk ist nur teilweise überschrieben.")
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
                    self.message = f"Der Löschprozess konnte nicht gestartet werden (Code {code}). {log}".strip()
            elif self.proc is None and time.time() - self.info["createdAt"] > 60:
                self.state = "failed"
                self.message = "Der Löschprozess konnte nicht gestartet werden."
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
        self.set_default_size(1000, 720)
        self.set_size_request(860, 560)
        self.app = app
        self.disks = []
        self.disk_signature = None
        self.jobs = []
        self.selection = None      # ("disk", name) | ("job", id)
        self.mode_key = "secure"
        self.updating_lists = False
        self.force_close = False
        self.job_page_refs = {}
        self.job_rows = {}
        self.disk_rings = {}

        BASE_DIR.mkdir(mode=0o700, exist_ok=True)

        # --- Seitenleiste ---------------------------------------------------
        sidebar_header = Adw.HeaderBar()
        refresh = Gtk.Button(icon_name="view-refresh-symbolic", tooltip_text="Laufwerke neu einlesen")
        refresh.connect("clicked", lambda *_: self.refresh_disks(force=True))
        sidebar_header.pack_start(refresh)

        self.disk_list = Gtk.ListBox()
        self.disk_list.add_css_class("navigation-sidebar")
        self.disk_list.connect("row-selected", self.on_row_selected)
        self.job_list = Gtk.ListBox()
        self.job_list.add_css_class("navigation-sidebar")
        self.job_list.connect("row-selected", self.on_row_selected)
        self.no_disks = label("Kein Laufwerk angeschlossen", css=("dim-label",))
        self.no_disks.set_margin_start(18)
        self.no_disks.set_margin_top(6)
        self.jobs_heading = self.section_heading("Löschvorgänge")

        side_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        side_box.append(self.section_heading("Externe Laufwerke"))
        side_box.append(self.no_disks)
        side_box.append(self.disk_list)
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

        # --- Inhalt -------------------------------------------------------------
        content_header = Adw.HeaderBar()
        protocols = Gtk.Button(icon_name="folder-documents-symbolic", tooltip_text="Ordner mit den Löschprotokollen öffnen")
        protocols.connect("clicked", self.on_open_protocols)
        content_header.pack_end(protocols)
        self.content_view = Adw.ToolbarView()
        self.content_view.add_top_bar(content_header)
        self.content_page = Adw.NavigationPage(title=APP_NAME, child=self.content_view)

        split = Adw.NavigationSplitView()
        split.set_sidebar(sidebar_page)
        split.set_content(self.content_page)
        split.set_min_sidebar_width(250)
        split.set_max_sidebar_width(340)
        self.set_content(split)

        self.connect("close-request", self.on_close_request)

        self.restore_jobs()
        self.refresh_disks(force=True)
        self.update_sleep_status()
        if self.selection is None:
            self.show_detail()
        GLib.timeout_add_seconds(1, self.tick)
        GLib.timeout_add_seconds(3, self.periodic_refresh)

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

    # --- Laufwerke ------------------------------------------------------------

    def periodic_refresh(self):
        self.refresh_disks()
        return True

    def refresh_disks(self, force=False):
        disks = list_external_disks()
        sig = [(d.name, d.size, d.model, tuple(d.volumes), d.is_protected) for d in disks]
        if sig == self.disk_signature and not force:
            return
        self.disk_signature = sig
        self.disks = disks
        if self.selection and self.selection[0] == "disk" and not self.find_disk(self.selection[1]):
            self.selection = None
        if self.selection is None:
            first = next((d for d in disks if not d.is_protected and not self.active_job_for(d)), None)
            if first:
                self.select(("disk", first.name))
        self.rebuild_disk_list()
        self.show_detail()

    def rebuild_disk_list(self):
        self.updating_lists = True
        self.disk_list.remove_all()
        self.disk_rings = {}
        self.no_disks.set_visible(not self.disks)
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
            job = self.active_job_for(d)
            if d.is_protected:
                box.append(Gtk.Image.new_from_icon_name("changes-prevent-symbolic"))
            elif job:
                ring = Ring(16, 2.5)
                ring.update(job.fraction, COLORS["accent"])
                box.append(ring)
                self.disk_rings[d.name] = ring
            r.set_child(box)
            self.disk_list.append(r)
            if self.selection == r.key:
                self.disk_list.select_row(r)
        self.updating_lists = False

    # --- Löschvorgänge in der Seitenleiste -----------------------------------------

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
            t = label(j.disk.model)
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
                icon.set_from_icon_name({"done": "object-select-symbolic", "failed": "dialog-error-symbolic",
                                         "cancelled": "process-stop-symbolic"}.get(j.state, "dialog-question-symbolic"))

    def update_sleep_status(self):
        active = any(j.active for j in self.jobs)
        self.sleep_icon.set_from_icon_name("wipekit-awake-symbolic" if active else "wipekit-sleep-symbolic")
        self.sleep_label.set_label("Rechner bleibt wach" if active else "Ruhezustand wie gewohnt")

    # --- Auswahl ------------------------------------------------------------------

    def select(self, key):
        self.selection = key
        self.updating_lists = True
        for lst in (self.disk_list, self.job_list):
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
        if key and key[0] == "disk":
            d = self.find_disk(key[1])
            if d:
                if d.is_ssd is True:
                    self.mode_key = "ssd"
                elif self.mode_key == "ssd":
                    self.mode_key = "secure"

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
                widget = self.build_disk_page(d)
                self.content_page.set_title(d.model)
        if widget is None:
            self.content_page.set_title(APP_NAME)
            status = Adw.StatusPage()
            status.set_icon_name("wipekit-drive-symbolic")
            if self.disks:
                status.set_title("Kein Laufwerk ausgewählt")
                status.set_description("Wähle in der Seitenleiste das Laufwerk aus, das gelöscht werden soll.")
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
        texts.append(label(subtitle, css=("dim-label",)))
        hb.append(icon)
        hb.append(texts)
        g.add(hb)
        return g

    # Laufwerk -----------------------------------------------------------------------

    def build_disk_page(self, d: Disk):
        page = Adw.PreferencesPage()
        page.add(self.header_group(d.icon, d.model, f"{fmt_bytes(d.size)} · {d.type_label} · {d.bus}"))
        bar_start, bar_end = [], []
        job = self.active_job_for(d)

        if d.is_protected:
            g = Adw.PreferencesGroup()
            r = row("Auf diesem Laufwerk läuft das System. Es ist geschützt und kann nicht gelöscht werden.")
            r.add_prefix(Gtk.Image.new_from_icon_name("changes-prevent-symbolic"))
            g.add(r)
            page.add(g)
        elif job:
            g = Adw.PreferencesGroup()
            r = row("Dieses Laufwerk wird gerade gelöscht.")
            btn = Gtk.Button(label="Fortschritt anzeigen", valign=Gtk.Align.CENTER)
            btn.connect("clicked", lambda *_: (self.select(("job", job.id)), self.show_detail()))
            r.add_suffix(btn)
            g.add(r)
            page.add(g)
        else:
            modes = Adw.PreferencesGroup(title="Löschmethode")
            first_check = None
            rec = recommended_mode(d)
            for m in MODES:
                r = row(m.title, m.tagline)
                check = Gtk.CheckButton()
                if first_check is None:
                    first_check = check
                else:
                    check.set_group(first_check)
                check.set_active(m.key == self.mode_key)
                check.connect("toggled", self.on_mode_toggled, m.key, d)
                r.add_prefix(check)
                icon = Gtk.Image.new_from_icon_name(m.icon)
                r.add_prefix(icon)
                if m.key == rec:
                    badge = label("Empfohlen", css=("accent", "caption-heading"))
                    badge.set_valign(Gtk.Align.CENTER)
                    r.add_suffix(badge)
                r.set_activatable_widget(check)
                modes.add(r)
            page.add(modes)

            self.mode_group = Adw.PreferencesGroup()
            self.mode_best = row("Geeignet für", "", prop=True)
            self.mode_security = row("Sicherheit")
            self.mode_level = Gtk.LevelBar(min_value=0, max_value=4, valign=Gtk.Align.CENTER)
            self.mode_level.set_mode(Gtk.LevelBarMode.DISCRETE)
            self.mode_level.set_size_request(90, -1)
            self.mode_security_label = label("")
            self.mode_security.add_suffix(self.mode_level)
            self.mode_security.add_suffix(self.mode_security_label)
            self.mode_duration = row("Geschätzte Dauer", "", prop=True)
            self.mode_note = label("", css=("dim-label", "caption"), wrap=True)
            self.mode_note.set_margin_top(10)
            self.mode_hint = label("", css=("warning", "caption"), wrap=True)
            self.mode_hint.set_margin_top(6)
            for w in (self.mode_best, self.mode_security, self.mode_duration, self.mode_note, self.mode_hint):
                self.mode_group.add(w)
            page.add(self.mode_group)
            self.update_mode_detail(d)

            warn = label(f"Alle Daten auf »{d.model}« werden unwiderruflich gelöscht.", css=("dim-label",))
            warn.set_ellipsize(Pango.EllipsizeMode.END)
            btn = Gtk.Button(label="Löschen …")
            btn.add_css_class("destructive-action")
            btn.connect("clicked", lambda *_: self.confirm_wipe(d))
            bar_start.append(warn)
            bar_end.append(btn)

        info = Adw.PreferencesGroup(title="Informationen")
        info.add(row("Gerät", d.path, prop=True))
        info.add(row("Kapazität", f"{fmt_bytes(d.size)} ({fmt_exact_bytes(d.size)})", prop=True))
        info.add(row("Anschluss", d.bus, prop=True))
        info.add(row("Typ", d.type_label, prop=True))
        info.add(row("Volumes", ", ".join(d.volumes) if d.volumes else "Keine", prop=True))
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
        speed = 300e6 if d.is_ssd is True else 130e6
        self.mode_duration.set_subtitle("ca. " + fmt_short_duration(d.size * m.work_units / speed))
        self.mode_note.set_label(m.security_note)
        hint = None
        if d.is_ssd is True and m.key != "ssd":
            hint = "Dieses Laufwerk ist eine SSD – dafür ist der Modus »SSD« besser geeignet."
        elif d.is_ssd is False and m.key == "ssd":
            hint = "Dieses Laufwerk ist eine Festplatte – TRIM wird hier nicht unterstützt. »Sicher« ist die bessere Wahl."
        elif d.is_ssd is None:
            hint = "Der Laufwerkstyp wird vom USB-Adapter nicht gemeldet. Handelt es sich um eine SSD, wähle den Modus »SSD«."
        self.mode_hint.set_label("⚠ " + hint if hint else "")
        self.mode_hint.set_visible(hint is not None)

    # Bestätigen & starten --------------------------------------------------------------

    def confirm_wipe(self, d: Disk):
        m = MODES_BY_KEY[self.mode_key]
        heading = f"»{d.model}« löschen?"
        body = (f"Alle Daten auf {d.path} ({fmt_bytes(d.size)}) werden mit der Methode »{m.title}« "
                "unwiderruflich gelöscht. Dieser Vorgang kann nicht rückgängig gemacht werden.")
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
        dlg.set_default_response("cancel")
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

    def start_job(self, d: Disk, mode_key: str):
        if self.active_job_for(d):
            return
        job_id = uuid.uuid4().hex
        job_dir = BASE_DIR / job_id
        info = {"id": job_id, "disk": asdict(d), "mode": mode_key, "createdAt": time.time()}
        try:
            job_dir.mkdir(mode=0o700)
            (job_dir / "job.json").write_text(json.dumps(info))
            log = open(job_dir / "pkexec.log", "w")
            proc = subprocess.Popen(
                ["pkexec", str(HELPER), "--disk", d.name, "--mode", mode_key,
                 "--job-dir", str(job_dir), "--expected-size", str(d.size)],
                stdin=subprocess.DEVNULL, stdout=log, stderr=log, start_new_session=True,
            )
            log.close()
        except OSError as e:
            self.inform("Löschvorgang konnte nicht gestartet werden", str(e))
            return
        job = Job(info, job_dir, proc)
        self.jobs.insert(0, job)
        self.rebuild_job_list()
        self.select(("job", job.id))
        self.rebuild_disk_list()
        self.show_detail()
        self.update_sleep_status()

    # Löschvorgang -------------------------------------------------------------------------

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
        for w in (overlay, headline, subline):
            box.append(w)
        hero.add(box)
        page.add(hero)
        refs.update(ring=ring, pct=pct, step=step, headline=headline, subline=subline, result_icon=result_icon)

        bar_start, bar_end = [], []
        if j.active:
            prog = Adw.PreferencesGroup(title="Fortschritt")
            for key, title in (("r_step", "Schritt"), ("r_done", "Verarbeitet"), ("r_speed", "Geschwindigkeit"),
                               ("r_eta", "Verbleibend"), ("r_end", "Voraussichtlich fertig"), ("r_elapsed", "Läuft seit")):
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

            hint = label("Du kannst WipeKit schließen – das Löschen läuft im Hintergrund weiter.", css=("dim-label",))
            hint.set_ellipsize(Pango.EllipsizeMode.END)
            bar_start.append(hint)
            cancel = Gtk.Button(label="Abbrechen …")
            cancel.connect("clicked", lambda *_: self.ask(
                "Löschvorgang abbrechen?", "Das Laufwerk ist danach nur teilweise überschrieben.",
                "Löschvorgang abbrechen", lambda: (j.request_cancel(), self.update_job_page())))
            bar_end.append(cancel)
            refs["cancel"] = cancel
        else:
            res = Adw.PreferencesGroup(title="Ergebnis")
            res.set_description(("⚠ " if j.state == "failed" else "") + (j.message or "–"))
            if j.elapsed is not None:
                res.add(row("Dauer", fmt_duration(j.elapsed), prop=True))
            if j.mode.verifies:
                v = j.status.verified if j.status else None
                res.add(row("Prüfung", {True: "Bestanden", False: "Nicht bestanden"}.get(v, "Nicht durchgeführt"), prop=True))
            if j.mode.trims:
                res.add(row("TRIM", (j.status.trimResult if j.status else None) or "Nicht durchgeführt", prop=True))
            if j.status and j.status.finishedAt:
                res.add(row("Beendet", fmt_datetime(j.status.finishedAt), prop=True))
            page.add(res)

            remove = Gtk.Button(label="Entfernen")
            remove.connect("clicked", lambda *_: self.remove_job(j))
            bar_end.append(remove)
            if j.protocol:
                pg = Adw.PreferencesGroup(title="Protokoll")
                r = row(j.protocol.name)
                ob = Gtk.Button(label="Öffnen", valign=Gtk.Align.CENTER)
                ob.connect("clicked", lambda *_: open_path(j.protocol))
                fb = Gtk.Button(label="Ordner öffnen", valign=Gtk.Align.CENTER)
                fb.connect("clicked", lambda *_: open_path(j.protocol.parent))
                r.add_suffix(ob)
                r.add_suffix(fb)
                pg.add(r)
                page.add(pg)
                op = Gtk.Button(label="Protokoll öffnen")
                op.add_css_class("suggested-action")
                op.connect("clicked", lambda *_: open_path(j.protocol))
                bar_end.insert(0, op)

        info = Adw.PreferencesGroup(title="Laufwerk")
        info.add(row("Name", j.disk.model, prop=True))
        info.add(row("Gerät", j.disk.path, prop=True))
        info.add(row("Kapazität", fmt_bytes(j.disk.size), prop=True))
        info.add(row("Methode", f"{j.mode.title} · {j.mode.tagline}", prop=True))
        page.add(info)

        self.job_page_refs = refs
        self.update_job_page()
        return self.with_action_bar(page, bar_start, bar_end)

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
            refs["step"].set_label(f"Schritt {s.step} von {s.stepCount}" if s and s.state == "running" else "")
        else:
            refs["pct"].set_visible(False)
            icon.set_visible(True)
            icon.set_from_icon_name({"done": "object-select-symbolic", "failed": "window-close-symbolic"}.get(
                j.state, "media-playback-stop-symbolic"))
            for c in ("success", "error", "warning"):
                icon.remove_css_class(c)
            icon.add_css_class({"done": "success", "failed": "error"}.get(j.state, "warning"))
            refs["step"].set_label("")
        refs["headline"].set_label({"starting": "Wird vorbereitet", "running": "Wird gelöscht",
                                    "done": "Löschen abgeschlossen", "failed": "Löschen fehlgeschlagen",
                                    "cancelled": "Löschen abgebrochen"}[j.state])
        if j.state == "starting":
            refs["subline"].set_label(s.phase if s else "Bitte das Administrator-Passwort eingeben.")
        elif j.state == "running":
            refs["subline"].set_label(s.phase if s else "")
        elif j.state == "done":
            refs["subline"].set_label(f"»{j.disk.model}« wurde sicher gelöscht.")
        else:
            refs["subline"].set_label(j.disk.model)

        if j.active and "r_step" in refs:
            if s and s.state == "running":
                refs["r_step"].set_subtitle(f"{s.step} von {s.stepCount} · {s.phase}")
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
            else:
                refs["cancel"].set_sensitive(s is not None)

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

    # --- Abschluss & Protokoll -----------------------------------------------------------

    def job_finished(self, j: Job, notify=True):
        if j.reported:
            return
        j.reported = True
        path = self.write_protocol(j)
        if path:
            j.protocol = path
            try:
                (j.dir / "protocol_path").write_text(str(path))
            except OSError:
                pass
        d = j.disk
        if j.state == "done":
            title = "Löschen abgeschlossen"
            text = f"{d.model} ({d.path}, {fmt_bytes(d.size)}) wurde erfolgreich gelöscht."
            if j.elapsed:
                text += f"\nDauer: {fmt_duration(j.elapsed)}"
            if j.status and j.status.verified:
                text += "\nPrüfung: bestanden – jeder Sektor ist überschrieben."
            if j.mode.trims and j.status and j.status.trimResult:
                text += f"\nTRIM: {j.status.trimResult}"
        elif j.state == "cancelled":
            title = "Löschvorgang abgebrochen"
            text = f"{d.model} ({d.path}): {j.message or ''}"
        else:
            title = "Löschen fehlgeschlagen"
            text = f"{d.model} ({d.path}): {j.message or 'Unbekannter Fehler'}"
        if notify:
            n = Gio.Notification.new(title)
            n.set_body(text)
            self.app.send_notification(j.id, n)
            self.inform(title, text)

    def write_protocol(self, j: Job):
        folder = protocol_folder()
        try:
            folder.mkdir(parents=True, exist_ok=True)
        except OSError:
            return None
        d, s = j.disk, j.status
        started = s.startedAt if s and s.startedAt else j.info["createdAt"]
        ended = s.finishedAt if s and s.finishedAt else time.time()
        duration = max(0, ended - started)
        safe = "".join(c if c not in '/\\:' else "-" for c in d.model)
        path = folder / f"{time.strftime('%Y-%m-%d_%H-%M', time.localtime(started))}_{d.name}_{safe}.txt"
        result = {"done": "ERFOLGREICH", "cancelled": "ABGEBROCHEN – Laufwerk nur teilweise überschrieben"}.get(
            j.state, "FEHLGESCHLAGEN")
        verification = "nicht vorgesehen (Modus »Schnell«)"
        if j.mode.verifies:
            v = s.verified if s else None
            if v is True:
                verification = ("bestanden – alle Sektoren gelesen und mit den geschriebenen Zufallsdaten verglichen"
                                if j.mode.passes[-1] == SEEDED else "bestanden – alle Sektoren gelesen, alle enthalten Nullen")
            elif v is False:
                verification = "NICHT bestanden"
            else:
                verification = "nicht durchgeführt"
        trim = ((s.trimResult if s else None) or "nicht durchgeführt") if j.mode.trims else "nicht vorgesehen"
        written = s.bytesDoneTotal if s else 0
        avg = written / duration if duration > 0 else 0
        text = f"""WIPEKIT – LÖSCHPROTOKOLL
========================

Laufwerk:          {d.model}
Kennung:           {d.path}
Kapazität:         {fmt_bytes(d.size)} ({fmt_exact_bytes(s.diskSize if s and s.diskSize else d.size)})
Anschluss:         {d.bus}
Typ:               {d.type_label}
Frühere Volumes:   {", ".join(d.volumes) if d.volumes else "–"}

Methode:           {j.mode.title} – {j.mode.method_description}
Beginn:            {fmt_datetime(started)}
Ende:              {fmt_datetime(ended)}
Dauer:             {fmt_duration(duration)}
Ø Geschwindigkeit: {fmt_speed(avg)}

Ergebnis:          {result}
Verifikation:      {verification}
TRIM:              {trim}
Meldung:           {j.message or "–"}

Rechner:           {socket.gethostname()}
Erstellt mit WipeKit {VERSION} (Linux)
"""
        try:
            path.write_text(text, encoding="utf-8")
            return path
        except OSError:
            return None

    # --- Sonstiges ---------------------------------------------------------------------------

    def on_open_protocols(self, *_):
        folder = protocol_folder()
        folder.mkdir(parents=True, exist_ok=True)
        open_path(folder)

    def on_close_request(self, *_):
        active = sum(1 for j in self.jobs if j.active)
        if active == 0 or self.force_close:
            return False

        def really_close():
            self.force_close = True
            self.close()

        self.ask("Ein Löschvorgang läuft noch" if active == 1 else f"{active} Löschvorgänge laufen noch",
                 "Das Löschen wird im Hintergrund fortgesetzt und der Rechner bleibt wach. "
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
                Disk("sdx", "/dev/sdx", "Test SSD", 500_000_000_000, "USB", True, ["Daten"], [], [], False),
                Disk("sdy", "/dev/sdy", "Test HDD", 2_000_000_000_000, "USB", False, [], [], [], False),
                Disk("sdz", "/dev/sdz", "Test Unbekannt", 64_000_000_000, "USB", None, [], [], [], False),
                Disk("sdw", "/dev/sdw", "System", 256_000_000_000, "USB", True, [], ["/"], [], True),
            ]
            w.rebuild_disk_list()
            for d in w.disks:
                w.select(("disk", d.name))
                w.show_detail()
                if not d.is_protected:
                    for m in MODES:
                        w.mode_key = m.key
                        w.update_mode_detail(d)
            w.select(("disk", "sdx"))
            w.show_detail()
            shot("linux-1-ssd")
            w.select(("disk", "sdy"))
            w.show_detail()
            shot("linux-2-festplatte")
            tmp = Path(tempfile.mkdtemp())
            for state in ("starting", "running", "done", "failed", "cancelled"):
                info = {"id": "smoke-" + state, "disk": asdict(w.disks[0]), "mode": "ssd", "createdAt": time.time() - 100}
                jd = tmp / state
                jd.mkdir()
                st = HelperStatus(state=state, pid=os.getpid(), phase="Durchgang 1: Zufallsdaten schreiben", step=1,
                                  stepCount=4, diskSize=w.disks[0].size, bytesDoneTotal=10**11, bytesTotal=15 * 10**11,
                                  speed=2e8, startedAt=time.time() - 100, updatedAt=time.time(), verified=True,
                                  trimResult="durchgeführt", message="Testmeldung",
                                  finishedAt=None if state in ACTIVE_STATES else time.time())
                (jd / "status.json").write_text(st.to_json())
                j = Job(info, jd)
                j.poll()
                w.jobs.insert(0, j)
                w.rebuild_job_list()
                w.select(("job", j.id))
                w.show_detail()
                w.update_job_page()
                if state == "running":
                    w.update_sleep_status()
                    shot("linux-3-loeschen")
                if not j.active:
                    w.job_finished(j, notify=False)
                    w.show_detail()
                    if state == "done":
                        shot("linux-4-fertig")
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
