"""Review follow-up on the restarted Revit 2027 plugin and a fresh stdio server.

Requires the dedicated, unmodified Review-MEP-2027.rvt copy to be active.
Runs installed-handler regressions, then actual typed MCP pipe calls. Discards
typed-call fixtures by closing/reopening this test copy, never saving them.
"""
import datetime
import hashlib
import importlib.util
import json
import pathlib
import sys
import time

sys.dont_write_bytecode = True
ROOT = pathlib.Path(__file__).resolve().parents[3]
OUT = ROOT / "artifacts/issue-12-review"
spec = importlib.util.spec_from_file_location("placement_smoke", ROOT / "docs/testing/issue-13/typed-server-smoke.py")
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)
smoke.OUT = OUT

RUNTIME = r'''
if(doc.Title!="Review-MEP-2027")throw new Exception("Dedicated test copy required.");
var asm=typeof(RvtMcp.Plugin.Handlers.ConnectMepElementsHandler).Assembly;
var ids=new FilteredElementCollector(doc).WherePasses(new LogicalOrFilter(new ElementIsElementTypeFilter(),new ElementIsElementTypeFilter(true))).ToElementIds().Select(id=>id.Value).OrderBy(id=>id).ToArray();
string dllHash,idsHash;
using(var sha=System.Security.Cryptography.SHA256.Create()) {
 dllHash=BitConverter.ToString(sha.ComputeHash(System.IO.File.ReadAllBytes(asm.Location))).Replace("-", "");
 idsHash=BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(string.Join(",",ids)))).Replace("-", "");
}
return new{model=doc.Title,path=doc.PathName,modified=doc.IsModified,revit=app.Application.VersionNumber,build=app.Application.VersionBuild,
 pluginSha256=dllHash,pluginMvid=asm.ManifestModule.ModuleVersionId,pid=System.Diagnostics.Process.GetCurrentProcess().Id,
 toastEnabled=RvtMcp.Plugin.App.Instance.ToastEnabled,elementCount=ids.Length,elementIdsSha256=idsHash};
'''

SETUP = r'''
using Autodesk.Revit.DB.Plumbing;
var pt=new FilteredElementCollector(doc).OfClass(typeof(PipeType)).FirstElementId();
var types=new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().ToArray();
var sanitary=types.First(t=>t.SystemClassification==MEPSystemClassification.Sanitary);
var other=types.First(t=>t.Id!=sanitary.Id);
var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();
var start=new XYZ(1000,1000,level.Elevation+10);var joint=start+new XYZ(10,0,0);var end=joint+new XYZ(10,0,0);
Pipe a,b;
using(var tx=new Transaction(doc,"Review typed fixture")) {
 tx.Start();a=Pipe.Create(doc,sanitary.Id,pt,level.Id,start,joint);b=Pipe.Create(doc,sanitary.Id,pt,level.Id,joint+new XYZ(0,10,0),joint);
 a.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(4.0/12.0);b.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(4.0/12.0);
 if(tx.Commit()!=TransactionStatus.Committed)throw new Exception("Fixture commit failed");
}
var port=a.ConnectorManager.Connectors.Cast<Connector>().OrderBy(c=>c.Origin.DistanceTo(joint)).First();
return new{firstId=a.Id.Value,secondId=b.Id.Value,connectorId=port.Id,pipeTypeId=pt.Value,levelId=level.Id.Value,sanitaryId=sanitary.Id.Value,
 otherId=other.Id.Value,startX=joint.X*304.8,startY=joint.Y*304.8,startZ=joint.Z*304.8,endX=end.X*304.8,endY=end.Y*304.8,endZ=end.Z*304.8};
'''


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    server = ROOT / "publish/server-issue12-review/RvtMcp.Server.exe"
    expected = hashlib.sha256((ROOT / "src/plugin-r27/bin/Release/net10.0-windows7.0/RvtMcp.Plugin.dll").read_bytes()).hexdigest().upper()
    client = smoke.Client(server)
    report = {"tested_at_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "server_sha256": hashlib.sha256(server.with_suffix(".dll").read_bytes()).hexdigest().upper()}
    fixture_started = False
    try:
        report["target"] = client.tool("revit_switch_target", {"version": "2027", "verify": True})
        report["runtime_before"] = before = client.code(RUNTIME)
        assert not before["modified"] and before["pluginSha256"] == expected, before
        catalog = client.request("tools/list", {})
        pipe_schema = next(t for t in catalog["tools"] if t["name"] == "revit_create_pipe")
        props = pipe_schema["inputSchema"]["properties"]
        assert all(p in props for p in ("systemTypeId", "startElementId", "startConnectorId")), pipe_schema
        assert not any(p in props for p in ("system_type_id", "start_element_id", "start_connector_id")), pipe_schema
        report.update(tool_count=len(catalog["tools"]), pipe_schema=pipe_schema)
        for label, source, count in (("fitting_regression", "fitting-regression.cs", 4), ("issue12_regression", "live-regression.cs", 17)):
            result = client.code((pathlib.Path(__file__).parent / source).read_text(encoding="utf-8-sig"))
            report[label] = result
            assert result["total"] == count and result["failed"] == 0, result
        report["raw_connectto_probe"] = client.code((pathlib.Path(__file__).parent / "fitting-probe.cs").read_text(encoding="utf-8-sig"))
        assert report["raw_connectto_probe"]["restored"], report["raw_connectto_probe"]
        assert len(report["raw_connectto_probe"]["results"]) == 18 and all(
            r["restored"] and r["error"] is None for r in report["raw_connectto_probe"]["results"]), report["raw_connectto_probe"]
        fixture_started = True
        report["fixture"] = fixture = client.code(SETUP)
        args = {key: fixture[key] for key in ("startX", "startY", "startZ", "endX", "endY", "endZ", "pipeTypeId", "levelId")}
        report["ambiguous"] = ambiguous = client.tool("revit_create_pipe", args)
        assert ambiguous.get("created") is False and ambiguous.get("reason") == "ambiguous_start_connector" and len(ambiguous["candidates"]) == 2, ambiguous
        report["selected"] = selected = client.tool("revit_create_pipe", {**args, "startElementId": fixture["firstId"], "startConnectorId": fixture["connectorId"]})
        assert selected.get("created") and selected.get("system_type_source") == "connector" and selected.get("system_type_id") == fixture["sanitaryId"], selected
        oracle = client.code('var a=(MEPCurve)doc.GetElement(new ElementId(' + str(fixture["firstId"]) + 'L));'
            'var b=(MEPCurve)doc.GetElement(new ElementId(' + str(selected["pipe_id"]) + 'L));'
            'var other=(MEPCurve)doc.GetElement(new ElementId(' + str(fixture["secondId"]) + 'L));'
            'return new{direct=a.ConnectorManager.Connectors.Cast<Connector>().Any(c=>b.ConnectorManager.Connectors.Cast<Connector>().Any(d=>c.IsConnectedTo(d))),'
            'otherStillOpen=other.ConnectorManager.Connectors.Cast<Connector>().All(c=>!c.IsConnected),system=b.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM).AsElementId().Value,diameter=b.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).AsDouble()*304.8};')
        report["selection_oracle"] = oracle
        assert oracle["direct"] and oracle["otherStillOpen"] and oracle["system"] == fixture["sanitaryId"] and abs(oracle["diameter"] - 101.6) < .01, oracle
        independent = {**args, "startY": args["startY"] + 20000, "endY": args["endY"] + 20000, "systemTypeId": fixture["otherId"]}
        report["explicit_type"] = explicit = client.tool("revit_create_pipe", independent)
        assert explicit.get("created") and explicit.get("system_type_source") == "explicit" and explicit.get("system_type_id") == fixture["otherId"] and not explicit.get("connected_to_start"), explicit
        report["mismatch"] = mismatch = client.tool("revit_connect_mep_elements", {"elementId1": fixture["secondId"], "elementId2": explicit["pipe_id"]})
        assert mismatch.get("connected") is False and mismatch.get("reason") == "system_type_mismatch", mismatch
        report["typed_pass"] = True
    except Exception as error:
        report["failure"] = str(error)
        raise
    finally:
        try:
            if fixture_started:
                bridge = OUT / ("reload-bridge-" + str(time.time_ns()) + ".rvt")
                cleanup = client.code('if(doc.Title!="Review-MEP-2027")throw new Exception("Unexpected active model");'
                    'var test=doc;var testPath=test.PathName;var bridge=app.Application.NewProjectDocument(UnitSystem.Metric);'
                    'bridge.SaveAs(@"' + str(bridge) + '");bridge.Close(false);'
                    'var activeBridge=app.OpenAndActivateDocument(@"' + str(bridge) + '").Document;test.Close(false);'
                    'var reopened=app.OpenAndActivateDocument(testPath).Document;activeBridge.Close(false);'
                    'return new{model=reopened.Title,modified=reopened.IsModified,fixturesDiscarded=true};')
                report["cleanup"] = cleanup
            report["runtime_after"] = after = client.code(RUNTIME)
            assert after["elementIdsSha256"] == before["elementIdsSha256"] and not after["modified"] and after["pid"] == before["pid"], after
        finally:
            (OUT / "review-typed-live.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
            client.close()
    print(json.dumps({"fitting": report["fitting_regression"]["failed"], "issue12": report["issue12_regression"]["failed"],
                      "typed_pass": report["typed_pass"], "runtime": report["runtime_after"]}, indent=2))


if __name__ == "__main__":
    main()
