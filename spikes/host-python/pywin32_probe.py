"""Record why the Python host uses comtypes instead of pywin32. Read-only GetHierarchy calls.

Usage:  uv run --project spikes/host-python spikes/host-python/pywin32_probe.py
Logs one row per binding route to spikes/results/host.jsonl.
"""

import datetime
import json
import pathlib
import time
import winreg

import pythoncom
import win32com.client
from win32com.client import gencache

RESULTS = pathlib.Path(__file__).resolve().parent.parent / "results" / "host.jsonl"
TLB = "{0EA692EE-BB50-4E3C-AEF0-356D91732725}"


def log(probe, method, ok, ms, hr=None, detail=None):
    row = {"ts": datetime.datetime.now().astimezone().isoformat(), "probe": probe, "method": method,
           "args": {"host": "python"}, "outcome": "ok" if ok else "error", "verdict": "works" if ok else "fails",
           "hresult": hr, "ms": round(ms), "detail": detail or {}, "scenario": None, "host": "python", "elevated": None}
    with RESULTS.open("a", encoding="utf-8") as f:
        f.write(json.dumps(row) + "\n")
    print(f"[{row['verdict']:<8}] {probe}/{method} {round(ms)}ms {hr or ''} {json.dumps(detail or {})[:120]}")


def hresult(exc):
    code = exc.args[0] if exc.args and isinstance(exc.args[0], int) else None
    return f"{code & 0xFFFFFFFF:#010x}" if code is not None else type(exc).__name__


def attempt(probe, method, fn):
    t = time.perf_counter()
    try:
        r = fn()
        log(probe, method, True, (time.perf_counter() - t) * 1000, detail={"bytes": len(r)})
    except Exception as exc:  # noqa: BLE001
        log(probe, method, False, (time.perf_counter() - t) * 1000, hresult(exc), {"error": repr(exc)[:160]})


pythoncom.CoInitialize()
disp = pythoncom.CoCreateInstance("OneNote.Application", None, pythoncom.CLSCTX_LOCAL_SERVER, pythoncom.IID_IDispatch)
attempt("pywin32-dynamic", "GetHierarchy", lambda: win32com.client.Dispatch("OneNote.Application").GetHierarchy("", 2))
attempt("pywin32-gettypeinfo", "IDispatch.GetTypeInfo", lambda: str(disp.GetTypeInfo().GetDocumentation(-1)[0]))
attempt("pywin32-getidsofnames", "IDispatch.GetIDsOfNames", lambda: str(disp.GetIDsOfNames("GetHierarchy")))
attempt("pywin32-makepy-ensuredispatch", "GetHierarchy", lambda: gencache.EnsureDispatch("OneNote.Application").GetHierarchy("", 2))
attempt("pywin32-makepy-wrapper", "GetHierarchy",
        lambda: gencache.EnsureModule(TLB, 0, 1, 1).IApplication(disp).GetHierarchy("", 2, pythoncom.Missing, 2))

# Type library registration: which platform keys exist?
plats = []
with winreg.OpenKey(winreg.HKEY_CLASSES_ROOT, rf"TypeLib\{TLB}\1.1\0") as k:
    i = 0
    while True:
        try:
            plats.append(winreg.EnumKey(k, i)); i += 1
        except OSError:
            break
log("typelib-registration", "registry", "win64" in plats, 0, detail={"typelib": TLB, "version": "1.1", "platformKeys": plats})


def via_comtypes():
    import comtypes.client
    mod = comtypes.client.GetModule((TLB, 1, 1))
    return comtypes.client.CreateObject("OneNote.Application", interface=mod.IApplication).GetHierarchy("", mod.hsNotebooks, mod.xs2013)


attempt("comtypes-vtable", "GetHierarchy", via_comtypes)
