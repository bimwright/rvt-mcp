using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace RvtMcp.Tests;

public class SavedViewOrientationRefreshTests
{
    [Theory]
    [InlineData("undo")]
    [InlineData("redo")]
    [InlineData("group_rollback_without_ids")]
    [InlineData("cancelled_close")]
    [InlineData("failed_close")]
    [InlineData("cancelled_close_with_pending")]
    public void Replay_restores_temporary_camera_from_saved_state(string scenario)
    {
        var values = Run(scenario);
        Assert.Equal(1.0, values[0]);
        Assert.Equal(1, values[1]);
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("unchanged_saved_orientation")]
    [InlineData("untracked_view")]
    [InlineData("unrelated_undo_after_commit")]
    public void Unrelated_changes_preserve_manual_temporary_orientation(string scenario)
    {
        var values = Run(scenario);
        Assert.Equal(90.0, values[0]);
        Assert.Equal(0, values[1]);
    }

    [Fact]
    public void Successful_close_does_not_hash_invalid_document_or_forget_other_document()
    {
        var values = Run("successful_close");
        Assert.Equal(1.0, values[0]);
        Assert.Equal(1, values[1]);
    }

    private static object[] Run(string scenario, [CallerFilePath] string file = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", "..", ".."));
        var production = File.ReadAllText(Path.Combine(root, "src/shared/Handlers/Views/SavedViewOrientationRefresh.cs"));
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("OrientationRefresh_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(production), CSharpSyntaxTree.ParseText(ApiSeam) }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return (object[])Assembly.Load(stream.ToArray()).GetType("Probe")!.GetMethod("Run")!
            .Invoke(null, new object[] { scenario })!;
    }

    // The production callback runs unchanged. Doubles separate saved data from temporary camera
    // state, and model native invalid-document hashing and cancelled/failed close notifications.
    private const string ApiSeam = """
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using RvtMcp.Plugin.Handlers;
namespace Autodesk.Revit.DB {
 public enum UndoOperation {TransactionCommitted,TransactionUndone,TransactionRedone,TransactionGroupRolledBack}
 public sealed class ElementId {
  public int Value; public ElementId(int value){Value=value;}
  public override int GetHashCode()=>Value;
  public override bool Equals(object other)=>other is ElementId id && id.Value==Value;
 }
 public sealed class XYZ {
  public double X; public XYZ(double x){X=x;}
  public bool IsAlmostEqualTo(XYZ other)=>Math.Abs(X-other.X)<1e-9;
 }
 public sealed class ViewOrientation3D {
  public XYZ EyePosition,ForwardDirection=new XYZ(0),UpDirection=new XYZ(1);
  public ViewOrientation3D(double x){EyePosition=new XYZ(x);}
 }
 public sealed class Document {
  public bool IsValidObject=true;
  public Dictionary<ElementId,View3D> Views=new Dictionary<ElementId,View3D>();
  public View3D GetElement(ElementId id)=>Views.TryGetValue(id,out var view)?view:null;
  public override int GetHashCode()=>IsValidObject?1:throw new Exception("invalid_document_hash");
 }
 public sealed class View3D {
  public Document Document; public ElementId Id; public bool IsLocked;
  public ViewOrientation3D Saved=new ViewOrientation3D(1),Current=new ViewOrientation3D(1);
  public int Refreshes;
  public View3D(Document doc,int id){Document=doc;Id=new ElementId(id);doc.Views.Add(Id,this);}
  public ViewOrientation3D GetSavedOrientation()=>Saved;
  public void SetOrientation(ViewOrientation3D value){if(IsLocked)throw new Autodesk.Revit.Exceptions.InvalidOperationException();Current=value;Refreshes++;}
 }
}
namespace Autodesk.Revit.Exceptions {public class InvalidOperationException:Exception{}}
namespace Autodesk.Revit.DB.Events {
 public class DocumentClosingEventArgs:EventArgs{public Document Document;}
 public class DocumentClosedEventArgs:EventArgs{}
 public class DocumentChangedEventArgs:EventArgs {
  public Document Document; public UndoOperation Operation; public ElementId[] Ids=Array.Empty<ElementId>();
  public Document GetDocument()=>Document;
  public ICollection<ElementId> GetModifiedElementIds()=>Ids;
  public ICollection<ElementId> GetAddedElementIds()=>Array.Empty<ElementId>();
 }
}
namespace Autodesk.Revit.UI.Events {public class IdlingEventArgs:EventArgs{}}
namespace Autodesk.Revit.UI {
 public class Application {
  public event EventHandler<DocumentChangedEventArgs> DocumentChanged;
  public event EventHandler<DocumentClosingEventArgs> DocumentClosing;
  public event EventHandler<DocumentClosedEventArgs> DocumentClosed;
  public void Changed(Document doc,UndoOperation op,params ElementId[] ids)=>DocumentChanged?.Invoke(this,new DocumentChangedEventArgs{Document=doc,Operation=op,Ids=ids});
  public void Close(Document doc,bool success){DocumentClosing?.Invoke(this,new DocumentClosingEventArgs{Document=doc});doc.IsValidObject=!success;DocumentClosed?.Invoke(this,new DocumentClosedEventArgs());}
 }
 public class UIApplication {
  public Application Application=new Application();
  public event EventHandler<Autodesk.Revit.UI.Events.IdlingEventArgs> Idling;
  public void Idle()=>Idling?.Invoke(this,new Autodesk.Revit.UI.Events.IdlingEventArgs());
 }
}
namespace RvtMcp.Plugin {public static class App{public static void DebugLog(string message){}}}
public static class Probe {
 public static object[] Run(string scenario){
  var app=new UIApplication();var doc=new Document();var view=new View3D(doc,1);
  SavedViewOrientationRefresh.Track(app,view);
  if(scenario=="successful_close"){
   var other=new Document();var live=new View3D(other,1);
   SavedViewOrientationRefresh.Track(app,live);
   app.Application.Close(doc,true);
   doc=other;view=live;
  }
  if(scenario=="untracked_view")view=new View3D(doc,2);
  if(scenario=="commit" || scenario=="unrelated_undo_after_commit"){
   view.Saved=new ViewOrientation3D(2);view.Current=new ViewOrientation3D(90);
   app.Application.Changed(doc,UndoOperation.TransactionCommitted,view.Id);
   if(scenario=="unrelated_undo_after_commit")app.Application.Changed(doc,UndoOperation.TransactionUndone,view.Id);
  }else if(scenario=="unchanged_saved_orientation" || scenario=="untracked_view"){
   view.Current=new ViewOrientation3D(90);
   app.Application.Changed(doc,UndoOperation.TransactionUndone,view.Id);
  }else{
   view.Saved=new ViewOrientation3D(2);view.Current=view.Saved;
   app.Application.Changed(doc,UndoOperation.TransactionCommitted,view.Id);
   if(scenario=="cancelled_close" || scenario=="failed_close")app.Application.Close(doc,false);
   view.Saved=new ViewOrientation3D(1);
   var op=scenario=="redo"?UndoOperation.TransactionRedone:scenario=="group_rollback_without_ids"?UndoOperation.TransactionGroupRolledBack:UndoOperation.TransactionUndone;
   app.Application.Changed(doc,op,scenario=="group_rollback_without_ids"?Array.Empty<ElementId>():new[]{view.Id});
   if(scenario=="cancelled_close_with_pending")app.Application.Close(doc,false);
  }
  app.Idle();app.Idle();
  return new object[]{view.Current.EyePosition.X,view.Refreshes};
 }
}
""";
}
