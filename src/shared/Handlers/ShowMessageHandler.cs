using System;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ShowMessageHandler : IRevitCommand
    {
        public string Name => "show_message";
        public string Description => "Display a TaskDialog inside Revit without echoing an unbounded message by default.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""message"":{""type"":""string""},""title"":{""type"":""string""},""echo_message"":{""type"":""boolean"",""default"":false},""max_echo_chars"":{""type"":""integer"",""default"":1024,""minimum"":1,""maximum"":4096}}}";

        // Wire contract stays English even when the UI shows a localized/override body.
        private const string DefaultWireMessage = "Hello from MCP! Connection successful.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var title = "RvtMcp";
            var displayMessage = Localization.L.T("showMessage.defaultBody");
            var wireMessage = DefaultWireMessage;
            var echoMessage = false;
            var maxEchoChars = 1024;

            if (!string.IsNullOrWhiteSpace(paramsJson))
            {
                try
                {
                    var request = JObject.Parse(paramsJson);
                    var customMessage = request.Value<string>("message");
                    if (!string.IsNullOrWhiteSpace(customMessage))
                    {
                        displayMessage = customMessage;
                        wireMessage = customMessage;
                    }
                    var customTitle = request.Value<string>("title");
                    if (!string.IsNullOrWhiteSpace(customTitle))
                        title = customTitle;
                    echoMessage = request.Value<bool?>("echo_message") ?? false;
                    maxEchoChars = request.Value<int?>("max_echo_chars") ?? 1024;
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail("Invalid JSON parameters: " + ex.Message);
                }
            }

            if (maxEchoChars < 1 || maxEchoChars > 4096)
                return CommandResult.Fail("max_echo_chars must be between 1 and the hard maximum of 4096.");

            TaskDialog.Show(title, displayMessage);
            var echoedMessage = echoMessage
                ? wireMessage.Substring(0, Math.Min(wireMessage.Length, maxEchoChars))
                : null;
            var titlePreview = title.Substring(0, Math.Min(title.Length, 256));

            return CommandResult.Ok(new
            {
                displayed = true,
                title = titlePreview,
                title_truncated = titlePreview.Length < title.Length,
                message_char_count = wireMessage.Length,
                message = echoedMessage,
                message_truncated = echoMessage && echoedMessage.Length < wireMessage.Length
            });
        }
    }
}
