using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Reports.Model
{
    public enum ReportJsonReadStatus { Success, Corrupt, UnsupportedSchema }

    public static class ReportJson
    {
        public const int SchemaVersion = 1;
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            DateParseHandling = DateParseHandling.None,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            NullValueHandling = NullValueHandling.Include,
            TypeNameHandling = TypeNameHandling.None
        };

        public static string FormatUtc(DateTime value)
        {
            return value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        }

        public static string Serialize(AgentReport report)
        {
            return JsonConvert.SerializeObject(report, Settings);
        }

        public static bool TryDeserialize(string json, out AgentReport report, out ReportJsonReadStatus status)
        {
            report = null;
            status = ReportJsonReadStatus.Corrupt;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                using (var text = new StringReader(json))
                using (var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None })
                {
                    var root = JObject.Load(reader);
                    while (reader.Read())
                        if (reader.TokenType != JsonToken.Comment) return false;
                    var version = root["schema_version"];
                    if (version == null || version.Type != JTokenType.Integer) return false;
                    // Comparing integer text also handles a future version beyond Int64.
                    var value = version.ToString(Formatting.None);
                    if (value != "1")
                    {
                        if (value != "0" && !value.StartsWith("-", StringComparison.Ordinal))
                            status = ReportJsonReadStatus.UnsupportedSchema;
                        return false;
                    }
                    report = root.ToObject<AgentReport>(JsonSerializer.Create(Settings));
                    status = ReportJsonReadStatus.Success;
                    return true;
                }
            }
            catch (JsonException) { return false; }
            catch (ArgumentException) { return false; }
            catch (OverflowException) { return false; }
        }
    }
}
