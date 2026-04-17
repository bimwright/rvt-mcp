using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Bimwright.Plugin.Handlers
{
    public class ShowMessageHandler : IRevitCommand
    {
        public string Name => "show_message";
        public string Description => "Display a TaskDialog inside Revit with an optional custom message. Useful for connection testing, user notifications, and AI-to-user feedback during automation flows.";
        public string ParametersSchema => "{ \"message\": \"string (optional, dialog body text; default 'Hello from MCP! Connection successful.')\", \"title\": \"string (optional, dialog title; default 'Bimwright')\" }";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var title = "Bimwright";
            var message = "Hello from MCP! Connection successful.";

            if (!string.IsNullOrWhiteSpace(paramsJson))
            {
                try
                {
                    var request = JObject.Parse(paramsJson);

                    var customMessage = request.Value<string>("message");
                    if (!string.IsNullOrWhiteSpace(customMessage))
                        message = customMessage;

                    var customTitle = request.Value<string>("title");
                    if (!string.IsNullOrWhiteSpace(customTitle))
                        title = customTitle;
                }
                catch { }
            }

            TaskDialog.Show(title, message);
            return CommandResult.Ok(new { title, message });
        }
    }
}
