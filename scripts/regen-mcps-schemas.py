"""Regenerate mcps/rvt-mcp/tools/*.json from a live server's tools/list.

Spawns the Release server exe with the full surface (--toolsets all
--enable-send-code --enable-adaptive-bake), enumerates tools/list (handles
pagination), and writes one JSON per tool, verbatim as returned. Removes stale
tool files that no longer exist. mcps/ is local state (not git) consumed by
registry publishing. The server needs no Revit: tools/list does not connect.

Usage:
  python scripts/regen-mcps-schemas.py [--out <dir>] [--exe <path>]
"""
import json
import os
import subprocess
import sys
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_EXE = os.path.join(REPO, "src", "server", "bin", "Release", "net8.0",
                           "RvtMcp.Server.exe")
DEFAULT_OUT = os.path.join(REPO, "..", "mcps", "rvt-mcp", "tools")


def send(proc, obj):
    proc.stdin.write(json.dumps(obj) + "\n")
    proc.stdin.flush()


def read_msg(proc, timeout=30):
    deadline = time.time() + timeout
    while time.time() < deadline:
        line = proc.stdout.readline()
        if not line:
            time.sleep(0.05)
            continue
        return json.loads(line)
    raise TimeoutError("no response from server")


def main():
    exe = DEFAULT_EXE
    out = os.path.abspath(DEFAULT_OUT)
    args = sys.argv[1:]
    for i, a in enumerate(args):
        if a == "--exe":
            exe = args[i + 1]
        elif a == "--out":
            out = os.path.abspath(args[i + 1])
    if not os.path.isfile(exe):
        sys.exit(f"server exe not found: {exe}\nBuild: dotnet build src/server -c Release")

    proc = subprocess.Popen(
        [exe, "--toolsets", "all", "--enable-send-code", "--enable-adaptive-bake"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, text=True, encoding="utf-8")
    try:
        send(proc, {"jsonrpc": "2.0", "id": 1, "method": "initialize",
                    "params": {"protocolVersion": "2025-06-18",
                               "capabilities": {},
                               "clientInfo": {"name": "regen", "version": "0"}}})
        init = read_msg(proc)
        if "result" not in init:
            sys.exit(f"initialize failed: {init}")
        send(proc, {"jsonrpc": "2.0", "method": "notifications/initialized"})

        tools = []
        cursor = None
        rid = 2
        while True:
            params = {"cursor": cursor} if cursor else {}
            send(proc, {"jsonrpc": "2.0", "id": rid, "method": "tools/list",
                        "params": params})
            resp = read_msg(proc)
            if "result" not in resp:
                sys.exit(f"tools/list failed: {resp}")
            tools.extend(resp["result"].get("tools", []))
            cursor = resp["result"].get("nextCursor")
            rid += 1
            if not cursor:
                break
    finally:
        proc.kill()

    os.makedirs(out, exist_ok=True)
    names = {t["name"] for t in tools}
    removed = []
    for f in os.listdir(out):
        if f.endswith(".json") and f[:-5] not in names:
            os.remove(os.path.join(out, f))
            removed.append(f)
    for t in tools:
        path = os.path.join(out, t["name"] + ".json")
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(t, fh, indent=1, ensure_ascii=False)
            fh.write("\n")
    print(f"wrote {len(tools)} tool schemas -> {out}")
    if removed:
        print(f"removed {len(removed)} stale: {', '.join(sorted(removed))}")


if __name__ == "__main__":
    main()
