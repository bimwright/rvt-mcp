using System;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Handlers keep the short "No document is open." failure. The wire response
    /// expands it so an agent knows Revit itself is running and which tool to call next.
    /// </summary>
    public static class NoDocumentGuidance
    {
        public const string HandlerError = "No document is open.";

        public const string AgentError =
            "Revit is running but no document is open. Call revit_list_recent_models, ask the user which file to open, then call revit_open_model with that path. Do not guess a path and do not open a file before the user chooses.";

        public static string ForAgent(string handlerError)
        {
            if (string.Equals(handlerError, HandlerError, StringComparison.Ordinal))
                return AgentError;
            return handlerError;
        }
    }
}
