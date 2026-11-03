"""Spike host (ticket 1): minimal stdio MCP server in Python with one tool, get_hierarchy.

Every COM call runs on one dedicated STA worker thread. COM is bound early (vtable) with
comtypes from the OneNote 15 type library: pywin32's IDispatch path fails on this machine with
TYPE_E_LIBNOTREGISTERED (see pywin32_probe.py and the findings doc). Not a production shape.
"""

import asyncio
import concurrent.futures
import queue
import threading
import time
from typing import Any, Callable

from mcp.server.mcpserver import MCPServer

ONENOTE_TYPELIB = ("{0EA692EE-BB50-4E3C-AEF0-356D91732725}", 1, 1)


class StaComThread:
    """One STA thread that owns the OneNote COM object; callers queue work to it."""

    def __init__(self) -> None:
        self._q: queue.Queue[tuple[Callable[[Any, Any], Any], concurrent.futures.Future]] = queue.Queue()
        self._ready = threading.Event()
        self._thread = threading.Thread(target=self._loop, name="OneNote-STA", daemon=True)
        self._thread.start()
        self._ready.wait()

    def _loop(self) -> None:
        import comtypes
        import comtypes.client

        comtypes.CoInitializeEx(comtypes.COINIT_APARTMENTTHREADED)
        mod = comtypes.client.GetModule(ONENOTE_TYPELIB)
        app = None
        self._ready.set()
        while True:
            fn, fut = self._q.get()
            if not fut.set_running_or_notify_cancel():
                continue
            try:
                if app is None:
                    app = comtypes.client.CreateObject("OneNote.Application", interface=mod.IApplication)
                fut.set_result(fn(app, mod))
            except BaseException as exc:  # noqa: BLE001 - surfaced to the caller
                fut.set_exception(exc)

    @property
    def ident(self) -> int | None:
        return self._thread.ident

    async def run(self, fn: Callable[[Any, Any], Any]) -> Any:
        fut: concurrent.futures.Future = concurrent.futures.Future()
        self._q.put((fn, fut))
        return await asyncio.wrap_future(fut)


sta = StaComThread()
server = MCPServer("onenote-spike-python")


@server.tool(name="get_hierarchy", description="Spike: return OneNote GetHierarchy XML. Read-only.")
async def get_hierarchy(scope: str = "notebooks", start_id: str = "", hold_ms: int = 0) -> str:
    """scope: notebooks | sections | pages. hold_ms (test only) keeps the STA thread busy after the call."""
    queued = time.perf_counter()

    def call(app: Any, mod: Any) -> tuple[str, int, int, str]:
        import ctypes

        waited = int((time.perf_counter() - queued) * 1000)
        hs = {"sections": mod.hsSections, "pages": mod.hsPages}.get(scope, mod.hsNotebooks)
        t = time.perf_counter()
        xml = app.GetHierarchy(start_id, hs, mod.xs2013)
        ms = int((time.perf_counter() - t) * 1000)
        # CoGetApartmentType: 0 = STA, 3 = MAINSTA
        apt_type, qual = ctypes.c_int(), ctypes.c_int()
        ctypes.oledll.ole32.CoGetApartmentType(ctypes.byref(apt_type), ctypes.byref(qual))
        if hold_ms > 0:
            time.sleep(hold_ms / 1000)
        return xml, ms, waited, {0: "STA", 1: "MTA", 2: "NA", 3: "MainSTA"}.get(apt_type.value, str(apt_type.value))

    try:
        xml, ms, waited, apt = await sta.run(call)
    except Exception as exc:  # COMError carries the HRESULT in args[0]
        hr = exc.args[0] if exc.args and isinstance(exc.args[0], int) else None
        return f"error: COM {hr & 0xFFFFFFFF:#010x} {exc}" if hr is not None else f"error: {exc!r}"
    return f"<!-- host=python thread={sta.ident} apartment={apt} queueWaitMs={waited} callMs={ms} -->\n{xml}"


if __name__ == "__main__":
    server.run("stdio")
