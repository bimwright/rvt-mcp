using Autodesk.Revit.UI;

namespace RvtMcp.Plugin
{
    /// <summary>The only Revit API reads for health, called on the UI thread's Idling event.</summary>
    internal static class HostHealthObserver
    {
        internal static void Observe(UIApplication app)
        {
            try
            {
                if (app == null) return;
                var doc = app.ActiveUIDocument?.Document;
                HostHealthSnapshot.ObserveDocument(doc != null, doc?.Title, doc?.IsFamilyDocument);
            }
            catch { HostHealthSnapshot.ForgetDocument(); }
        }
    }
}
