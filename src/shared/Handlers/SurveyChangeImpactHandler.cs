using System;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Survey;

namespace RvtMcp.Plugin.Handlers
{
    public sealed class SurveyChangeImpactHandler : IRevitCommand
    {
        public string Name => "survey_change_impact";
        public string Description => "Read bounded change-impact evidence for explicit active-document targets; incomplete checks remain explicit.";
        public string ParametersSchema => SurveyContract.ParametersSchema;

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            if (app.ActiveUIDocument?.Document == null)
                return CommandResult.Fail("No document is open.");
            try
            {
                return CommandResult.Ok(SurveyEngine.Run(app, JObject.Parse(paramsJson)));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is JsonException || ex is OverflowException)
            {
                return CommandResult.Fail("Invalid survey input: " + ex.Message);
            }
        }
    }
}
