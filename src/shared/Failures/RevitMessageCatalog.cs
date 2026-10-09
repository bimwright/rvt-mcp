using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RvtMcp.Failures;

namespace RvtMcp.Plugin.Failures
{
    // Reads the TaskDialog and failure catalogue of the Revit running this add-in, once per session on the UI thread,
    // through public APIs only. The local cache lets the server answer revit_lookup_message while Revit is modal.
    internal static class RevitMessageCatalog
    {
        private const string TaskDialogResource = "/UIFrameworkRes;component/taskfiles/gensrc/taskfiles.xaml";
        private static bool _attempted;
        internal static RevitMessageCatalogData Current { get; private set; }

        internal static TaskDialogEntry FindDialog(string code) { return RevitMessageLookup.FindDialog(Current, code); }
        internal static FailureEntry FindFailure(string code) { return RevitMessageLookup.FindFailure(Current, code); }

        internal static void EnsureLoaded(UIApplication app)
        {
            if (_attempted || app == null) return;
            _attempted = true;
            try
            {
                var application = app.Application;
                int.TryParse(application.VersionNumber, out var year);
                var build = application.VersionBuild;
                var language = application.Language.ToString();
                var path = RevitMessageLookup.CachePath(year);
                var cached = RevitMessageLookup.Read(path);
                if (cached != null && cached.Build == build && cached.Language == language
                    && cached.TaskDialogError == null && cached.FailureError == null)
                {
                    Current = cached;
                    return;
                }
                var data = new RevitMessageCatalogData { RevitYear = year, Build = build, Language = language };
                try { data.TaskDialogs = ReadTaskDialogs(); }
                catch (Exception ex) { data.TaskDialogError = ex.GetType().Name; App.DebugLog("TaskDialog catalogue unavailable: " + ex.GetType().Name + ": " + ex.Message); }
                try { data.Failures = ReadFailures(); }
                catch (Exception ex) { data.FailureError = ex.GetType().Name; App.DebugLog("Failure catalogue unavailable: " + ex.GetType().Name + ": " + ex.Message); }
                Current = data;
                RevitMessageLookup.Write(path, data);
            }
            catch (Exception ex)
            {
                // A missing catalogue only removes detail from receipts; it never fails a command.
                App.DebugLog("Revit message catalogue unavailable: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static List<TaskDialogEntry> ReadTaskDialogs()
        {
            var dictionary = (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri(TaskDialogResource, UriKind.Relative));
            var result = new List<TaskDialogEntry>();
            foreach (var key in dictionary.Keys)
            {
                object dialog;
                try { dialog = dictionary[key]; } catch { continue; }
                if (dialog == null) continue;
                var entry = new TaskDialogEntry
                {
                    Id = key.ToString(), Title = Text(dialog, "WindowTitle"), MainInstruction = Text(dialog, "MainInstruction"),
                    Content = Text(dialog, "ContentText"), Expanded = Text(dialog, "ExpandedText"), Footer = Text(dialog, "FooterText"),
                    Verification = Text(dialog, "VerificationText"), CommonButtons = Text(dialog, "CommonButtons"),
                    DefaultButton = Text(dialog, "DefaultButton")
                };
                if (Value(dialog, "Buttons") is IEnumerable buttons)
                    foreach (var button in buttons)
                        if (button != null) entry.Buttons.Add(new CatalogButton { Id = Text(button, "ButtonId"), Text = Text(button, "ButtonText") });
                result.Add(entry);
            }
            return result;
        }

        private static List<FailureEntry> ReadFailures()
        {
            var names = new Dictionary<Guid, string>();
            foreach (var group in typeof(BuiltInFailures).GetNestedTypes(BindingFlags.Public))
                foreach (var property in group.GetProperties(BindingFlags.Public | BindingFlags.Static))
                {
                    if (property.PropertyType != typeof(FailureDefinitionId)) continue;
                    try
                    {
                        var id = (FailureDefinitionId)property.GetValue(null, null);
                        if (id != null && !names.ContainsKey(id.Guid)) names[id.Guid] = group.Name + "." + property.Name;
                    }
                    catch { }
                }
            var result = new List<FailureEntry>();
            foreach (var definition in Autodesk.Revit.ApplicationServices.Application.GetFailureDefinitionRegistry().ListAllFailureDefinitions())
            {
                var guid = definition.GetId().Guid;
                var entry = new FailureEntry
                {
                    Guid = guid.ToString(), Name = names.TryGetValue(guid, out var name) ? name : null,
                    Severity = definition.GetSeverity().ToString(), Description = definition.GetDescriptionText()
                };
                if (definition.HasResolutions())
                {
                    try { entry.DefaultResolution = definition.GetDefaultResolutionType().ToString(); } catch { }
                    foreach (var type in definition.GetApplicableResolutionTypes())
                    {
                        string caption = null;
                        try { caption = definition.GetResolutionCaption(type); } catch { }
                        entry.Resolutions.Add(new FailureResolutionEntry { Type = type.ToString(), Caption = caption });
                    }
                }
                result.Add(entry);
            }
            return result;
        }

        private static object Value(object target, string property)
        {
            try { return target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target, null); }
            catch { return null; }
        }

        private static string Text(object target, string property)
        {
            var value = Value(target, property);
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }
}
