"""Validate a setup ZIP and smoke-test its own self-contained server.

Optional --live-2027 only reads the active Revit view, after proving that the
installed plugin is byte-identical to the 2027 plugin inside this ZIP.
"""
import argparse
import datetime
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath, PureWindowsPath
import queue
import subprocess
import threading
import time
import zipfile


def digest(data):
    return hashlib.sha256(data).hexdigest().upper()


def safe_path(name):
    path = PurePosixPath(name.replace("\\", "/"))
    assert not path.is_absolute() and ".." not in path.parts and not PureWindowsPath(name).drive, name


class Client:
    def __init__(self, exe, log, args):
        self.log = log.open("w", encoding="utf-8")
        self.proc = subprocess.Popen([str(exe), *args], stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=self.log, text=True, encoding="utf-8",
            creationflags=subprocess.CREATE_NO_WINDOW)
        self.messages = queue.Queue()
        self.counter = 0

        def read():
            try:
                for line in self.proc.stdout:
                    self.messages.put(json.loads(line))
            finally:
                self.messages.put(None)

        threading.Thread(target=read, daemon=True).start()

    def send(self, value):
        self.proc.stdin.write(json.dumps(value) + "\n")
        self.proc.stdin.flush()

    def request(self, method, params):
        self.counter += 1
        self.send({"jsonrpc": "2.0", "id": self.counter, "method": method, "params": params})
        deadline = time.monotonic() + 60
        while True:
            reply = self.messages.get(timeout=max(.1, deadline - time.monotonic()))
            assert reply is not None, "Server exited before responding"
            if reply.get("id") == self.counter:
                assert "error" not in reply, reply
                return reply["result"]

    def close(self):
        self.proc.stdin.close()
        try:
            self.proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.proc.terminate()  # Only the subprocess created by this test.
            self.proc.wait(timeout=10)
        self.log.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("zip", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--version", default="0.6.2")
    parser.add_argument("--live-2027", action="store_true")
    args = parser.parse_args()
    package = args.zip.resolve()
    extraction = package.parent / "verified-extraction"
    assert not extraction.exists(), f"Use a new extraction directory: {extraction}"
    report = {"testedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "package": package.name, "bytes": package.stat().st_size,
        "sha256": digest(package.read_bytes())}
    with zipfile.ZipFile(package) as archive:
        assert archive.testzip() is None, "ZIP CRC check failed"
        for name in archive.namelist():
            safe_path(name)
        manifest = json.loads(archive.read("manifest.json").decode("utf-8-sig"))
        assert manifest["version"] == args.version, manifest["version"]
        assert sorted(p["year"] for p in manifest["plugins"]) == list(range(2022, 2028))
        for item in manifest["files"]:
            data = archive.read(item["path"])
            assert len(data) == item["bytes"] and digest(data) == item["sha256"].upper(), item["path"]
        assert set(n for n in archive.namelist() if not n.endswith("/")) == {
            "manifest.json", *(f["path"] for f in manifest["files"])}
        plugin_hashes = {}
        for plugin in manifest["plugins"]:
            data = archive.read(plugin["path"])
            assert digest(data) == plugin["sha256"].upper(), plugin["path"]
            with zipfile.ZipFile(io.BytesIO(data)) as inner:
                assert inner.testzip() is None
                for name in inner.namelist():
                    safe_path(name)
                for required in ("RvtMcp.Plugin.dll", f"RvtMcp.R{plugin['year']-2000}.addin",
                                 "e_sqlite3.dll", "runtimes/win-x64/native/e_sqlite3.dll"):
                    assert required in inner.namelist(), (plugin["year"], required)
                plugin_hashes[str(plugin["year"])] = digest(inner.read("RvtMcp.Plugin.dll"))
        report.update(version=manifest["version"], manifestCommit=manifest["commit"],
            checkedFileCount=len(manifest["files"]), pluginSha256=plugin_hashes,
            installerSha256=digest(archive.read("install.ps1")), packageIntegrityPassed=True)
        archive.extractall(extraction)
    exe = extraction / manifest["server"]["command"]
    report["serverExeSha256"] = digest(exe.read_bytes())
    report["smoke"] = []
    for mode, flags, expected in (("default", [], 40), ("all", ["--toolsets", "all"], 229)):
        client = Client(exe, package.parent / f"smoke-{mode}-stderr.log", flags + ["--target", "2027"])
        try:
            init = client.request("initialize", {"protocolVersion": "2025-03-26", "capabilities": {},
                "clientInfo": {"name": "setup-package-verification", "version": "1"}})
            assert init["serverInfo"]["version"] == args.version, init["serverInfo"]
            client.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
            catalog = client.request("tools/list", {})["tools"]
            assert len(catalog) == expected, (mode, len(catalog))
            if mode == "all":
                tools = {t["name"]: t for t in catalog}
                assert "host_id" in tools["revit_create_point_based_element"]["inputSchema"]["properties"]
                assert {"systemTypeId", "startElementId", "startConnectorId"} <= set(
                    tools["revit_create_pipe"]["inputSchema"]["properties"])
                if args.live_2027:
                    installed = Path(os.environ["APPDATA"]) / "Autodesk/Revit/Addins/2027/RvtMcp/RvtMcp.Plugin.dll"
                    assert digest(installed.read_bytes()) == plugin_hashes["2027"], "Installed plugin differs from package"
                    result = client.request("tools/call", {"name": "revit_get_current_view_info", "arguments": {}})
                    assert not result.get("isError"), result
                    payload = json.loads(next(c["text"] for c in result["content"] if c["type"] == "text"))
                    assert "error" not in payload and ("viewName" in payload or "view_name" in payload), payload
                    report["live2027View"] = payload
                    report["live2027PluginMatchesPackage"] = True
            report["smoke"].append({"mode": mode, "serverInfo": init["serverInfo"], "toolCount": len(catalog), "passed": True})
        finally:
            client.close()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
