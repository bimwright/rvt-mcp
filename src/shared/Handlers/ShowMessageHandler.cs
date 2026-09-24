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

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var title = "RvtMcp";
            var message = Localization.L.T("showMessage.defaultBody");
            var echoMessage = false;
            var maxEchoChars = 1024;

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

            TaskDialog.Show(title, message);
            var echoedMessage = echoMessage
                ? message.Substring(0, Math.Min(message.Length, maxEchoChars))
                : null;
            var titlePreview = title.Substring(0, Math.Min(title.Length, 256));

            return CommandResult.Ok(new
            {
                displayed = true,
                title = titlePreview,
                title_truncated = titlePreview.Length < title.Length,
                message_char_count = message.Length,
                message = echoedMessage,
                message_truncated = echoMessage && echoedMessage.Length < message.Length
            });
        }
    }
}
