"""Exercise the real stdio MCP server; --live uses an isolated disposable Revit project.

Run with Revit 2027 + Snowdon Towers Sample HVAC open for --live. Existing model
is never saved/modified. The scratch project is saved only to permit activation,
then closed without saving test instances. Logs/results go to ignored artifacts.
"""
import argparse
import datetime
import hashlib
import json
import pathlib
import queue
import subprocess
import threading
import time

ROOT = pathlib.Path(__file__).resolve().parents[3]
OUT = ROOT / "artifacts/issue-13"


class Client:
    def __init__(self, exe):
        self.stderr = (OUT / "typed-server-stderr.log").open("w", encoding="utf-8")
        self.proc = subprocess.Popen([str(exe), "--toolsets", "all", "--target", "2027"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.stderr,
            text=True, encoding="utf-8", creationflags=subprocess.CREATE_NO_WINDOW)
        self.messages = queue.Queue()
        self.counter = 0
        def read():
            for line in self.proc.stdout:
                self.messages.put(json.loads(line))
        threading.Thread(target=read, daemon=True).start()
        self.request("initialize", {"protocolVersion": "2025-03-26", "capabilities": {},
            "clientInfo": {"name": "issue13-acceptance", "version": "1"}})
        self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def send(self, msg):
        self.proc.stdin.write(json.dumps(msg) + "\n")
        self.proc.stdin.flush()

    def request(self, method, params):
        self.counter += 1
        request_id = self.counter
        self.send({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params})
        deadline = time.monotonic() + 70
        while True:
            reply = self.messages.get(timeout=max(.1, deadline - time.monotonic()))
            if reply.get("id") == request_id:
                if "error" in reply:
                    raise RuntimeError(reply["error"])
                return reply["result"]

    def tool(self, name, arguments):
        result = self.request("tools/call", {"name": name, "arguments": arguments})
        content = next(c["text"] for c in result["content"] if c["type"] == "text")
        try:
            return json.loads(content)
        except json.JSONDecodeError:
            return {"error_text": content, "isError": result.get("isError", False)}

    def code(self, source):
        result = self.tool("revit_send_code_to_revit", {"code": source})
        assert result.get("executed"), result
        return result["result"]

    def close(self):
        self.proc.stdin.close()
        try:
            self.proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            # This is solely the subprocess created by this test, never a user's MCP session.
            self.proc.terminate()
            self.proc.wait(timeout=10)
        self.stderr.close()


SETUP = r'''
using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
public class McpDynamicScript {
 public static object Run(UIApplication app) {
  var original=app.ActiveUIDocument.Document;
  var originalPath=original.PathName;
  if(string.IsNullOrEmpty(originalPath))throw new Exception("Open the saved HVAC sample first.");
  var linked=new FilteredElementCollector(original).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().Select(l=>l.GetLinkDocument()).First(d=>d!=null&&d.Title.Contains("Architectural"));
  var symbol=new FilteredElementCollector(linked).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().First(s=>s.FamilyName=="Door-Passage-Single-Flush");
  var temp=app.Application.NewProjectDocument(UnitSystem.Metric);
  long typeId,hostId;string levelName;
  try {
   using(var tx=new Transaction(temp,"Issue13 typed fixture")){tx.Start();
    var options=new CopyPasteOptions();options.SetDuplicateTypeNamesHandler(new DestinationTypes());
    var copied=ElementTransformUtils.CopyElements(linked,new[]{symbol.Id},temp,Transform.Identity,options).Select(id=>temp.GetElement(id)).OfType<FamilySymbol>().First();
    var level=Level.Create(temp,0);levelName=level.Name;
    var wall=Wall.Create(temp,Line.CreateBound(new XYZ(0,2000/304.8,0),new XYZ(5000/304.8,2000/304.8,0)),level.Id,false);
    typeId=copied.Id.Value;hostId=wall.Id.Value;
    if(tx.Commit()!=TransactionStatus.Committed)throw new Exception("Fixture commit failed");
   }
   temp.SaveAs(@"__SCRATCH__");
  } finally {temp.Close(false);}
  app.OpenAndActivateDocument(@"__SCRATCH__");
  return new{originalPath,originalModified=original.IsModified,typeId,hostId,levelName,scratch=@"__SCRATCH__"};
 }
}
public class DestinationTypes:IDuplicateTypeNamesHandler {public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs a)=>DuplicateTypeAction.UseDestinationTypes;}
'''

RUNTIME = r'''
var asm=typeof(RvtMcp.Plugin.Handlers.CreatePointBasedElementHandler).Assembly;
string hash;
using(var sha=System.Security.Cryptography.SHA256.Create())
 hash=BitConverter.ToString(sha.ComputeHash(System.IO.File.ReadAllBytes(asm.Location))).Replace("-", "");
return new{model=doc.Title,modified=doc.IsModified,revit=app.Application.VersionNumber,
 build=app.Application.VersionBuild,pluginSha256=hash,pluginMvid=asm.ManifestModule.ModuleVersionId,
 toastEnabled=RvtMcp.Plugin.App.Instance.ToastEnabled,pid=System.Diagnostics.Process.GetCurrentProcess().Id};
'''


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--server", type=pathlib.Path, default=ROOT / "publish/server-issues-11-13/RvtMcp.Server.exe")
    parser.add_argument("--live", action="store_true")
    args = parser.parse_args()
    OUT.mkdir(parents=True, exist_ok=True)
    client = Client(args.server)
    report = {"tested_at_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "server": str(args.server), "server_dll_sha256": hashlib.sha256(args.server.with_suffix(".dll").read_bytes()).hexdigest().upper()}
    fixture = None
    try:
        catalog = client.request("tools/list", {})
        tool = next(t for t in catalog["tools"] if t["name"] == "revit_create_point_based_element")
        assert "host_id" in tool["inputSchema"]["properties"], tool
        assert "host_id" not in tool["inputSchema"].get("required", []), tool
        report.update(tool_count=len(catalog["tools"]), schema=tool, schema_pass=True)
        if args.live:
            targets = client.tool("revit_list_available_targets", {})
            assert any(t["year"] == "2027" for t in targets["targets"]), targets
            client.tool("revit_switch_target", {"version": "2027", "verify": True})
            report["runtime"] = client.code(RUNTIME)
            assert report["runtime"]["toastEnabled"], report["runtime"]
            report["installed_handler_suite"] = client.code((pathlib.Path(__file__).parent / "live-regression.cs").read_text(encoding="utf-8-sig"))
            suite = report["installed_handler_suite"]
            assert suite.get("failed") == 0 and suite.get("total") == 16 and suite.get("restored"), suite
            scratch = OUT / ("typed-fixture-" + str(time.time_ns()) + ".rvt")
            fixture = client.code(SETUP.replace("__SCRATCH__", str(scratch)))
            report["fixture"] = fixture
            params = {"typeId": fixture["typeId"], "x": 2500, "y": 2000, "z": 0, "level": fixture["levelName"]}
            missing = client.tool("revit_create_point_based_element", params)
            assert "requires host_id" in json.dumps(missing), missing
            report["missing_host"] = missing
            params["host_id"] = fixture["hostId"]
            created = client.tool("revit_create_point_based_element", params)
            assert created.get("host_id") == fixture["hostId"], created
            report["created"] = created
            check = client.code('var i=(FamilyInstance)doc.GetElement(new ElementId(' + str(created["elementId"]) + 'L));var p=((LocationPoint)i.Location).Point;return new{host=i.Host.Id.Value,x=p.X*304.8,y=p.Y*304.8,z=p.Z*304.8};')
            assert check["host"] == fixture["hostId"] and abs(check["x"]-2500) < 1 and abs(check["y"]-2000) < 1 and abs(check["z"]) < 1, check
            report.update(actual=check, live_pass=True)
            # Let error/success notifications expire through the real host dispatcher.
            time.sleep(10)
            report["runtime_after_toasts"] = client.code(RUNTIME)
            assert report["runtime_after_toasts"]["pid"] == report["runtime"]["pid"]
    finally:
        try:
            if fixture:
                original = fixture["originalPath"].replace('"', '""')
                cleanup = client.code('var scratch=doc;var original=app.OpenAndActivateDocument(@"' + original + '").Document;scratch.Close(false);return new{model=original.Title,modified=original.IsModified,scratchClosed=!scratch.IsValidObject};')
                report["cleanup"] = cleanup
                assert cleanup["scratchClosed"] and cleanup["modified"] == fixture["originalModified"], cleanup
        finally:
            (OUT / ("typed-server-live.json" if args.live else "typed-server-schema.json")).write_text(json.dumps(report, indent=2), encoding="utf-8")
            client.close()
    print(json.dumps({k:v for k,v in report.items() if k not in ("schema", "fixture")}, indent=2))


if __name__ == "__main__":
    main()
