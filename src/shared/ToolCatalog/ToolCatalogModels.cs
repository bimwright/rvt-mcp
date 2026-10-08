using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.ToolCatalog
{
    /// <summary>
    /// Stable metadata sent by the server for the built-in tools that are exposed in
    /// the current session. This deliberately contains no parameter schema, prompt
    /// body, token, or filesystem data.
    /// </summary>
    public sealed class ToolCatalog
    {
        public const int SupportedSchemaVersion = 1;

        [JsonProperty("schema_version")]
        public int SchemaVersion { get; }

        [JsonProperty("server_version")]
        public string ServerVersion { get; }

        [JsonProperty("catalogued_built_in_count")]
        public int CataloguedBuiltInCount { get; }

        [JsonProperty("tools")]
        public IReadOnlyList<ToolCatalogEntry> Tools { get; }

        [JsonConstructor]
        public ToolCatalog(
            int schemaVersion,
            string serverVersion,
            int cataloguedBuiltInCount,
            IReadOnlyList<ToolCatalogEntry> tools)
        {
            SchemaVersion = schemaVersion;
            ServerVersion = serverVersion ?? string.Empty;
            CataloguedBuiltInCount = cataloguedBuiltInCount;
            Tools = tools == null
                ? Array.Empty<ToolCatalogEntry>()
                : new List<ToolCatalogEntry>(tools).AsReadOnly();
        }
    }

    public sealed class ToolCatalogEntry
    {
        [JsonProperty("mcp_name")]
        public string McpName { get; }

        [JsonProperty("toolset")]
        public string Toolset { get; }

        [JsonProperty("short_description_en")]
        public string ShortDescriptionEn { get; }

        [JsonProperty("summary_key")]
        public string SummaryKey { get; }

        [JsonProperty("timeout")]
        public TimeoutPolicy Timeout { get; }

        [JsonConstructor]
        public ToolCatalogEntry(
            string mcpName,
            string toolset,
            string shortDescriptionEn,
            string summaryKey,
            TimeoutPolicy timeout)
        {
            McpName = mcpName ?? string.Empty;
            Toolset = toolset ?? string.Empty;
            ShortDescriptionEn = shortDescriptionEn ?? string.Empty;
            SummaryKey = summaryKey;
            Timeout = timeout;
        }
    }

    public sealed class TimeoutPolicy
    {
        [JsonProperty("default_seconds", NullValueHandling = NullValueHandling.Include)]
        public int? DefaultSeconds { get; }

        [JsonProperty("parameter_default_seconds", NullValueHandling = NullValueHandling.Include)]
        public int? ParameterDefaultSeconds { get; }

        [JsonProperty("parameter_minimum_seconds", NullValueHandling = NullValueHandling.Include)]
        public int? ParameterMinimumSeconds { get; }

        [JsonProperty("parameter_maximum_seconds", NullValueHandling = NullValueHandling.Include)]
        public int? ParameterMaximumSeconds { get; }

        [JsonProperty("transport_grace_seconds")]
        public int TransportGraceSeconds { get; }

        [JsonProperty("policy_kind")]
        public string PolicyKind { get; }

        [JsonConstructor]
        public TimeoutPolicy(
            int? defaultSeconds,
            int? parameterDefaultSeconds,
            int? parameterMinimumSeconds,
            int? parameterMaximumSeconds,
            int transportGraceSeconds,
            string policyKind)
        {
            DefaultSeconds = defaultSeconds;
            ParameterDefaultSeconds = parameterDefaultSeconds;
            ParameterMinimumSeconds = parameterMinimumSeconds;
            ParameterMaximumSeconds = parameterMaximumSeconds;
            TransportGraceSeconds = transportGraceSeconds;
            PolicyKind = policyKind ?? string.Empty;
        }
    }

    public enum ToolCatalogStatus
    {
        NotConnected,
        ConnectedNoCatalog,
        Invalid,
        Current
    }

    public sealed class ToolCatalogValidationResult
    {
        private ToolCatalogValidationResult(bool valid, ToolCatalog catalog, string error)
        {
            IsValid = valid;
            Catalog = catalog;
            Error = error;
        }

        public bool IsValid { get; }
        public ToolCatalog Catalog { get; }
        public string Error { get; }

        public static ToolCatalogValidationResult Valid(ToolCatalog catalog)
            => new ToolCatalogValidationResult(true, catalog, null);

        public static ToolCatalogValidationResult Invalid(string error)
            => new ToolCatalogValidationResult(false, null, error ?? "Catalog is invalid.");
    }

    /// <summary>
    /// Parser and validator shared by the plugin transports and tests. Parsing is
    /// explicit so unknown JSON cannot smuggle arbitrary data into the UI model.
    /// </summary>
    public static class ToolCatalogCodec
    {
        private const int ExpectedTransportGraceSeconds = 5;

        public static ToolCatalogValidationResult ParseAndValidate(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return ToolCatalogValidationResult.Invalid("Catalog payload is empty.");

            try
            {
                var root = JObject.Parse(json);
                return ParseAndValidate(root);
            }
            catch (JsonException)
            {
                return ToolCatalogValidationResult.Invalid("Catalog payload is not valid JSON.");
            }
            catch (Exception)
            {
                return ToolCatalogValidationResult.Invalid("Catalog payload could not be read.");
            }
        }

        public static ToolCatalogValidationResult ParseAndValidate(JObject root)
        {
            if (root == null)
                return ToolCatalogValidationResult.Invalid("Catalog payload must be an object.");

            try
            {
                var schemaVersion = root.Value<int?>("schema_version");
                if (!schemaVersion.HasValue || schemaVersion.Value != ToolCatalog.SupportedSchemaVersion)
                    return ToolCatalogValidationResult.Invalid("Unsupported catalog schema version.");

            var serverVersion = root.Value<string>("server_version");
            if (string.IsNullOrWhiteSpace(serverVersion))
                return ToolCatalogValidationResult.Invalid("Catalog server_version is required.");

            var cataloguedCount = root.Value<int?>("catalogued_built_in_count");
            if (!cataloguedCount.HasValue || cataloguedCount.Value < 0)
                return ToolCatalogValidationResult.Invalid("Catalogued built-in count is invalid.");

            var toolsToken = root["tools"] as JArray;
            if (toolsToken == null)
                return ToolCatalogValidationResult.Invalid("Catalog tools is required.");

            var tools = new List<ToolCatalogEntry>(toolsToken.Count);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in toolsToken)
            {
                var entryObject = token as JObject;
                if (entryObject == null)
                    return ToolCatalogValidationResult.Invalid("Catalog contains a non-object tool entry.");

                var mcpName = entryObject.Value<string>("mcp_name");
                if (!IsCanonicalName(mcpName) || !names.Add(mcpName))
                    return ToolCatalogValidationResult.Invalid("Catalog contains an empty, malformed, or duplicate tool name.");

                var toolset = entryObject.Value<string>("toolset");
                if (string.IsNullOrWhiteSpace(toolset))
                    return ToolCatalogValidationResult.Invalid("Catalog toolset is required.");

                var description = entryObject.Value<string>("short_description_en");
                if (string.IsNullOrWhiteSpace(description) || ScalarLength(description) > 160)
                    return ToolCatalogValidationResult.Invalid("Catalog tool descriptions must be 1-160 Unicode scalar values.");

                var timeoutObject = entryObject["timeout"] as JObject;
                var timeoutResult = ParseTimeout(timeoutObject);
                if (!timeoutResult.IsValid)
                    return ToolCatalogValidationResult.Invalid(timeoutResult.Error);

                tools.Add(new ToolCatalogEntry(
                    mcpName,
                    toolset,
                    description,
                    entryObject.Value<string>("summary_key"),
                    timeoutResult.Timeout));
            }

            if (cataloguedCount.Value < tools.Count)
                return ToolCatalogValidationResult.Invalid("Catalogued built-in count is smaller than the exposed tool count.");

                return ToolCatalogValidationResult.Valid(new ToolCatalog(
                    schemaVersion.Value,
                    serverVersion.Trim(),
                    cataloguedCount.Value,
                    tools));
            }
            catch (Exception)
            {
                return ToolCatalogValidationResult.Invalid("Catalog contains a field with an invalid type.");
            }
        }

        private static TimeoutValidationResult ParseTimeout(JObject timeout)
        {
            if (timeout == null)
                return TimeoutValidationResult.Invalid("Catalog timeout policy is required.");

            var kind = timeout.Value<string>("policy_kind");
            if (kind != "fixed" && kind != "long_run_parameter" && kind != "server_local")
                return TimeoutValidationResult.Invalid("Catalog timeout policy kind is invalid.");

            var grace = timeout.Value<int?>("transport_grace_seconds");
            if (!grace.HasValue || grace.Value != ExpectedTransportGraceSeconds)
                return TimeoutValidationResult.Invalid("Catalog transport grace is invalid.");

            var defaultSeconds = timeout.Value<int?>("default_seconds");
            var parameterDefault = timeout.Value<int?>("parameter_default_seconds");
            var minimum = timeout.Value<int?>("parameter_minimum_seconds");
            var maximum = timeout.Value<int?>("parameter_maximum_seconds");

            if (kind == "fixed")
            {
                if (!defaultSeconds.HasValue || defaultSeconds.Value < 1 || defaultSeconds.Value > 900
                    || parameterDefault.HasValue || minimum.HasValue || maximum.HasValue)
                    return TimeoutValidationResult.Invalid("Fixed timeout policy is invalid.");
            }
            else if (kind == "long_run_parameter")
            {
                if (defaultSeconds.HasValue || !parameterDefault.HasValue || !minimum.HasValue || !maximum.HasValue
                    || minimum.Value < 1 || maximum.Value > 900 || minimum.Value > maximum.Value
                    || parameterDefault.Value < minimum.Value || parameterDefault.Value > maximum.Value)
                    return TimeoutValidationResult.Invalid("Long-run timeout policy is invalid.");
            }
            else if (defaultSeconds.HasValue || parameterDefault.HasValue || minimum.HasValue || maximum.HasValue)
            {
                return TimeoutValidationResult.Invalid("Server-local timeout policy must not expose a duration.");
            }

            return TimeoutValidationResult.Valid(new TimeoutPolicy(
                defaultSeconds,
                parameterDefault,
                minimum,
                maximum,
                grace.Value,
                kind));
        }

        private static bool IsCanonicalName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            foreach (var c in name)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-'))
                    return false;
            }
            return true;
        }

        public static int ScalarLength(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            var count = 0;
            for (var i = 0; i < value.Length; i++, count++)
            {
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    i++;
            }
            return count;
        }

        internal static string TakeScalars(string value, int maxScalars)
        {
            if (string.IsNullOrEmpty(value) || maxScalars <= 0) return string.Empty;
            var end = 0;
            var scalars = 0;
            for (var i = 0; i < value.Length && scalars < maxScalars; i++, scalars++)
            {
                end = i + 1;
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) end = ++i + 1;
            }
            return value.Substring(0, end);
        }

        /// <summary>
        /// Truncates to at most <paramref name="maxScalars"/> Unicode scalar values,
        /// appending "…" only when truncation happened. Never splits a surrogate pair.
        /// </summary>
        public static string TruncateScalars(string value, int maxScalars)
        {
            if (string.IsNullOrEmpty(value) || maxScalars <= 0) return string.Empty;
            if (ScalarLength(value) <= maxScalars) return value;
            return TakeScalars(value, maxScalars - 1).TrimEnd() + "…";
        }

        private sealed class TimeoutValidationResult
        {
            private TimeoutValidationResult(bool valid, TimeoutPolicy timeout, string error)
            {
                IsValid = valid;
                Timeout = timeout;
                Error = error;
            }

            public bool IsValid { get; }
            public TimeoutPolicy Timeout { get; }
            public string Error { get; }

            public static TimeoutValidationResult Valid(TimeoutPolicy timeout)
                => new TimeoutValidationResult(true, timeout, null);

            public static TimeoutValidationResult Invalid(string error)
                => new TimeoutValidationResult(false, null, error);
        }
    }

    /// <summary>Thread-safe, connection-scoped plugin view of the latest catalog.</summary>
    public static class ToolCatalogStore
    {
        private static readonly object Gate = new object();
        private static ToolCatalog _current;
        private static ToolCatalogStatus _status = ToolCatalogStatus.NotConnected;
        private static string _error;
        private static long _owner;
        private static long _nextConnectionId;

        public static event EventHandler Changed;

        public static ToolCatalogStatus Status
        {
            get { lock (Gate) return _status; }
        }

        public static ToolCatalog Current
        {
            get { lock (Gate) return _current; }
        }

        public static string Error
        {
            get { lock (Gate) return _error; }
        }

        /// <summary>Registers a new connection and returns its ownership id.</summary>
        public static long BeginConnection()
        {
            long id;
            lock (Gate)
            {
                id = ++_nextConnectionId;
                _owner = id;
                _current = null;
                _error = null;
                _status = ToolCatalogStatus.ConnectedNoCatalog;
            }
            NotifyChanged();
            return id;
        }

        /// <summary>Maps to the current connection owner (non-transport callers).</summary>
        public static ToolCatalogValidationResult AcceptJson(string json)
        {
            long owner;
            lock (Gate) { owner = _owner; }
            return AcceptJson(json, owner);
        }

        /// <summary>Accepts a catalog only from the owning connection; a stale id is a no-op.</summary>
        public static ToolCatalogValidationResult AcceptJson(string json, long connectionId)
            => AcceptJson(json, connectionId, null);

        internal static ToolCatalogValidationResult AcceptJson(string json, long connectionId, Action betweenParseAndCommit)
        {
            lock (Gate)
            {
                if (connectionId != _owner)
                    return ToolCatalogValidationResult.Invalid("Catalog update from a stale connection.");
            }
            var result = ToolCatalogCodec.ParseAndValidate(json);
            betweenParseAndCommit?.Invoke();
            lock (Gate)
            {
                if (connectionId != _owner)
                    return ToolCatalogValidationResult.Invalid("Catalog update from a stale connection.");
                if (result.IsValid)
                {
                    _current = result.Catalog;
                    _error = null;
                    _status = ToolCatalogStatus.Current;
                }
                else
                {
                    _current = null;
                    _error = result.Error;
                    _status = ToolCatalogStatus.Invalid;
                }
            }
            NotifyChanged();
            return result;
        }

        /// <summary>Compare-and-clear: only the owning connection may reset state.</summary>
        public static bool Clear(long connectionId)
        {
            lock (Gate)
            {
                if (connectionId != _owner)
                    return false;
                _current = null;
                _error = null;
                _status = ToolCatalogStatus.NotConnected;
                _owner = 0;
            }
            NotifyChanged();
            return true;
        }

        /// <summary>Unconditional reset.</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                _current = null;
                _error = null;
                _status = ToolCatalogStatus.NotConnected;
                _owner = 0;
            }
            NotifyChanged();
        }

        private static void NotifyChanged()
        {
            var handler = Changed;
            if (handler == null) return;
            foreach (EventHandler subscriber in handler.GetInvocationList())
            {
                try { subscriber(null, EventArgs.Empty); } catch { }
            }
        }
    }
}
