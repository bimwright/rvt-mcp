using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace RvtMcp.Failures
{
    // One aligned 64-bit activation stamp per x64 Revit process. The gateway can read it even
    // while Revit is modal. Only the add-in writes, on the Revit UI thread. No model data is shared.
    internal sealed class DialogDocumentContext : IDisposable
    {
        private readonly MemoryMappedFile _map;
        private readonly MemoryMappedViewAccessor _view;
        private string _documentId;
        internal long Stamp { get; private set; }

        internal DialogDocumentContext(string target)
        {
            _map = MemoryMappedFile.CreateNew(Name(target), sizeof(long));
            try
            {
                _view = _map.CreateViewAccessor(0, sizeof(long));
                Stamp = DateTime.UtcNow.Ticks;
                _view.Write(0, Stamp);
            }
            catch { _view?.Dispose(); _map.Dispose(); throw; }
        }

        internal void Observe(string activeDocumentId)
        {
            if (activeDocumentId == _documentId) return;
            Invalidate();
            _documentId = activeDocumentId;
        }

        internal void Invalidate() => _view.Write(0, ++Stamp);

        internal static void RequireCurrent(DialogDocumentContext context, long? declaredStamp)
        {
            if (context == null || context.Stamp != declaredStamp)
                throw new InvalidOperationException("DOCUMENT_CHANGED: Dialog declarations expired before dispatch. Review the active document and declare answers again.");
        }

        internal static long? Read(string target)
        {
#if NET5_0_OR_GREATER
            if (!OperatingSystem.IsWindows()) return null;
#endif
            try
            {
                using (var map = MemoryMappedFile.OpenExisting(Name(target), MemoryMappedFileRights.Read))
                using (var view = map.CreateViewAccessor(0, sizeof(long), MemoryMappedFileAccess.Read))
                {
                    var stamp = view.ReadInt64(0);
                    return stamp == 0 ? (long?)null : stamp;
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static string Name(string target) => @"Local\RvtMcp.DialogContext." + target;

        public void Dispose() { _view.Dispose(); _map.Dispose(); }
    }
}
