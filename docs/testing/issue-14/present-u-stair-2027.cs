using Autodesk.Revit.DB.Architecture;
var f = new RvtMcp.Plugin.SafeFailuresPreprocessor();
var view = (View3D)doc.GetElement(new ElementId(3327923L));
var stair = (Stairs)doc.GetElement(new ElementId(3327830L));
using(var tx = new Transaction(doc, "U stair: pipe railing and clear isometric crop"))
{
 tx.Start();
 tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(f).SetClearAfterRollback(true));
 foreach(var id in stair.GetAssociatedRailings()) doc.GetElement(id).ChangeTypeId(new ElementId(51388L));
 var centre = (view.GetSectionBox().Min + view.GetSectionBox().Max) / 2;
 var forward = new XYZ(24, 30, -38).Normalize();
 var right = forward.CrossProduct(XYZ.BasisZ).Normalize();
 var up = right.CrossProduct(forward).Normalize();
 view.SetOrientation(new ViewOrientation3D(centre - forward * 50, up, forward));
 view.DisplayStyle = DisplayStyle.HLR;
 doc.Regenerate();
 var crop = view.CropBox;
 var inverse = crop.Transform.Inverse;
 var ids = new List<ElementId>{stair.Id};
 ids.AddRange(stair.GetAssociatedRailings());
 var points = new List<XYZ>();
 foreach(var id in ids)
 {
  var bb = doc.GetElement(id).get_BoundingBox(null);
  if(bb == null) continue;
  foreach(double x in new []{bb.Min.X,bb.Max.X})
   foreach(double y in new []{bb.Min.Y,bb.Max.Y})
    foreach(double z in new []{bb.Min.Z,bb.Max.Z}) points.Add(inverse.OfPoint(bb.Transform.OfPoint(new XYZ(x,y,z))));
 }
 double margin = 0.6;
 crop.Min = new XYZ(points.Min(p=>p.X)-margin,points.Min(p=>p.Y)-margin,crop.Min.Z);
 crop.Max = new XYZ(points.Max(p=>p.X)+margin,points.Max(p=>p.Y)+margin,crop.Max.Z);
 view.CropBox = crop;
 view.CropBoxActive = true;
 view.CropBoxVisible = false;
 var status=tx.Commit();
 if(status != TransactionStatus.Committed || f.HadErrors) throw new InvalidOperationException("Presentation transaction failed.");
}
uidoc.ActiveView=view;
uidoc.GetOpenUIViews().First(v=>v.ViewId==view.Id).ZoomToFit();
var o = new ImageExportOptions { FilePath=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(doc.PathName), "u-stair-final"),ExportRange=ExportRange.SetOfViews,ZoomType=ZoomFitType.FitToPage,PixelSize=1800,HLRandWFViewsFileType=ImageFileType.PNG,ShadowViewsFileType=ImageFileType.PNG,ImageResolution=ImageResolution.DPI_150 };
o.SetViewsAndSheets(new List<ElementId>{view.Id});
doc.ExportImage(o);
return new { status="committed", railingType="Handrail - Pipe", f.HadWarnings,f.HadErrors,f.Messages };
