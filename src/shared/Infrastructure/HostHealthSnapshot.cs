using System;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    /// <summary>Listener-safe identity and explicitly dated UI observations; never calls the Revit API.</summary>
    internal static class HostHealthSnapshot
    {
        private static readonly object Gate = new object();
        private static JObject _identity;
        private static JObject _document;
        private static string _observedUtc;

        internal static void Initialize(Assembly assembly, int year)
        {
            var identity = RuntimeIdentity.Capture(assembly, "plugin", year);
            lock (Gate) { _identity = identity; _document = null; _observedUtc = null; }
        }

        // Only a successful UI-thread read updates this snapshot. Never retain native wrappers or paths.
        internal static void ObserveDocument(bool hasDocument, string title, bool? isFamily)
        {
            lock (Gate)
            {
                _document = new JObject
                {
                    ["has_active_document"] = hasDocument,
                    ["document_title"] = title,
                    ["is_family_document"] = isFamily
                };
                _observedUtc = DateTimeOffset.UtcNow.ToString("o");
            }
        }

        internal static void ForgetDocument()
        {
            lock (Gate) { _document = null; _observedUtc = null; }
        }

        internal static JObject Capture(int connectionInFlight, bool waitingForUser)
        {
            lock (Gate)
                return new JObject
                {
                    ["ok"] = _identity != null, ["healthy"] = _identity != null,
                    ["setup_plugin"] = _identity?.DeepClone(),
                    ["revit_year"] = _identity?["host_year"]?.DeepClone(),
                    ["process_id"] = _identity?["process_id"]?.DeepClone(),
                    ["waiting_for_user"] = waitingForUser,
                    ["connection_in_flight"] = connectionInFlight,
                    ["host_state"] = waitingForUser ? "waiting_for_user"
                        : connectionInFlight > 0 ? "commands_in_flight" : "not_observed",
                    ["ready_for_commands"] = waitingForUser ? new JValue(false) : JValue.CreateNull(),
                    ["state_probe"] = _document == null ? "not_observed" : "last_ui_observation",
                    ["document_observed_utc"] = _observedUtc,
                    ["has_active_document"] = _document?["has_active_document"]?.DeepClone(),
                    ["document_title"] = _document?["document_title"]?.DeepClone(),
                    ["is_family_document"] = _document?["is_family_document"]?.DeepClone()
                };
        }
    }
}
