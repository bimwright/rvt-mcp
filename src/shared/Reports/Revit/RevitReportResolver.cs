using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RvtMcp.Plugin.Reports.Binding;
using RvtMcp.Plugin.Reports.Model;

namespace RvtMcp.Plugin.Reports.Revit
{
    public sealed class RevitReportResolver : IReportResolver
    {
        private readonly UIApplication _app;

        public RevitReportResolver(UIApplication app) { _app = app; }

        public ReportDocument ActiveDocument
        {
            get
            {
                var document = _app.ActiveUIDocument?.Document;
                return document == null ? null : McpChangeTracker.DescribeForReport(document);
            }
        }

        public ReportDocument FindDocument(string token)
        {
            var document = DocumentTargetGuard.TryFind(token);
            return document == null ? null : McpChangeTracker.DescribeForReport(document);
        }

        public ReportElement FindElement(string token, long id)
        {
            var document = DocumentTargetGuard.TryFind(token);
            if (document == null || !RevitCompat.CanRepresentElementId(id)) return null;
            var element = document.GetElement(RevitCompat.ToElementId(id));
            if (element == null) return null;

            Category categoryObject = null;
            string category = null;
            string name = null;
            try { categoryObject = element.Category; } catch (Exception) { }
            try { category = categoryObject?.Name; } catch (Exception) { }
            try
            {
                name = element.Name;
                if (name != null && name.Length > 120) name = name.Substring(0, 120);
            }
            catch (Exception) { }

            var kind = element is ViewSheet ? ReportElementKinds.Sheet
                : element is View view && !view.IsTemplate ? ReportElementKinds.View
                : element is ElementType ? ReportElementKinds.Type
                : categoryObject != null ? ReportElementKinds.Element : ReportElementKinds.Other;
            return new ReportElement
            {
                Id = RevitCompat.GetId(element.Id),
                UniqueId = element.UniqueId,
                Kind = kind,
                Category = category,
                Name = name
            };
        }

        public ReportView FindView(string token, long id)
        {
            var document = DocumentTargetGuard.TryFind(token);
            if (document == null || !RevitCompat.CanRepresentElementId(id)) return null;
            var view = document.GetElement(RevitCompat.ToElementId(id)) as View;
            return view == null || view.IsTemplate ? null : new ReportView { Id = RevitCompat.GetId(view.Id), UniqueId = view.UniqueId };
        }
    }
}
