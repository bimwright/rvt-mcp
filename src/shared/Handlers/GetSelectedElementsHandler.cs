using System.Linq;
using Autodesk.Revit.UI;

namespace Bimwright.Plugin.Handlers
{
    public class GetSelectedElementsHandler : IRevitCommand
    {
        public string Name => "get_selected_elements";
        public string Description => "Get currently selected elements";
        public string ParametersSchema => "{}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
                return CommandResult.Fail("No document is open.");

            var selectedIds = uidoc.Selection.GetElementIds();
            if (selectedIds.Count == 0)
                return CommandResult.Ok(new { count = 0, elements = new object[0] });

            var doc = uidoc.Document;
            var elements = selectedIds.Select(id =>
            {
                var el = doc.GetElement(id);
                return new
                {
                    elementId = RevitCompat.GetId(id),
                    name = el.Name,
                    category = el.Category?.Name,
                    typeName = doc.GetElement(el.GetTypeId())?.Name
                };
            }).ToArray();

            return CommandResult.Ok(new { count = elements.Length, elements });
        }
    }
}
