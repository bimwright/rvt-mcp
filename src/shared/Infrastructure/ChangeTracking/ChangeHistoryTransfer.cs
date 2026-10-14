using System;
using System.IO;
using System.Linq;
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
        // The production publication seam: ordinary reads do no filesystem I/O.
        public JObject Publish(string id, JObject payload)
        {
            var documents = payload?["documents"] as JArray;
            var marker = PublicationMarker(id, payload);
            if (marker?["id"] == null) return marker;
            var known = new JArray(documents.OfType<JObject>().Where(d => d["model"] is JObject).Select(d => d.DeepClone()));
            int skipped = documents.Count - known.Count;
            var capture = (JObject)payload.DeepClone();
            capture["documents"] = known;
            capture["skippedDocuments"] = skipped;
            Write(id, capture);
            return marker;
        }
        public static JObject PublicationMarker(string id, JObject payload)
        {
            var documents = payload?["documents"] as JArray;
            if (documents == null || documents.Count == 0) return null;
            return documents.OfType<JObject>().Any(d => d["model"] is JObject)
                ? new JObject { ["id"] = id }
                : new JObject { ["status"] = "skipped_identity", ["skippedDocuments"] = documents.Count };
        }
        public JObject Read(string id)
        {
            var path = FilePath(id);
            var info = new FileInfo(path);
            if (info.Length > MaxBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Invalid history capture file.");
            var value = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (value.Value<string>("callId") != id) throw new InvalidDataException("History capture identifier mismatch.");
            return value;
        }
        public void Remove(string id) { File.Delete(FilePath(id)); }
        public void Quarantine(string id)
        {
            var source = FilePath(id);
            var destination = Path.Combine(Path.GetDirectoryName(Root), "history-quarantine");
            Directory.CreateDirectory(destination);
            // Immutable evidence outside the retry queue. A racing consumer may already have moved it.
            File.Move(source, Path.Combine(destination, id + "-" + Guid.NewGuid().ToString("N") + ".json"));
        }
        public System.Collections.Generic.IEnumerable<FileInfo> PendingFiles()
        {
            if (!Directory.Exists(Root)) yield break;
            foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
                if (IsId(Path.GetFileNameWithoutExtension(file))) yield return new FileInfo(file);
        }
        public string[] PendingIds()
        {
            return PendingFiles().Select(f => Path.GetFileNameWithoutExtension(f.Name)).ToArray();
        }
    }
}
