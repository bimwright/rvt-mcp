using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace RvtMcp.Plugin.Documents
{
    public static class CheckpointSpace
    {
        public static bool HasEnough(long sourceBytes, long freeBytes)
        {
            if (sourceBytes < 0) throw new ArgumentOutOfRangeException(nameof(sourceBytes));
            if (freeBytes < 0) throw new ArgumentOutOfRangeException(nameof(freeBytes));
            return (decimal)freeBytes * 10 >= (decimal)sourceBytes * 11;
        }

        public static long AvailableBytes(string modelPath)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(modelPath));
            // UNC directories need a trailing separator for GetDiskFreeSpaceEx.
            if (!directory.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                directory += Path.DirectorySeparatorChar;
            if (!GetDiskFreeSpaceEx(directory, out var available, out _, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return available > long.MaxValue ? long.MaxValue : (long)available;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available,
            out ulong totalBytes, out ulong totalFreeBytes);
    }
}
