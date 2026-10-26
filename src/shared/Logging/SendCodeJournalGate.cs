using System;

namespace RvtMcp.Plugin
{
    internal static class SendCodeJournalGate
    {
        public static bool OnSendCodeLogged(
            string commandName,
            string paramsJson,
            string codeSnippet,
            bool success,
            long durationMs,
            string resultError,
            string resultJson,
            RvtMcpConfig runtimeConfig = null, Newtonsoft.Json.Linq.JObject receipt = null)
        {
            if (commandName != "send_code_to_revit")
                return true;

            try
            {
                var cfg = runtimeConfig ?? RvtMcpConfig.LoadReadOnly();
                if (!cfg.EnableCallLogOrDefault) return true;
                // Always enter the journal: it purges expired bodies even when persistence is off.
                var appended = SendCodeJournal.TryAppendWithReceipt(
                    cfg,
                    McpLogger.CurrentSessionId,
                    codeSnippet,
                    success,
                    durationMs,
                    resultError,
                    resultJson, receipt: receipt);
                return appended || !cfg.IsPersistSendCodeBodiesActive();
            }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine("SendCodeJournal failed: " + ex.Message); } catch { }
                return false;
            }
        }
    }
}
