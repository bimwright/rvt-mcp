using System;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Lists one Revit API type as loaded in this Revit (members, factories, enum values, base types, subtypes), so an
    /// agent can write a send_code script against the real API of this version. Reads no document and changes nothing.
    /// </summary>
    public sealed class DescribeApiHandler : IRevitCommand
    {
        public string Name => "describe_api";
        public string Description => "List the members of one Revit API type in the running Revit; reads no document.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""type"":{""type"":""string""},""member"":{""type"":""string""}},""required"":[""type""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = JObject.Parse(string.IsNullOrWhiteSpace(paramsJson) ? "{}" : paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Invalid JSON parameters: " + ex.Message); }

            var result = ApiDescriber.Describe(
                new[] { typeof(Autodesk.Revit.DB.Document).Assembly, typeof(UIApplication).Assembly },
                request.Value<string>("type"), request.Value<string>("member"));
            if (result.Error != null) return CommandResult.Fail(result.Error);
            var value = (System.Collections.Generic.Dictionary<string, object>)result.Value;
            value["revit_version"] = app.Application.VersionNumber;
            return CommandResult.Ok(value);
        }
    }
}
