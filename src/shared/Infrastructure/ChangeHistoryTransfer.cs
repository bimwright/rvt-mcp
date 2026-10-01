using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    // Private authenticated gateway metadata, never a tool argument or public response payload.
    public sealed class ChangeHistoryRequest
    {
        public string Id { get; set; }
        public string Session { get; set; }
        public bool IsValid => ChangeHistoryTransfer.IsId(Id) && ChangeHistoryTransfer.IsId(Session);
    }

    public static class ChangeHistoryIdentity
    {
        public static JObject Create(string identity, string kind, string title, string path = null)
        {
            if (string.IsNullOrWhiteSpace(identity)) return null;
            var canonical = kind == "file" || kind == "central"
                ? identity.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant()
                : identity.Trim().ToUpperInvariant();
            var keyText = kind + ":" + canonical;
            string key;
            using (var hash = SHA256.Create())
                key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(keyText))).Replace("-", "").ToLowerInvariant();
            return new JObject { ["key"] = key, ["identity"] = keyText, ["kind"] = kind,
                ["path"] = path, ["title"] = BakeRedactor.RedactForBake(title ?? "") };
        }
    }

    public sealed class ChangeHistoryTransfer
    {
        public const long MaxBytes = 128L * 1024 * 1024;
        public string Root { get; }
        public ChangeHistoryTransfer(string dataRoot = null)
        {
            Root = Path.Combine(dataRoot ?? Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "Bimwright", "rvt-mcp"), "history-pending");
        }
        public static bool IsId(string value) => value != null && value.Length == 32 && Guid.TryParseExact(value, "N", out _);
        private string FilePath(string id)
        {
            if (!IsId(id)) throw new ArgumentException("Invalid history call identifier.");
            return Path.Combine(Root, id + ".json");
        }
        public void Write(string id, JObject payload)
        {
            var path = FilePath(id);
            var bytes = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
            if (bytes.LongLength > MaxBytes) throw new InvalidOperationException("History capture exceeded its storage limit.");
            Directory.CreateDirectory(Root);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public JObject Read(string id)
        {
            var path = FilePath(id);
            var info = new FileInfo(path);
            if (info.Length > MaxBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Invalid history capture file.");
            var value = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (value.Value<string>("callId") != id) throw new InvalidOperationException("History capture identifier mismatch.");
            return value;
        }
        public void Remove(string id) { File.Delete(FilePath(id)); }
        public string[] PendingIds()
        {
            if (!Directory.Exists(Root)) return new string[0];
            return System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(
                System.Linq.Enumerable.Select(Directory.EnumerateFiles(Root, "*.json"), Path.GetFileNameWithoutExtension), IsId));
        }
    }
}
