using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RvtMcp.Plugin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server.Bake
{
    public sealed class UsageEventLogger
    {
        private static readonly TimeSpan DefaultAnalysisThrottle = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan DefaultErrorWarningThrottle = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MaxReplayWindow = TimeSpan.FromDays(21);
        private const int DefaultReplayLineLimit = 5000;

        private static readonly HashSet<string> SendCodeTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "send_code_to_revit"
        };

        private static readonly HashSet<string> NonPresetTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "list_baked_tools",
            "run_baked_tool",
            "switch_target",
            "show_message",
            "batch_execute"
        };

        private readonly BakePaths _paths;
        private readonly RvtMcpConfig _config;
        private readonly ClusterEngine _clusterEngine;
        private readonly TimeSpan _analysisThrottle;
        private readonly TimeSpan _errorWarningThrottle;
        private readonly int _replayLineLimit;
        private readonly object _lock = new object();
        private IReadOnlyList<ClusterCandidate> _lastCandidates = Array.Empty<ClusterCandidate>();
        private DateTimeOffset _lastAnalysisUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastErrorWarningUtc = DateTimeOffset.MinValue;
        private string _lastError;
        private UsageEvent _previousPreset;

        public UsageEventLogger(
            BakePaths paths,
            RvtMcpConfig config,
            ClusterEngine clusterEngine = null,
            TimeSpan? analysisThrottle = null,
            int? replayLineLimit = null,
            TimeSpan? errorWarningThrottle = null)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _config = config ?? new RvtMcpConfig();
            _clusterEngine = clusterEngine ?? new ClusterEngine();
            _analysisThrottle = analysisThrottle ?? DefaultAnalysisThrottle;
            _replayLineLimit = Math.Max(1, replayLineLimit ?? DefaultReplayLineLimit);
            _errorWarningThrottle = errorWarningThrottle ?? DefaultErrorWarningThrottle;
        }

        public IReadOnlyList<ClusterCandidate> LastCandidates
        {
            get
            {
                lock (_lock)
                    return _lastCandidates.ToArray();
            }
        }

        public string LastError
        {
            get
            {
                lock (_lock)
                    return _lastError;
            }
        }

        public UsageEvent RecordToolCall(string tool, string paramsJson, bool success)
        {
            if (!_config.EnableAdaptiveBakeOrDefault)
                return null;

            try
            {
                var usageEvent = CreateToolEvent(tool, paramsJson, success);
                if (usageEvent == null)
                    return null;

                lock (_lock)
                {
                    Append(usageEvent);
                    RecordMacroIfApplicable(usageEvent);
                    RefreshCandidatesIfDue(DateTimeOffset.UtcNow);
                }

                return usageEvent;
            }
            catch (Exception ex)
            {
                RecordCaptureFailure(ex);
                return null;
            }
        }

        public void Record(UsageEvent usageEvent)
        {
            if (!_config.EnableAdaptiveBakeOrDefault || usageEvent == null)
                return;

            try
            {
                lock (_lock)
                {
                    Append(usageEvent);
                    RefreshCandidatesIfDue(DateTimeOffset.UtcNow);
                }
            }
            catch (Exception ex)
            {
                RecordCaptureFailure(ex);
            }
        }

        public IReadOnlyList<ClusterCandidate> RefreshCandidates(DateTimeOffset? now = null)
        {
            if (!_config.EnableAdaptiveBakeOrDefault)
                return Array.Empty<ClusterCandidate>();

            lock (_lock)
                return RefreshCandidatesLocked(now ?? DateTimeOffset.UtcNow);
        }

        private UsageEvent CreateToolEvent(string tool, string paramsJson, bool success)
        {
            if (string.IsNullOrWhiteSpace(tool))
                return null;

            if (SendCodeTools.Contains(tool))
                return CreateSendCodeEvent(tool, paramsJson, success);

            if (IsPresetTool(tool))
                return CreatePresetEvent(tool, paramsJson, success);

            return null;
        }

        private UsageEvent CreateSendCodeEvent(string tool, string paramsJson, bool success)
        {
            var code = ExtractCodeBody(paramsJson);
            var payload = new JObject
            {
                ["code_length"] = code.Length,
                ["body_cache_enabled"] = _config.CacheSendCodeBodiesOrDefault
            };

            string normalizedKey;
            if (_config.CacheSendCodeBodiesOrDefault)
            {
                var material = BakeRedactor.RedactForBake(code, redactResultFields: true);
                payload["cluster_material"] = material;
                normalizedKey = "send_code:" + BakeRedactor.HashBody(material);
            }
            else
            {
                normalizedKey = "send_code:body-cache-disabled";
            }

            return NewEvent(
                source: "send_code",
                tool: tool,
                normalizedKey: normalizedKey,
                payload: payload,
                bodyHash: BakeRedactor.HashBody(code),
                success: success);
        }

        private UsageEvent CreatePresetEvent(string tool, string paramsJson, bool success)
        {
            var kinds = BuildParameterKinds(paramsJson);
            var shape = string.Join(",", kinds.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
            if (shape.Length == 0)
                shape = "no_args";

            var payload = new JObject
            {
                ["parameter_kinds"] = kinds
            };

            return NewEvent(
                source: "preset",
                tool: tool,
                normalizedKey: "preset:" + tool + ":" + shape,
                payload: payload,
                bodyHash: null,
                success: success);
        }

        private void RecordMacroIfApplicable(UsageEvent current)
        {
            if (!string.Equals(current.Source, "preset", StringComparison.OrdinalIgnoreCase))
            {
                _previousPreset = null;
                return;
            }

            if (_previousPreset != null)
            {
                var sequence = new JArray(_previousPreset.Tool, current.Tool);
                var key = "macro:" + _previousPreset.Tool + ">" + current.Tool;
                Append(NewEvent(
                    source: "macro",
                    tool: _previousPreset.Tool + ">" + current.Tool,
                    normalizedKey: key,
                    payload: new JObject { ["sequence"] = sequence },
                    bodyHash: null,
                    success: _previousPreset.Success && current.Success));
            }

            _previousPreset = current.Success ? current : null;
        }

        private void Append(UsageEvent usageEvent)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_paths.UsageJsonl) ?? ".");
            BoundedLogFile.Append(_paths.UsageJsonl, JsonConvert.SerializeObject(usageEvent, Formatting.None));
        }

        private void RefreshCandidatesIfDue(DateTimeOffset now)
        {
            if (now - _lastAnalysisUtc < _analysisThrottle)
                return;

            RefreshCandidatesLocked(now);
        }

        private IReadOnlyList<ClusterCandidate> RefreshCandidatesLocked(DateTimeOffset now)
        {
            var events = ReadUsageEvents(now);
            _lastCandidates = _clusterEngine.Analyze(events, now);
            _lastAnalysisUtc = now;
            return _lastCandidates.ToArray();
        }

        private IReadOnlyList<UsageEvent> ReadUsageEvents(DateTimeOffset now)
        {
            var cutoff = now.Subtract(MaxReplayWindow);
            var tail = BoundedLogFile.Files(Path.GetDirectoryName(_paths.UsageJsonl), "usage")
                .SelectMany(path => BoundedLogFile.ReadLinesSafe(path)).TakeLast(_replayLineLimit);
            var events = new List<UsageEvent>();
            foreach (var line in tail)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var usageEvent = JsonConvert.DeserializeObject<UsageEvent>(line);
                    if (usageEvent != null && usageEvent.TsUtc >= cutoff && usageEvent.TsUtc <= now)
                        events.Add(usageEvent);
                }
                catch (JsonException)
                {
                }
            }

            return events;
        }
        private void RecordCaptureFailure(Exception ex)
        {
            var now = DateTimeOffset.UtcNow;
            var message = $"Usage capture failed: {ex.GetType().Name}: {ex.Message}";
            lock (_lock)
            {
                _lastError = message;
                if (now - _lastErrorWarningUtc < _errorWarningThrottle)
                    return;

                _lastErrorWarningUtc = now;
            }

            try
            {
                Console.Error.WriteLine("[RvtMcp] Warning: " + message);
            }
            catch
            {
            }
        }

        private static UsageEvent NewEvent(string source, string tool, string normalizedKey, JObject payload, string bodyHash, bool success)
        {
            return new UsageEvent
            {
                Id = Guid.NewGuid().ToString("N"),
                TsUtc = DateTimeOffset.UtcNow,
                Source = source,
                Tool = tool,
                NormalizedKey = normalizedKey,
                PayloadJson = payload.ToString(Formatting.None),
                BodyHash = bodyHash,
                Success = success
            };
        }

        private static bool IsPresetTool(string tool)
        {
            return !NonPresetTools.Contains(tool);
        }

        private static JObject BuildParameterKinds(string paramsJson)
        {
            var result = new JObject();
            if (string.IsNullOrWhiteSpace(paramsJson))
                return result;

            JObject obj;
            try
            {
                obj = JObject.Parse(paramsJson);
            }
            catch (JsonException)
            {
                return result;
            }

            foreach (var property in obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (IsVolatileField(property.Name))
                    continue;

                result[property.Name] = ClassifyKind(property.Value);
            }

            return result;
        }

        private static bool IsVolatileField(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return true;

            var lower = name.ToLowerInvariant();
            return lower == "id" ||
                   lower == "ids" ||
                   name.EndsWith("Id", StringComparison.Ordinal) ||
                   name.EndsWith("Ids", StringComparison.Ordinal) ||
                   lower.EndsWith("_id", StringComparison.Ordinal) ||
                   lower.EndsWith("_ids", StringComparison.Ordinal) ||
                   IsKnownVolatileField(lower);
        }

        private static bool IsKnownVolatileField(string lowerName)
        {
            switch (lowerName)
            {
                case "timestamp":
                case "time":
                case "date":
                case "ts":
                case "ts_utc":
                case "createdat":
                case "updatedat":
                case "created_at":
                case "updated_at":
                case "path":
                case "filepath":
                case "file_path":
                case "exportpath":
                case "export_path":
                case "localpath":
                case "local_path":
                case "file":
                case "filename":
                case "file_name":
                    return true;
                default:
                    return false;
            }
        }

        private static string ClassifyKind(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                return "null";

            switch (token.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return "number";
                case JTokenType.Boolean:
                    return "bool";
                case JTokenType.String:
                    return "string";
                case JTokenType.Array:
                    return "array";
                case JTokenType.Object:
                    return "object";
                default:
                    return token.Type.ToString().ToLowerInvariant();
            }
        }

        private static string ExtractCodeBody(string paramsJson)
        {
            if (string.IsNullOrEmpty(paramsJson))
                return string.Empty;

            try
            {
                var obj = JObject.Parse(paramsJson);
                var code = obj["code"];
                if (code == null || code.Type == JTokenType.Null)
                    return string.Empty;
                if (code.Type == JTokenType.String)
                    return code.Value<string>() ?? string.Empty;
                return code.ToString(Formatting.None);
            }
            catch (JsonException)
            {
                return paramsJson;
            }
        }
    }
}
