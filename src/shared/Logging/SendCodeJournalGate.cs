using System;

namespace RvtMcp.Plugin
{
    internal static class SendCodeJournalGate
    {
        public static void OnSendCodeLogged(
            string commandName,
            string paramsJson,
            string codeSnippet,
            bool success,
            long durationMs,
            string resultError,
            string resultJson,
            RvtMcpConfig runtimeConfig = null)
        {
            if (commandName != "send_code_to_revit")
                return;

            try
            {
                var cfg = runtimeConfig ?? RvtMcpConfig.LoadReadOnly();
                SendCodeJournal.TryAppend(
                    cfg,
                    McpLogger.CurrentSessionId,
                    codeSnippet,
                    success,
                    durationMs,
                    resultError,
                    resultJson);
            }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine("SendCodeJournal failed: " + ex.Message); } catch { }
            }
        }
    }
}
