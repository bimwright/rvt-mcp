using System;
using Autodesk.Revit.DB;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Revit's IFC and DWF/DWFx exporters require a modifiable document, i.e. an open
    /// transaction. Runs the export inside one and rolls it back, so the file is written
    /// but the model is left unchanged. When the caller already holds a transaction the
    /// export runs inside it.
    /// </summary>
    internal static class ExportTransaction
    {
        public static void Run(Document doc, string name, Action export)
        {
            if (doc.IsModifiable)
            {
                export();
                return;
            }

            using (var tx = new Transaction(doc, name))
            {
                tx.Start();
                try
                {
                    export();
                }
                finally
                {
                    if (tx.GetStatus() == TransactionStatus.Started)
                        tx.RollBack();
                }
            }
        }
    }
}
