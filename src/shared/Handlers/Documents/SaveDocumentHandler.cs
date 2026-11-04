using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public sealed class SaveDocumentHandler : IRevitCommand
    {
        public string Name => "save_document";
        public string Description => "Save the active local document with its file-version and backup safety checks.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""allow_format_upgrade"":{""type"":""boolean""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var args = JObject.Parse(paramsJson);
            var prep = DocumentLifecycleSupport.Prepare(app, app.ActiveUIDocument?.Document, args.Value<bool?>("allow_format_upgrade") ?? false, false);
            if (prep.Error != null && prep.Error != "no_changes") return CommandResult.Fail(prep.Error);
            return CommandResult.Ok(DocumentLifecycleSupport.Save(app, prep).ToDto());
        }
    }
}
