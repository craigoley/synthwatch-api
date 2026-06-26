#!/usr/bin/env python3
"""
SPIKE PROTOTYPE for the "trace AI insights" feature (see ../trace-ai-insights.md).

Proves the server-side extraction: a Playwright trace.zip -> a COMPACT, FILTERED structured summary
suitable to send to gpt-5-mini (a few hundred tokens, never the raw 18 MB trace). The production build
re-implements this in C# (System.IO.Compression + NDJSON), but this proves the signals + the filter.

Run against a real trace:
    curl -s -o t.zip https://synthwatch-api.azurewebsites.net/api/runs/844486/trace
    unzip -o t.zip trace.network trace.trace
    python3 extract_trace.py --network trace.network --console trace.trace --target www.wegmans.com
"""
import argparse, json, re
from urllib.parse import urlparse
from collections import defaultdict

# Browser-EXTENSION console noise (a trace captured/opened with extensions is NOT the monitored site).
# Matched against the message text AND its source url. Headless runner traces are clean, but this is the
# load-bearing correctness filter — keep it.
EXT_NOISE = re.compile(
    r"grammarly|recorder\.contentScripts|contentscript|message port closed|"
    r"DEFAULT root logger|AAA-init|chrome-extension://|moz-extension://", re.I)

TEXT_TYPES = {"script", "stylesheet", "document", "fetch", "xhr"}  # assets where 'uncompressed' is a real concern


def host(u: str) -> str:
    try:
        return urlparse(u).hostname or ""
    except ValueError:
        return ""


def is_site(h: str, target: str) -> bool:
    return h == target or h.endswith("." + target)


def extract_network(path: str, target: str) -> dict:
    reqs = []
    with open(path) as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                e = json.loads(line)
            except json.JSONDecodeError:
                continue
            if e.get("type") != "resource-snapshot":
                continue
            s = e["snapshot"]
            req, resp = s.get("request", {}), s.get("response", {}) or {}
            cont = resp.get("content", {}) or {}
            headers = {h["name"].lower(): h["value"] for h in (resp.get("headers") or [])}
            reqs.append(dict(
                url=req.get("url", ""), method=req.get("method", ""), status=resp.get("status", 0),
                rtype=s.get("_resourceType", ""), time=round(s.get("time", 0) or 0),
                wait=round((s.get("timings", {}) or {}).get("wait", 0) or 0),
                wire=resp.get("_transferSize", 0) or 0, size=cont.get("size", 0) or 0,
                enc=headers.get("content-encoding", ""), cache=headers.get("cache-control", ""),
                third=not is_site(host(req.get("url", "")), target),
            ))

    def top(key, n=5, pred=None):
        rs = [r for r in reqs if pred is None or pred(r)]
        return sorted(rs, key=key, reverse=True)[:n]

    tp = defaultdict(lambda: [0, 0])
    for r in reqs:
        if r["third"]:
            tp[host(r["url"])][0] += 1
            tp[host(r["url"])][1] += r["wire"]

    def slim(r):
        return {k: r[k] for k in ("url", "status", "rtype", "time", "wait", "size", "wire", "enc", "third")}

    return dict(
        totalRequests=len(reqs), wireKb=sum(r["wire"] for r in reqs) // 1024,
        thirdPartyCount=sum(1 for r in reqs if r["third"]),
        failed=[slim(r) for r in reqs if r["status"] >= 400][:8],
        slowest=[slim(r) for r in top(lambda r: r["time"])],
        largest=[slim(r) for r in top(lambda r: r["size"])],
        # 'uncompressed' applies to TEXT assets only (a big image isn't "uncompressed", it's just large).
        uncompressed=[slim(r) for r in top(lambda r: r["size"],
                      pred=lambda r: r["rtype"] in TEXT_TYPES and not r["enc"] and r["size"] > 30000)],
        topThirdParties=[dict(host=h, count=c, kb=b // 1024)
                         for h, (c, b) in sorted(tp.items(), key=lambda x: -x[1][1])[:6]],
    )


def extract_console(path: str, target: str) -> dict:
    kept, dropped_level, dropped_ext = [], 0, 0
    seen = set()
    with open(path) as f:
        for line in f:
            try:
                e = json.loads(line)
            except json.JSONDecodeError:
                continue
            if e.get("type") != "console":
                continue
            mt = e.get("messageType", "log")
            text = (e.get("text") or "").strip()
            loc = (e.get("location") or {}).get("url", "")
            if mt not in ("error", "warning"):          # drop info/log SDK chatter
                dropped_level += 1
                continue
            if EXT_NOISE.search(text) or EXT_NOISE.search(loc):  # drop browser-extension noise
                dropped_ext += 1
                continue
            key = (mt, text[:80])
            if key in seen:                              # dedupe spammy repeats
                continue
            seen.add(key)
            kept.append(dict(level=mt, origin="site" if is_site(host(loc), target) else "third-party",
                             text=text[:200]))
    return dict(messages=kept, droppedInfoLog=dropped_level, droppedExtensionNoise=dropped_ext)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--network", required=True)
    ap.add_argument("--console", required=True)
    ap.add_argument("--target", required=True, help="the check's target host, e.g. www.wegmans.com")
    a = ap.parse_args()
    summary = dict(target=a.target,
                   network=extract_network(a.network, a.target),
                   console=extract_console(a.console, a.target))
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
