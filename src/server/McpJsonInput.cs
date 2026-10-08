using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bimwright.Targeting;
using RvtMcp.Plugin; // RvtMcpConfig
using RvtMcp.Server.Bake;
using RvtMcp.Server.Handlers;
using RvtMcp.Server.Prompts;
using RvtMcp.ToolCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    internal static class McpJsonInput
    {
        // MCP binds untyped array items to System.Text.Json.JsonElement. Newtonsoft
        // must read their JSON contents, including elements nested in native objects.
        public static JToken FromObject(object value)
        {
            var serializer = Newtonsoft.Json.JsonSerializer.CreateDefault();
            serializer.Converters.Add(new JsonElementConverter());
            return JToken.FromObject(value, serializer);
        }

        internal sealed class JsonElementConverter : Newtonsoft.Json.JsonConverter<JsonElement>
        {
            public override bool CanRead => false;

            public override void WriteJson(JsonWriter writer, JsonElement value, Newtonsoft.Json.JsonSerializer serializer)
            {
                if (value.ValueKind == JsonValueKind.Undefined || value.ValueKind == JsonValueKind.Null)
                    writer.WriteNull();
                else
                    JToken.Parse(value.GetRawText()).WriteTo(writer);
            }

            public override JsonElement ReadJson(JsonReader reader, Type objectType, JsonElement existingValue,
                bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer) => throw new NotSupportedException();
        }

        public static JArray RequiredArray(object value, string parameterName)
        {
            var array = OptionalArray(value, parameterName);
            if (array == null)
                throw new ArgumentException($"{parameterName} must be a JSON array.");
            return array;
        }

        public static JArray OptionalArray(object value, string parameterName)
        {
            if (value == null)
                return null;

            if (value is JArray jArray)
                return jArray;

            if (value is JToken token)
            {
                if (token.Type == JTokenType.Null)
                    return null;
                if (token.Type == JTokenType.Array)
                    return (JArray)token;
                throw new ArgumentException($"{parameterName} must be a JSON array when supplied.");
            }

            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
                    return null;
                if (element.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException($"{parameterName} must be a JSON array when supplied.");
                return JArray.Parse(element.GetRawText());
            }

            if (value is string)
                throw new ArgumentException($"{parameterName} must be a JSON array, not a string.");

            var tokenFromObject = FromObject(value);
            if (tokenFromObject.Type == JTokenType.Array)
                return (JArray)tokenFromObject;

            throw new ArgumentException($"{parameterName} must be a JSON array when supplied.");
        }

        public static JObject OptionalObject(object value, string parameterName)
        {
            if (value == null)
                return null;

            if (value is JObject jObject)
                return jObject;

            if (value is JToken token)
            {
                if (token.Type == JTokenType.Null)
                    return null;
                if (token.Type == JTokenType.Object)
                    return (JObject)token;
                throw new ArgumentException($"{parameterName} must be a JSON object when supplied.");
            }

            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
                    return null;
                if (element.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException($"{parameterName} must be a JSON object when supplied.");
                return JObject.Parse(element.GetRawText());
            }

            if (value is string)
                throw new ArgumentException($"{parameterName} must be a JSON object, not a string.");

            var tokenFromObject = FromObject(value);
            if (tokenFromObject.Type == JTokenType.Object)
                return (JObject)tokenFromObject;

            throw new ArgumentException($"{parameterName} must be a JSON object when supplied.");
        }
    }
}
