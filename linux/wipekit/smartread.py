"""Liest S.M.A.R.T.-Daten mit smartctl (smartmontools) und versteckte Bereiche (HPA) mit hdparm.

Wird nur von Prozessen mit Administratorrechten verwendet (wipekit-diag, wipekit-helper).
"""

import json
import os
import re
import shutil
import subprocess

# Reihenfolge der Versuche bei USB-Adaptern, die smartctl nicht von selbst erkennt
USB_TYPES = ["sat", "sntasmedia", "sntjmicron", "sntrealtek", "usbjmicron", "usbcypress", "usbprolific", "usbsunplus"]


def smartctl_path():
    return shutil.which("smartctl") or ("/usr/sbin/smartctl" if os.path.exists("/usr/sbin/smartctl") else None)


def _run(args, timeout=60):
    try:
        r = subprocess.run(args, capture_output=True, text=True, timeout=timeout)
        return r.returncode, r.stdout, r.stderr
    except (OSError, subprocess.TimeoutExpired) as e:
        return -1, "", str(e)


def _has_data(j: dict) -> bool:
    return bool(j.get("nvme_smart_health_information_log") or (j.get("ata_smart_attributes") or {}).get("table")
                or "smart_status" in j)


def read_smart(dev_path: str, transport: str = "") -> dict:
    """Gibt {"json": …, "devtype": "…"} zurück; json enthält im Fehlerfall smartctls Meldungen."""
    exe = smartctl_path()
    if not exe:
        return {"json": {"smartctl": {"messages": [{"string": "smartctl nicht installiert", "severity": "error"}]}},
                "devtype": "", "missing": True}
    types = [""]
    if transport == "usb":
        types += USB_TYPES
    first = None
    for t in types:
        args = [exe, "-x", "-j"] + (["-d", t] if t else []) + [dev_path]
        code, out, err = _run(args, timeout=90)
        try:
            j = json.loads(out) if out.strip() else {}
        except ValueError:
            j = {}
        if first is None:
            first = j or {"smartctl": {"messages": [{"string": err.strip() or "smartctl fehlgeschlagen"}]}}
        if _has_data(j):
            return {"json": j, "devtype": t}
    return {"json": first or {}, "devtype": ""}


def self_test(dev_path: str, devtype: str, test: str) -> tuple:
    """Startet einen Selbsttest (short/long/conveyance) oder bricht ihn ab (abort)."""
    exe = smartctl_path()
    if not exe:
        return False, "smartctl ist nicht installiert."
    arg = ["-X"] if test == "abort" else ["-t", test]
    code, out, err = _run([exe, "-j"] + arg + (["-d", devtype] if devtype else []) + [dev_path], timeout=60)
    try:
        j = json.loads(out)
        msgs = [m.get("string", "") for m in (j.get("smartctl") or {}).get("messages", [])]
    except ValueError:
        msgs = [err.strip()]
    ok = code >= 0 and not (code & 0b11)
    return ok, "; ".join(m for m in msgs if m)


def read_hpa(dev_path: str) -> dict:
    """hdparm -N: {"enabled": bool, "visible": int, "native": int} oder {}."""
    exe = shutil.which("hdparm") or ("/usr/sbin/hdparm" if os.path.exists("/usr/sbin/hdparm") else None)
    if not exe:
        return {}
    code, out, err = _run([exe, "-N", dev_path], timeout=15)
    m = re.search(r"max sectors\s*=\s*(\d+)/(\d+),\s*HPA is (enabled|disabled)", out)
    if not m:
        return {}
    return {"visible": int(m.group(1)), "native": int(m.group(2)), "enabled": m.group(3) == "enabled"}


def hpa_text(hpa: dict, logical_block=512) -> str:
    if not hpa:
        return ""
    if hpa.get("enabled") and hpa.get("native", 0) > hpa.get("visible", 0):
        hidden = (hpa["native"] - hpa["visible"]) * logical_block
        from health import fmt_bytes
        return (f"ACHTUNG: Ein versteckter Bereich (HPA) von {fmt_bytes(hidden)} ist aktiv und wird beim "
                "Überschreiben nicht erreicht.")
    return "Kein versteckter Bereich (HPA) aktiv."
