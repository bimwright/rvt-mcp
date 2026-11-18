using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Failures
{
    // Exact owner-approved dialog/button pairs. IDs identify the private decision record;
    // only codes and button identifiers are shipped, never Autodesk catalogue text.
    public static class DialogAnswerPolicy
    {
        public const int MaxAnswers = 64;
        private static readonly HashSet<string> Denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TaskDialog_Project_Not_Saved_Recently|1001", // D001
            "TaskDialog_Project_Not_Saved_Recently|1002", // D002
            "TaskDialog_Changes_Not_Saved|1001", // D003
            "TaskDialog_Changes_Not_Saved|1002", // D004
            "TaskDialog_Changes_Not_Saved|1003", // D005
            "TaskDialog_Save_File|Yes", // D006
            "TaskDialog_Changes_Not_Saved_To_Local_File|Yes", // D007
            "TaskDialog_Confirm_Save_Location|Yes", // D008
            "TaskDialog_Invalid_Central_Model_Extension|Yes", // D009
            "TaskDialog_Save_File_As_Central_Model|Yes", // D010
            "TaskDialog_Save_As_Central_Model|1002", // D011
            "TaskDialog_Save_As_Revit_Model|1001", // D012
            "TaskDialog_Loading_Transmitted_File|1001", // D013
            "TaskDialog_Unsynced_Changes|1001", // D014
            "TaskDialog_Serious_Error_Occurred_Save_Recovery_File|Yes", // D015
            "TaskDialog_Serious_Error_Occurred_Recoverable|Yes", // D016
            "TaskDialog_Fatal_Error_Occured|Yes", // D017
            "TaskDialog_Close_Project_Without_Saving|1001", // D018
            "TaskDialog_Close_Project_Without_Saving|1002", // D019
            "TaskDialog_Close_Single_User_Rollback_Incompatible_Model|1001", // D020
            "TaskDialog_Close_Single_User_Rollback_Incompatible_Model|1002", // D021
            "TaskDialog_Close_Rollback_Incompatible_Model|1001", // D022
            "TaskDialog_Close_Rollback_Incompatible_Model|1002", // D023
            "TaskDialog_Close_Relinquish_Incompatible_Model|1001", // D024
            "TaskDialog_Close_Relinquish_Incompatible_Model|1002", // D025
            "TaskDialog_Local_Changes_Not_Synchronized_With_Central|1002", // D026
            "TaskDialog_Local_Changes_Not_Synchronized_With_Central|1003", // D027
            "TaskDialog_Single_User_Local_Changes_Not_Saved_To_Cloud|1001", // D028
            "TaskDialog_Single_User_Local_Changes_Not_Saved_To_Cloud|1002", // D029
            "TaskDialog_Unsubmitted_Changes|1002", // D030
            "TaskDialog_Single_User_Unsubmitted_Changes|1002", // D031
            "TaskDialog_Open_Central_Model_Version_Archived|1002", // D032
            "TaskDialog_Open_Rollback_Incompatible_Model|1002", // D033
            "TaskDialog_Open_Single_User_Rollback_Incompatible_Model|1002", // D034
            "TaskDialog_Open_Relinquish_Incompatible_Model|1002", // D035
            "TaskDialog_Missing_Third_Party_Updater|1003", // D036
            "TaskDialog_Missing_Third_Party_Updater|1004", // D037
            "TaskDialog_Missing_Third_Party_Updaters|1003", // D038
            "TaskDialog_Missing_Third_Party_Updaters|1004", // D039
            "TaskDialog_Wait_For_Grant_In_Modal_Or_Canvas|1002", // D040
            "TaskDialog_Sketch_Edits_Discarded|Yes", // D041
            "TaskDialog_Sketch_Discarded|Yes", // D042
            "TaskDialog_Quit_Paste_Edit_Mode|Yes", // D043
            "TaskDialog_Quit_Toposurface|Yes", // D044
            "TaskDialog_Quit_In_Place_Family|Yes", // D045
            "TaskDialog_Model_Upgrade_Central|1001", // D046
            "TaskDialog_Changes_Not_Synchronized_With_Central|1001", // D047
            "TaskDialog_Changes_Not_Synchronized_With_Central|1002", // D048
            "TaskDialog_Editable_Elements|1001", // D049
            "TaskDialog_Save_Changes_To_Local_File|1001", // D050
            "TaskDialog_Repair_Local_Missing_Unique_Elements|1001", // D051
            "TaskDialog_Warn_To_Repair_Corrupt_Central_Model|Ok", // D052
            "TaskDialog_Corrupt_Model_Detected_On_Save|1001", // D053
            "TaskDialog_Dynamic_IRDB_Sync_To_Central|Ok", // D054
            "TaskDialog_Confirm_Create_New_Local|Ok", // D055
            "TaskDialog_Location_Position_Changed|1001", // D056
            "TaskDialog_Location_Position_Changed_For_Unloading_Locally|1001", // D057
            "TaskDialog_Host_Model_Has_Different_Geo_Location_Data|1001", // D058
            "TaskDialog_Host_Model_Has_No_Geo_Location_Data|1001", // D059
            "TaskDialog_Cannot_Modify_Linked_File|Yes", // D060
            "TaskDialog_Cannot_Modify_Imported_File|1001", // D061
            "TaskDialog_Cannot_Modify_Imported_File|1002", // D062
            "TaskDialog_Convert_To_Link|1001", // D063
            "TaskDialog_Overwrite_Link_File|Yes", // D064
            "TaskDialog_Modified_Shared_Parameter_File|Yes", // D065
            "TaskDialog_Overwrite_File|Yes", // D066
            "TaskDialog_Overwrite_File_List|Yes", // D067
            "TaskDialog_Replace_Existing_File|Yes", // D068
            "TaskDialog_Replace_File|Yes", // D069
            "TaskDialog_Replace_Multiple_Files|Yes", // D070
            "TaskDialog_Workset_File_Already_Exists|Yes", // D071
            "TaskDialog_Newer_File_Exists|Yes", // D072
            "TaskDialog_Backup_Failed|Yes", // D073
            "TaskDialog_File_Name_In_Use|1001", // D074
            "TaskDialog_File_Name_In_Use|1002", // D075
            "TaskDialog_File_Name_In_Use_LT|1001", // D076
            "TaskDialog_File_Name_In_Use_LT|1002", // D077
            "TaskDialog_Folder_Not_Empty|1001", // D078
            "TaskDialog_Project_Not_Saved_Recently|1003", // P001
            "TaskDialog_Changes_Not_Synchronized_With_Central|1003", // P002
            "TaskDialog_Location_Position_Changed|1002", // P003
            "TaskDialog_Location_Position_Changed|1003", // P004
            "TaskDialog_Location_Position_Changed_For_Unloading_Locally|1002", // P005
            "TaskDialog_Update_Saved_Position|Ok", // P006
            "TaskDialog_Confirm_Save_Location|No", // P007
            "TaskDialog_Changes_Not_Saved_To_Local_File|No", // P008
            "TaskDialog_Editable_Elements|1002", // P009
            "TaskDialog_Save_Changes_To_Local_File|1002", // P010
            "TaskDialog_Serious_Error_Occurred_Save_Recovery_File|No", // P011
            "TaskDialog_Serious_Error_Occurred_Recoverable|No", // P012
            "TaskDialog_Fatal_Error_Occured|No", // P013
            "TaskDialog_Model_Upgrade_Local|1001", // P014
            "TaskDialog_Detach_Model_From_Central|1001", // P015
            "TaskDialog_Detach_Model_From_Central|1002", // P016
            "TaskDialog_Open_Model_With_Unsupported_Feature|1001", // P017
            "TaskDialog_Schema_Conflict_Load|1002", // P018
            "TaskDialog_Shortcut_Changes_Not_Saved|1001", // P019
            "TaskDialog_Shortcut_Changes_Not_Saved|1002", // P020
            "TaskDialog_Replace_Existing_Keyboard_Shortcuts|1001", // P021
            "TaskDialog_Replace_Existing_Keyboard_Shortcuts|1002", // P022
            "TaskDialog_Save_Settings|Yes", // P023
            "TaskDialog_Save_Settings|No", // P024
            "TaskDialog_Asset_Not_Saved|Yes", // P025
            "TaskDialog_Asset_Not_Saved|No", // P026
            "TaskDialog_Overwrite_Property_Set_Library|Yes", // P027
            "TaskDialog_Changes_To_Panel_Schedules_Unsaved|1002", // P028
            "TaskDialog_Missing_Third_Party_Updater|1002", // P029
            "TaskDialog_Missing_Third_Party_Updaters|1002", // P030
            "TaskDialog_Save_File|No", // P031
            "TaskDialog_Modified_Shared_Parameter_File|No", // P032
            "TaskDialog_Cannot_Relinquish_Renamed_Workset|Yes", // P033
            "TaskDialog_Edit_Workset_At_Risk|Yes", // P034
            "TaskDialog_Model_Opened_By_Another_User|1001", // P035
            "TaskDialog_Energy_Model_Creation_Will_End_If_File_Closed|Yes", // P036
            "TaskDialog_Energy_Model_Geometry_Wont_Be_Saved|Ok", // P037
            "TaskDialog_Fabrication_Network_Inline_Fittings_Need_Attention|1002", // P038
            "TaskDialog_Security_Unsigned_File_Loading|1001", // T001
            "TaskDialog_Security_Unsigned_File_Loading|1002", // T002
            "TaskDialog_Security_Invalid_Signature|1001", // T003
            "TaskDialog_Security_Signed_File_Not_In_Trusted_Folder|1001", // T004
        };

        public static bool IsDenied(string code, string button) => Denied.Contains(code + "|" + button);

        public static JArray Validate(JArray answers, RevitMessageCatalogData catalog)
        {
            if (catalog == null) throw new ArgumentException("The target message catalogue is unavailable.");
            if (answers == null || answers.Count == 0 || answers.Count > MaxAnswers)
                throw new ArgumentException("answers must contain 1-" + MaxAnswers + " dialog/button pairs.");
            var accepted = new JArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in answers)
            {
                var item = token as JObject;
                if (item?["code"]?.Type != JTokenType.String || item?["button"]?.Type != JTokenType.String)
                    throw new ArgumentException("Each answer must contain string code and button fields.");
                var code = item.Value<string>("code").Trim();
                var button = item.Value<string>("button").Trim();
                if (code.Length == 0 || code.Length > 256 || button.Length == 0 || button.Length > 64)
                    throw new ArgumentException("Dialog code or button is empty or too long.");
                var entry = RevitMessageLookup.FindDialog(catalog, code);
                if (entry == null || string.Equals(entry.Id, "Dialog_Revit_DocWarnDialog", StringComparison.OrdinalIgnoreCase)
                    || !entry.Id.StartsWith("TaskDialog_", StringComparison.Ordinal))
                    throw new ArgumentException("Dialog " + code + " is outside the supported TaskDialog catalogue.");
                var common = (entry.CommonButtons ?? "").Split(',').Select(x => x.Trim())
                    .Where(x => x.Length > 0 && x != "None");
                var custom = entry.Buttons ?? new List<CatalogButton>();
                var normalized = common.FirstOrDefault(x => string.Equals(x, button, StringComparison.OrdinalIgnoreCase))
                    ?? custom.FirstOrDefault(x => x.Id == button)?.Id;
                var allowed = common.Concat(custom.Select(x => x.Id)).Where(x => !IsDenied(entry.Id, x)).ToArray();
                if (normalized == null || IsDenied(entry.Id, normalized))
                    throw new ArgumentException("Dialog " + entry.Id + ", button " + button + " is invalid or denied. Allowed buttons: " + string.Join(", ", allowed) + ".");
                if (!seen.Add(entry.Id)) throw new ArgumentException("Duplicate dialog code: " + entry.Id + ". Declare one answer per code.");
                accepted.Add(new JObject { ["code"] = entry.Id, ["button"] = normalized,
                    ["text"] = FailureCapture.SafeText(custom.FirstOrDefault(x => x.Id == normalized)?.Text ?? normalized, RevitMessageLookup.MaxButtonText) });
            }
            return accepted;
        }

        public static JObject Select(JArray answers, RevitMessageCatalogData catalog, string code,
            bool userChoice, bool stopped, bool storageFailed)
        {
            if (answers == null || answers.Count > MaxAnswers || userChoice || stopped || storageFailed) return null;
            try
            {
                // Validate the entire untrusted wire batch again against the in-process catalogue.
                return Validate(answers, catalog).OfType<JObject>().FirstOrDefault(x =>
                    string.Equals(x.Value<string>("code"), code, StringComparison.OrdinalIgnoreCase));
            }
            catch (ArgumentException) { return null; }
        }

        // Keep the write-ahead contract testable without loading the native API.
        public static bool ApplyRecorded(Func<bool> authorize, Func<bool> apply, Action<bool> recordResult)
        {
            if (!authorize()) return false;
            bool applied;
            try { applied = apply(); }
            catch { applied = false; }
            recordResult(applied);
            return applied;
        }
    }
}
