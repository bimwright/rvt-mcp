using System; using System.Linq; using System.Collections.Generic; using Autodesk.Revit.DB; using Autodesk.Revit.UI;
public class McpDynamicScript { public static CopyPasteOptions Options(){var o=new CopyPasteOptions();o.SetDuplicateTypeNamesHandler(new DestinationTypes());return o;} public static object Run(UIApplication app){var doc=app.ActiveUIDocument.Document;
var before=new FilteredElementCollector(doc).WherePasses(new LogicalOrFilter(new ElementIsElementTypeFilter(),new ElementIsElementTypeFilter(true))).ToElementIds().Select(i=>i.Value).OrderBy(i=>i).ToArray();
var modified=doc.IsModified;var results=new List<object>();
using(var group=new TransactionGroup(doc,"Issue13 baseline")){
group.Start();try{
var ld=new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().Select(l=>l.GetLinkDocument()).First(d=>d!=null&&d.Title.Contains("Architectural"));
var original=new FilteredElementCollector(ld).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().First(s=>s.FamilyName=="Door-Passage-Single-Flush");
FamilySymbol symbol=null;Wall wall=null;var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l=>Math.Abs(l.Elevation)).First();
var point=new XYZ(2500/304.8,2000/304.8,level.Elevation);
using(var tx=new Transaction(doc,"fixture")){tx.Start();var ids=ElementTransformUtils.CopyElements(ld,new[]{original.Id},doc,Transform.Identity,Options());symbol=ids.Select(id=>doc.GetElement(id)).OfType<FamilySymbol>().First();symbol.Activate();wall=Wall.Create(doc,Line.CreateBound(new XYZ(0,2000/304.8,level.Elevation),new XYZ(5000/304.8,2000/304.8,level.Elevation)),level.Id,false);tx.Commit();}
for(int i=0;i<2;i++){
var request=Newtonsoft.Json.JsonConvert.SerializeObject(new{typeId=symbol.Id.Value,x=2500,y=2000,z=level.Elevation*304.8,level=level.Name});
var r=new RvtMcp.Plugin.Handlers.CreatePointBasedElementHandler().Execute(app,request);
var id=Newtonsoft.Json.Linq.JObject.FromObject(r.Data).Value<long>("elementId");var fi=(FamilyInstance)doc.GetElement(new ElementId(id));var p=((LocationPoint)fi.Location).Point;
results.Add(new{mode="old handler",r.Success,host=fi.Host?.Id.Value,position=new[]{p.X*304.8,p.Y*304.8,p.Z*304.8}});
}
using(var tx=new Transaction(doc,"host overload")){tx.Start();var fi=doc.Create.NewFamilyInstance(point,symbol,wall,level,Autodesk.Revit.DB.Structure.StructuralType.NonStructural);doc.Regenerate();var p=((LocationPoint)fi.Location).Point;results.Add(new{mode="host overload",host=fi.Host?.Id.Value,wall=wall.Id.Value,position=new[]{p.X*304.8,p.Y*304.8,p.Z*304.8}});tx.Commit();}
}finally{group.RollBack();}}
return new{results,restored=before.SequenceEqual(new FilteredElementCollector(doc).WherePasses(new LogicalOrFilter(new ElementIsElementTypeFilter(),new ElementIsElementTypeFilter(true))).ToElementIds().Select(i=>i.Value).OrderBy(i=>i))&&modified==doc.IsModified,modified=doc.IsModified};
}}
public class DestinationTypes:IDuplicateTypeNamesHandler {public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)=>DuplicateTypeAction.UseDestinationTypes;}
