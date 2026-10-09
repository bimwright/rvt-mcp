using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Failures
{
    // The message catalogue of the Revit installed on this machine, read by the add-in at run time.
    // Its text belongs to that Revit: the file is generated locally and is never packaged or shipped.
    public sealed class RevitMessageCatalogData
    {
        public const int CurrentSchema = 1;
        public int SchemaVersion { get; set; } = CurrentSchema;
        public int RevitYear { get; set; }
        public string Build { get; set; }
        public string Language { get; set; }
        public DateTimeOffset GeneratedUtc { get; set; } = DateTimeOffset.UtcNow;
        public string TaskDialogError { get; set; }
        public string FailureError { get; set; }
        public List<TaskDialogEntry> TaskDialogs { get; set; } = new List<TaskDialogEntry>();
        public List<FailureEntry> Failures { get; set; } = new List<FailureEntry>();
    }

    public sealed class TaskDialogEntry
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string MainInstruction { get; set; }
        public string Content { get; set; }
        public string Expanded { get; set; }
        public string Footer { get; set; }
        public string Verification { get; set; }
        public string CommonButtons { get; set; }
        public string DefaultButton { get; set; }
        public List<CatalogButton> Buttons { get; set; } = new List<CatalogButton>();
    }

    public sealed class CatalogButton
    {
        public string Id { get; set; }
        public string Text { get; set; }
    }

    public sealed class FailureEntry
    {
        public string Guid { get; set; }
        public string Name { get; set; }
        public string Severity { get; set; }
        public string Description { get; set; }
        public string DefaultResolution { get; set; }
        public List<FailureResolutionEntry> Resolutions { get; set; } = new List<FailureResolutionEntry>();
    }

    public sealed class FailureResolutionEntry
    {
        public string Type { get; set; }
        public string Caption { get; set; }
    }

    public static class RevitMessageLookup
    {
        public const int MaxDialogText = 800, MaxTitle = 160, MaxButtons = 8, MaxButtonText = 120;
        public static string RootOverride;

        public static string CachePath(int revitYear)
        {
            var root = RootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bimwright", "rvt-mcp");
            return Path.Combine(root, "catalog", "revit-" + revitYear + ".json");
        }

        public static RevitMessageCatalogData Read(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var data = FailureJson.Deserialize<RevitMessageCatalogData>(File.ReadAllText(path, Encoding.UTF8));
                return data?.SchemaVersion == RevitMessageCatalogData.CurrentSchema ? data : null;
            }
            catch { return null; }
        }

        public static void Write(string path, RevitMessageCatalogData data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, FailureJson.Serialize(data), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        public static TaskDialogEntry FindDialog(RevitMessageCatalogData data, string code)
        {
            if (data == null || string.IsNullOrWhiteSpace(code)) return null;
            code = code.Trim();
            return data.TaskDialogs.FirstOrDefault(d => string.Equals(d.Id, code, StringComparison.OrdinalIgnoreCase))
                ?? data.TaskDialogs.FirstOrDefault(d => string.Equals(d.Id, "TaskDialog_" + code, StringComparison.OrdinalIgnoreCase));
        }

        public static FailureEntry FindFailure(RevitMessageCatalogData data, string code)
        {
            if (data == null || string.IsNullOrWhiteSpace(code)) return null;
            code = code.Trim();
            if (System.Guid.TryParse(code, out var guid))
            {
                var text = guid.ToString(); // Entries are written in this same format.
                return data.Failures.FirstOrDefault(f => string.Equals(f.Guid, text, StringComparison.OrdinalIgnoreCase));
            }
            return data.Failures.FirstOrDefault(f => string.Equals(f.Name, code, StringComparison.OrdinalIgnoreCase));
        }

        // What the agent receives about a dialog Revit is showing: the live text plus what the catalogue knows about its buttons.
        public static JObject DialogInfo(string code, string kind, string text, TaskDialogEntry entry, int maxText = MaxDialogText)
        {
            var info = new JObject
            {
                ["code"] = FailureCapture.SafeText(code ?? "", 128),
                ["kind"] = kind,
                ["text"] = text == null ? null : FailureCapture.SafeText(text, maxText),
                ["inCatalog"] = entry != null
            };
            if (entry != null)
            {
                info["title"] = Bounded(entry.Title, MaxTitle);
                if (entry.Buttons.Count > 0) info["buttons"] = Buttons(entry);
                if (!string.IsNullOrEmpty(entry.CommonButtons) && entry.CommonButtons != "None") info["commonButtons"] = entry.CommonButtons;
                if (!string.IsNullOrEmpty(entry.DefaultButton) && entry.DefaultButton != "0") info["defaultButton"] = entry.DefaultButton;
            }
            foreach (var property in info.Properties().Where(p => p.Value is JValue value && value.Value == null).ToList()) property.Remove();
            return info;
        }

        public static JArray Buttons(TaskDialogEntry entry)
        {
            return new JArray(entry.Buttons.Take(MaxButtons).Select(b => new JObject
            { ["id"] = b.Id, ["text"] = Bounded(b.Text, MaxButtonText) }));
        }

        // Answer for revit_lookup_message. Catalogue text is a template: the live dialog fills in its placeholders.
        public static JObject Lookup(RevitMessageCatalogData data, string code, int revitYear)
        {
            if (data == null)
                return new JObject
                {
                    ["available"] = false, ["revitYear"] = revitYear, ["code"] = code,
                    ["note"] = "No local message catalogue for this Revit year yet. The add-in creates it from the running Revit shortly after start-up."
                };
            var result = new JObject
            {
                ["available"] = true, ["revitYear"] = data.RevitYear, ["build"] = data.Build, ["language"] = data.Language, ["code"] = code
            };
            var dialog = FindDialog(data, code);
            if (dialog != null)
            {
                result["found"] = true;
                result["kind"] = "TaskDialog";
                result["entry"] = JObject.Parse(FailureJson.Serialize(dialog));
                result["note"] = "Template text from this Revit. The live dialog fills in placeholders such as file or element names.";
                return result;
            }
            var failure = FindFailure(data, code);
            if (failure != null)
            {
                result["found"] = true;
                result["kind"] = "Failure";
                result["entry"] = JObject.Parse(FailureJson.Serialize(failure));
                return result;
            }
            result["found"] = false;
            result["note"] = (data.TaskDialogError != null || data.FailureError != null ? "Part of the catalogue could not be read in this Revit. " : "")
                + "No catalogue entry for this code. Add-in dialogs, Windows dialogs and dialogs without a code are not in the catalogue.";
            return result;
        }

        private static string Bounded(string value, int max)
        {
            return value == null ? null : FailureCapture.SafeText(value, max);
        }
    }
}
