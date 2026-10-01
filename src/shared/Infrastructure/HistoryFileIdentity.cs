using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RvtMcp.Plugin
{
    public static class HistoryFileIdentity
    {
        // No file content is read and nothing is written. File IDs distinguish copies;
        // Revit GUIDs alone do not. Unsupported/unavailable evidence stays unknown.
        public static string TryGet(string path, Action<string> log = null)
        {
            if (string.IsNullOrWhiteSpace(path) || Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    FileIdInfo id; FileInfo info;
                    if (!GetFileInformationByHandleEx(stream.SafeFileHandle, 18, out id, (uint)Marshal.SizeOf(typeof(FileIdInfo)))
                        || !GetFileInformationByHandle(stream.SafeFileHandle, out info)
                        || (id.Low == 0 && id.High == 0) || (info.CreationLow == 0 && info.CreationHigh == 0)) return null;
                    var final = new StringBuilder(32768);
                    uint length = GetFinalPathNameByHandleW(stream.SafeFileHandle, final, (uint)final.Capacity, 0);
                    if (length == 0 || length >= final.Capacity) return null;
                    var name = final.ToString(); var server = Environment.MachineName;
                    if (name.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    {
                        var end = name.IndexOf('\\', 8);
                        if (end < 0) return null;
                        server = name.Substring(8, end - 8);
                        if (server.Equals("localhost", StringComparison.OrdinalIgnoreCase) || server == "." || server == "127.0.0.1")
                            server = Environment.MachineName;
                    }
                    var fingerprint = server.ToUpperInvariant() + ":" + id.Volume.ToString("x16") + ":" + id.High.ToString("x16") + id.Low.ToString("x16")
                        + ":" + info.CreationHigh.ToString("x8") + info.CreationLow.ToString("x8");
                    return ChangeHistoryIdentity.Create(fingerprint, "physical-file", null).Value<string>("key");
                }
            }
            catch (Exception ex)
            {
                HistoryDiagnostics.Report("history_file_identity", ex, log: log);
                return null;
            }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdInfo { public ulong Volume; public ulong Low; public ulong High; }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileInfo
        {
            public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out FileIdInfo info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    }
}
