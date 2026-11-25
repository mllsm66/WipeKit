"""Automatische Tests der gemeinsamen Logik (Linux-Version): S.M.A.R.T.-Auswertung, Berichte, Dateinamen.

Aufruf:  python3 tests/test_core.py
"""

import os
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "linux" / "wipekit"))
FX = ROOT / "tests" / "fixtures"

import health as hl  # noqa: E402
import pdfdoc  # noqa: E402
import reports as rp  # noqa: E402


def load(name):
    return hl.parse_text((FX / f"smartctl-{name}.json").read_text())


class HealthTests(unittest.TestCase):
    def test_hdd(self):
        h = load("hdd")
        self.assertTrue(h.available)
        self.assertEqual(h.vendor, "Western Digital")
        self.assertEqual(h.model_number, "WD5000AAKX-00ERMA0")
        self.assertEqual(h.serial, "WD-WCC2EXX12345")
        self.assertEqual(h.rpm, 7200)
        self.assertEqual(h.interface_max, "SATA 600")
        self.assertEqual(h.interface_current, "SATA 300")
        self.assertEqual(h.power_on_hours, 14523)
        self.assertEqual(h.power_cycles, 1834)
        self.assertEqual(h.pending, 0)
        self.assertEqual(h.uncorrectable, 0)
        self.assertEqual(h.reallocated, 3)
        self.assertEqual(h.bytes_written, 61328447522 * 512)
        self.assertEqual(h.bytes_read, 102938475610 * 512)
        self.assertIsNone(h.life_percent)
        self.assertEqual(h.rating, hl.GOOD)

    def test_nvme(self):
        h = load("nvme")
        self.assertEqual(h.vendor, "Samsung")
        self.assertEqual(h.life_percent, 96)
        self.assertEqual(h.bytes_written, 34567890 * 512000)
        self.assertEqual(h.bytes_read, 45678901 * 512000)
        self.assertTrue(h.is_ssd)
        self.assertIsNone(h.rpm)
        self.assertEqual(h.rating, hl.VERY_GOOD)

    def test_sata_ssd(self):
        h = load("ssd")
        self.assertEqual(h.vendor, "Crucial")
        self.assertEqual(h.life_percent, 77)
        self.assertEqual(h.pending, 2)
        self.assertEqual(h.rating, hl.CAUTION)

    def test_usb_unsupported(self):
        h = load("usb-unsupported")
        self.assertFalse(h.available)
        self.assertEqual(h.rating, hl.UNKNOWN)
        self.assertIn("USB", h.reason)

    def test_bad_drive(self):
        import json
        j = json.loads((FX / "smartctl-hdd.json").read_text())
        for a in j["ata_smart_attributes"]["table"]:
            if a["id"] == 198:
                a["raw"]["value"] = 5
                a["raw"]["string"] = "5"
        self.assertEqual(hl.parse(j).rating, hl.BAD)
        j["smart_status"]["passed"] = False
        self.assertEqual(hl.parse(j).rating, hl.BAD)

    def test_key_values_complete(self):
        labels = [k for k, _ in hl.key_values(load("hdd"))]
        for want in ("Gesamtzustand", "Bytes geschrieben", "Bytes gelesen", "Ausstehende Sektoren",
                     "Unkorrigierbare Sektoren", "Drehzahl", "Einschaltvorgänge (Power-on-Zyklen)",
                     "Stunden eingeschaltet", "Schnittstelle", "Restlebensdauer"):
            self.assertIn(want, labels)


class ReportTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())

    def wipe_info(self, **kw):
        base = dict(mode_title="Sicher", method="2 Durchgänge", standard="NIST SP 800-88 Rev. 2 – Clear",
                    state="done", started=time.time() - 3600, ended=time.time(), bytes_written=10**12,
                    verification="bestanden", device="/dev/sdb", bus="USB", type_label="Festplatte",
                    exact_size=500107862016, host="test", platform="Linux", app_version="1.1.0")
        base.update(kw)
        return rp.WipeInfo(**base)

    def check_pdf(self, path: Path):
        data = path.read_bytes()
        self.assertTrue(data.startswith(b"%PDF-1.4"))
        self.assertIn(b"%%EOF", data[-10:])
        for tool in (["qpdf", "--check"], ["pdfinfo"]):
            try:
                r = subprocess.run(tool + [str(path)], capture_output=True, text=True)
            except FileNotFoundError:
                continue
            self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        try:
            r = subprocess.run(["pdftotext", str(path), "-"], capture_output=True, text=True)
            self.assertIn("WipeKit-Bericht", r.stdout)
        except FileNotFoundError:
            pass

    def test_file_name(self):
        h = load("hdd")
        ident = rp.identity_from(h)
        when = time.mktime((2026, 11, 12, 3, 3, 0, 0, 0, -1))
        self.assertEqual(rp.base_name(ident, when), "2026-11-12_03-03_WESTERN-DIGITAL-WD5000AAKX-00ERMA0-500GB-WD-WCC2EXX12345")

    def test_all_reports(self):
        for name in ("hdd", "nvme", "ssd", "usb-unsupported"):
            h = load(name)
            ident = rp.identity_from(h, "WDC WD5000LPVX", 500107862016, "ABC")
            r = rp.health_report(h, ident, "1.1.0", "Linux", "test")
            pdf, txt = rp.save(r, self.tmp)
            self.assertEqual(pdf.parent.name, "PDF")
            self.assertEqual(txt.parent.name, "Text")
            self.check_pdf(pdf)
            text = txt.read_text()
            self.assertIn("Gesamtzustand", text)
            for state in ("done", "failed", "cancelled"):
                for detailed in (False, True):
                    r = rp.wipe_report(self.wipe_info(state=state), ident, h, h, detailed)
                    pdf, txt = rp.save(r, self.tmp)
                    self.check_pdf(pdf)
            r = rp.wipe_report(self.wipe_info(read_only=True, bad_blocks=0, non_zero=0), ident, h, h)
            pdf, txt = rp.save(r, self.tmp)
            self.assertIn("Pruefprotokoll", pdf.name)
            self.check_pdf(pdf)

    def test_short_protocol_one_page(self):
        """Das kurze Löschprotokoll passt auch im ungünstigsten Fall (SSD-Modus, keine Daten) auf eine Seite."""
        h = load("usb-unsupported")
        ident = rp.identity_from(h, "WDS500G2B0A-00SM50", 500107862016, "")
        w = self.wipe_info(mode_title="SSD", method="2 Durchgänge Zufallsdaten + vollständige Lese-Verifikation + TRIM aller Blöcke",
                           standard="NIST SP 800-88 Rev. 2 – »Clear« (Überschreiben) – für SSDs nach bestem Bemühen",
                           verification="bestanden – alle Sektoren gelesen und mit den geschriebenen Zufallsdaten verglichen",
                           trim="durchgeführt – alle Blöcke freigegeben", type_label="SSD", volumes=["EFI", "Ohne Titel"],
                           message="Alle Sektoren wurden überschrieben und die Prüfung war erfolgreich.",
                           host="iMac von Ben", platform="macOS 26.7.3")
        pdf, _ = rp.save(rp.wipe_report(w, ident, h, h, False), self.tmp)
        try:
            out = subprocess.run(["pdfinfo", str(pdf)], capture_output=True, text=True).stdout
        except FileNotFoundError:
            self.skipTest("pdfinfo fehlt")
        pages = [l for l in out.splitlines() if l.startswith("Pages:")]
        self.assertEqual(pages[0].split()[-1], "1", out)

    def test_detailed_contains_values(self):
        h = load("hdd")
        r = rp.wipe_report(self.wipe_info(), rp.identity_from(h), h, h, detailed=True)
        text = rp.render_text(r)
        for want in ("Hersteller:", "Western Digital", "Seriennummer:", "WD-WCC2EXX12345", "Bytes geschrieben",
                     "Ausstehende Sektoren", "SATA 600", "7.200 U/min", "14.523"):
            self.assertIn(want, text)
        small = rp.render_text(rp.wipe_report(self.wipe_info(), rp.identity_from(h), h, h, detailed=False))
        self.assertNotIn("Ausstehende Sektoren", small)
        self.assertIn("Gesamtzustand:", small)

    def test_text_encoding(self):
        self.assertEqual(pdfdoc.to_ansi("Größe ≈ 5 °C"), "Größe ca. 5 °C".encode("cp1252"))
        self.assertGreater(pdfdoc.text_width("WipeKit", 10, True), pdfdoc.text_width("WipeKit", 10, False))


if __name__ == "__main__":
    os.environ.setdefault("TZ", "Europe/Berlin")
    unittest.main(verbosity=2)


class JobFinishTests(unittest.TestCase):
    """Abschluss im Arbeitsprozess: Protokolle, finish.json, Zugangsdaten entfernen"""

    def test_finish_creates_reports(self):
        import json as _json
        import jobfinish
        from common import HelperStatus
        with tempfile.TemporaryDirectory() as tmp:
            job = Path(tmp) / "job"
            job.mkdir()
            disk = {"name": "sdb", "path": "/dev/sdb", "model": "WDC WD5000AAKX-00ERMA0", "size": 500107862016,
                    "bus": "USB", "is_ssd": False, "transport": "usb", "serial": "WD-1"}
            (job / "job.json").write_text(_json.dumps({"id": "x", "disk": disk, "mode": "secure",
                                                       "createdAt": time.time() - 60, "purpose": ""}))
            (job / "smart-before.json").write_text((FX / "smartctl-hdd.json").read_text())
            settings = {"detailed": True, "serialInFileName": False, "meta": [["Durchgeführt von", "Ben"]],
                        "notifyLocal": False, "push": {"service": "off"}, "host": "test", "platform": "Linux",
                        "appVersion": "9.9.9", "uid": 0}
            (job / jobfinish.SETTINGS_FILE).write_text(_json.dumps(settings))
            st = HelperStatus(state="done", startedAt=time.time() - 50, finishedAt=time.time(), verified=True,
                              bytesDoneTotal=10, bytesTotal=10, diskSize=500107862016)
            written = []
            fin = jobfinish.finish(job, st, "done", "Alles gut.", lambda: written.append(1))
            self.assertEqual(written, [1])
            self.assertEqual(len(fin["files"]), 2)
            self.assertFalse((job / jobfinish.SETTINGS_FILE).exists())
            saved = _json.loads((job / jobfinish.FINISH_FILE).read_text())
            self.assertEqual(saved["files"][0]["title"], "Löschprotokoll")
            pdf = Path(saved["files"][0]["pdf"])
            self.assertTrue(pdf.exists())
            self.assertNotIn("WCC2EXX12345", pdf.name)          # Seriennummer abgeschaltet
            txt = Path(saved["files"][1]["txt"]).read_text()
            self.assertIn("Durchgeführt von", txt)
            head, body, warn = jobfinish.notice(_json.loads((job / "job.json").read_text()), "done", "", st,
                                                hl.parse_text((FX / "smartctl-hdd.json").read_text()))
            self.assertIn("erfolgreich gelöscht", body)

    def test_push_validation(self):
        import push
        self.assertTrue(push.valid_discord("https://discord.com/api/webhooks/1/abc"))
        self.assertFalse(push.valid_discord("https://evil.example/api/webhooks/1/abc"))
        self.assertIsNone(push.parse_hub_code("WKHUB1:nonsense"))
        self.assertEqual(push.send({"service": "ntfy", "onDone": False}, {"state": "done"}), None)
