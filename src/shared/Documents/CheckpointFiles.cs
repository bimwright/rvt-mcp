using System.IO;

namespace RvtMcp.Plugin.Documents
{
    public static class CheckpointFiles
    {
        public static void CopyNew(string source, string target)
        {
            // Only remove a target after File.Copy has successfully created it.
            File.Copy(source, target, false);
            try
            {
                var attributes = File.GetAttributes(target);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
            }
            catch
            {
                try { File.Delete(target); } catch { }
                throw;
            }
        }
    }
}
