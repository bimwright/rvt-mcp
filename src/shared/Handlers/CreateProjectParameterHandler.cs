using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateProjectParameterHandler : IRevitCommand
    {
        public string Name => "create_project_parameter";
        public string Description => "Create a pure project parameter and bind it to categories.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""name"", ""dataTypeId"", ""categories""],
  ""properties"": {
    ""name"": { ""type"": ""string"" },
    ""dataTypeId"": { ""type"": ""string"" },
    ""categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""bindingKind"": {
      ""type"": ""string"",
      ""enum"": [""instance"", ""type""],
      ""default"": ""instance""
    },
    ""parameterGroupId"": {
      ""type"": ""string""
    },
    ""allowIfApiUnsupported"": {
      ""type"": ""boolean"",
      ""default"": false,
      ""description"": ""Must remain false unless the implementation verifies a pure project parameter API path.""
    }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            // Explicitly fail under the unverified Revit public API constraints
            return CommandResult.Fail("create_project_parameter requires a verified pure project parameter API path for Revit R22-R27; use create_shared_parameter + bind_shared_parameter for shared-backed parameters.");
        }
    }
}
