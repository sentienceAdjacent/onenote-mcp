"""Drive a spike MCP host over stdio and log what happens to spikes/results/host.jsonl.

Usage:  uv run --project spikes/host-python spikes/host-python/mcp_check.py <label> -- <server command...>

Checks: initialize + tools/list, one get_hierarchy call, 2 parallel calls, and 8 parallel calls that
each hold the STA thread for 300 ms (shows whether calls queue on one thread). Logs counts and
timings only, never notebook names.
"""

import asyncio
import datetime
import json
import os
import pathlib
import re
import sys
import time

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

RESULTS = pathlib.Path(__file__).resolve().parent.parent / "results" / "host.jsonl"
HEADER = re.compile(r"<!-- host=(\w+) thread=(\d+) apartment=(\w+) queueWaitMs=(\d+) callMs=(\d+) -->")


def log(label: str, probe: str, outcome: str, verdict: str, ms: float, detail: dict) -> None:
    RESULTS.parent.mkdir(parents=True, exist_ok=True)
    row = {
        "ts": datetime.datetime.now().astimezone().isoformat(),
        "probe": probe, "method": "tools/call get_hierarchy" if probe != "tools-list" else "tools/list",
        "args": {"host": label}, "outcome": outcome, "verdict": verdict, "hresult": None,
        "ms": round(ms), "detail": detail, "scenario": None, "host": label, "elevated": None,
    }
    with RESULTS.open("a", encoding="utf-8") as f:
        f.write(json.dumps(row) + "\n")
    print(f"[{verdict:<8}] {outcome:<6} {label}/{probe} {round(ms)}ms {json.dumps(detail)[:160]}")


def parse(result) -> dict:
    text = "".join(getattr(c, "text", "") for c in result.content)
    m = HEADER.search(text)
    d = {"isError": bool(getattr(result, "is_error", False)), "notebooks": text.count("<one:Notebook ")}
    if text.startswith("error:"):
        d["error"] = text[:120]
    if m:
        d.update(thread=int(m[2]), apartment=m[3], queueWaitMs=int(m[4]), callMs=int(m[5]))
    return d


async def main(label: str, cmd: list[str]) -> int:
    # Full environment: the SDK default passes a reduced set, which breaks `dotnet run` (MSBuild errors on stdout).
    params = StdioServerParameters(command=cmd[0], args=cmd[1:], env=dict(os.environ))
    t0 = time.perf_counter()
    async with stdio_client(params) as (r, w), ClientSession(r, w) as s:
        await s.initialize()
        tools = await s.list_tools()
        names = [t.name for t in tools.tools]
        props = next(t.input_schema.get("properties", {}) for t in tools.tools if t.name == "get_hierarchy")
        hold = next((p for p in props if "hold" in p.lower()), None)
        log(label, "tools-list", "ok", "works" if "get_hierarchy" in names else "fails", (time.perf_counter() - t0) * 1000,
            {"tools": names, "params": sorted(props)})

        t = time.perf_counter()
        d = parse(await s.call_tool("get_hierarchy", {}))
        ok = d["notebooks"] > 0 and not d["isError"]
        log(label, "single-call", "ok" if ok else "error", "works" if ok else "fails", (time.perf_counter() - t) * 1000, d)

        for n, hold_ms in ((2, 0), (8, 300)):
            args = {hold: hold_ms} if (hold and hold_ms) else {}
            t = time.perf_counter()
            res = await asyncio.gather(*(s.call_tool("get_hierarchy", args) for _ in range(n)), return_exceptions=True)
            wall = (time.perf_counter() - t) * 1000
            parsed = [parse(x) if not isinstance(x, BaseException) else {"exception": repr(x)[:120]} for x in res]
            good = [p for p in parsed if p.get("notebooks", 0) > 0 and not p.get("isError")]
            threads = sorted({p["thread"] for p in good if "thread" in p})
            detail = {"parallel": n, "holdMs": hold_ms, "succeeded": len(good), "distinctStaThreads": threads,
                      "apartments": sorted({p.get("apartment", "?") for p in good}),
                      "maxQueueWaitMs": max((p.get("queueWaitMs", 0) for p in good), default=None),
                      "wallMs": round(wall)}
            if len(good) != n:
                detail["failures"] = [p for p in parsed if p not in good][:3]
            log(label, f"parallel-{n}", "ok" if len(good) == n else "error", "works" if len(good) == n else "fails", wall, detail)
    return 0


if __name__ == "__main__":
    if "--" not in sys.argv:
        sys.exit(__doc__)
    i = sys.argv.index("--")
    sys.exit(asyncio.run(main(sys.argv[1], sys.argv[i + 1:])))
