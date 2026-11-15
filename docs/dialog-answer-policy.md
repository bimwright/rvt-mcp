# Agent dialog answer policy

Unreleased Phase B policy. Revit supplies the catalogue and button values at runtime; these identifiers contain no bundled Autodesk text. This table applies only to `revit_set_dialog_answers`. It does not override user rules/grants or native failure guards.

D and T pairs are denied. P003/P005 are denied; the other P pairs remain temporarily denied pending evidence and a specific policy change. A pairs are illustrative exceptions, accepted only if valid in the target catalogue. Other catalogue-valid pairs are allowed unless denied; this is not an A-only allowlist. Cancel/No/Close are never universally safe: each pair retains its own semantics.

| Decision | Dialog code | Button | Policy |
| --- | --- | --- | --- |
| D001 | `TaskDialog_Project_Not_Saved_Recently` | `1001` | denied |
| D002 | `TaskDialog_Project_Not_Saved_Recently` | `1002` | denied |
| D003 | `TaskDialog_Changes_Not_Saved` | `1001` | denied |
| D004 | `TaskDialog_Changes_Not_Saved` | `1002` | denied |
| D005 | `TaskDialog_Changes_Not_Saved` | `1003` | denied |
| D006 | `TaskDialog_Save_File` | `Yes` | denied |
| D007 | `TaskDialog_Changes_Not_Saved_To_Local_File` | `Yes` | denied |
| D008 | `TaskDialog_Confirm_Save_Location` | `Yes` | denied |
| D009 | `TaskDialog_Invalid_Central_Model_Extension` | `Yes` | denied |
| D010 | `TaskDialog_Save_File_As_Central_Model` | `Yes` | denied |
| D011 | `TaskDialog_Save_As_Central_Model` | `1002` | denied |
| D012 | `TaskDialog_Save_As_Revit_Model` | `1001` | denied |
| D013 | `TaskDialog_Loading_Transmitted_File` | `1001` | denied |
| D014 | `TaskDialog_Unsynced_Changes` | `1001` | denied |
| D015 | `TaskDialog_Serious_Error_Occurred_Save_Recovery_File` | `Yes` | denied |
| D016 | `TaskDialog_Serious_Error_Occurred_Recoverable` | `Yes` | denied |
| D017 | `TaskDialog_Fatal_Error_Occured` | `Yes` | denied |
| D018 | `TaskDialog_Close_Project_Without_Saving` | `1001` | denied |
| D019 | `TaskDialog_Close_Project_Without_Saving` | `1002` | denied |
| D020 | `TaskDialog_Close_Single_User_Rollback_Incompatible_Model` | `1001` | denied |
| D021 | `TaskDialog_Close_Single_User_Rollback_Incompatible_Model` | `1002` | denied |
| D022 | `TaskDialog_Close_Rollback_Incompatible_Model` | `1001` | denied |
| D023 | `TaskDialog_Close_Rollback_Incompatible_Model` | `1002` | denied |
| D024 | `TaskDialog_Close_Relinquish_Incompatible_Model` | `1001` | denied |
| D025 | `TaskDialog_Close_Relinquish_Incompatible_Model` | `1002` | denied |
| D026 | `TaskDialog_Local_Changes_Not_Synchronized_With_Central` | `1002` | denied |
| D027 | `TaskDialog_Local_Changes_Not_Synchronized_With_Central` | `1003` | denied |
| D028 | `TaskDialog_Single_User_Local_Changes_Not_Saved_To_Cloud` | `1001` | denied |
| D029 | `TaskDialog_Single_User_Local_Changes_Not_Saved_To_Cloud` | `1002` | denied |
| D030 | `TaskDialog_Unsubmitted_Changes` | `1002` | denied |
| D031 | `TaskDialog_Single_User_Unsubmitted_Changes` | `1002` | denied |
| D032 | `TaskDialog_Open_Central_Model_Version_Archived` | `1002` | denied |
| D033 | `TaskDialog_Open_Rollback_Incompatible_Model` | `1002` | denied |
| D034 | `TaskDialog_Open_Single_User_Rollback_Incompatible_Model` | `1002` | denied |
| D035 | `TaskDialog_Open_Relinquish_Incompatible_Model` | `1002` | denied |
| D036 | `TaskDialog_Missing_Third_Party_Updater` | `1003` | denied |
| D037 | `TaskDialog_Missing_Third_Party_Updater` | `1004` | denied |
| D038 | `TaskDialog_Missing_Third_Party_Updaters` | `1003` | denied |
| D039 | `TaskDialog_Missing_Third_Party_Updaters` | `1004` | denied |
| D040 | `TaskDialog_Wait_For_Grant_In_Modal_Or_Canvas` | `1002` | denied |
| D041 | `TaskDialog_Sketch_Edits_Discarded` | `Yes` | denied |
| D042 | `TaskDialog_Sketch_Discarded` | `Yes` | denied |
| D043 | `TaskDialog_Quit_Paste_Edit_Mode` | `Yes` | denied |
| D044 | `TaskDialog_Quit_Toposurface` | `Yes` | denied |
| D045 | `TaskDialog_Quit_In_Place_Family` | `Yes` | denied |
| D046 | `TaskDialog_Model_Upgrade_Central` | `1001` | denied |
| D047 | `TaskDialog_Changes_Not_Synchronized_With_Central` | `1001` | denied |
| D048 | `TaskDialog_Changes_Not_Synchronized_With_Central` | `1002` | denied |
| D049 | `TaskDialog_Editable_Elements` | `1001` | denied |
| D050 | `TaskDialog_Save_Changes_To_Local_File` | `1001` | denied |
| D051 | `TaskDialog_Repair_Local_Missing_Unique_Elements` | `1001` | denied |
| D052 | `TaskDialog_Warn_To_Repair_Corrupt_Central_Model` | `Ok` | denied |
| D053 | `TaskDialog_Corrupt_Model_Detected_On_Save` | `1001` | denied |
| D054 | `TaskDialog_Dynamic_IRDB_Sync_To_Central` | `Ok` | denied |
| D055 | `TaskDialog_Confirm_Create_New_Local` | `Ok` | denied |
| D056 | `TaskDialog_Location_Position_Changed` | `1001` | denied |
| D057 | `TaskDialog_Location_Position_Changed_For_Unloading_Locally` | `1001` | denied |
| D058 | `TaskDialog_Host_Model_Has_Different_Geo_Location_Data` | `1001` | denied |
| D059 | `TaskDialog_Host_Model_Has_No_Geo_Location_Data` | `1001` | denied |
| D060 | `TaskDialog_Cannot_Modify_Linked_File` | `Yes` | denied |
| D061 | `TaskDialog_Cannot_Modify_Imported_File` | `1001` | denied |
| D062 | `TaskDialog_Cannot_Modify_Imported_File` | `1002` | denied |
| D063 | `TaskDialog_Convert_To_Link` | `1001` | denied |
| D064 | `TaskDialog_Overwrite_Link_File` | `Yes` | denied |
| D065 | `TaskDialog_Modified_Shared_Parameter_File` | `Yes` | denied |
| D066 | `TaskDialog_Overwrite_File` | `Yes` | denied |
| D067 | `TaskDialog_Overwrite_File_List` | `Yes` | denied |
| D068 | `TaskDialog_Replace_Existing_File` | `Yes` | denied |
| D069 | `TaskDialog_Replace_File` | `Yes` | denied |
| D070 | `TaskDialog_Replace_Multiple_Files` | `Yes` | denied |
| D071 | `TaskDialog_Workset_File_Already_Exists` | `Yes` | denied |
| D072 | `TaskDialog_Newer_File_Exists` | `Yes` | denied |
| D073 | `TaskDialog_Backup_Failed` | `Yes` | denied |
| D074 | `TaskDialog_File_Name_In_Use` | `1001` | denied |
| D075 | `TaskDialog_File_Name_In_Use` | `1002` | denied |
| D076 | `TaskDialog_File_Name_In_Use_LT` | `1001` | denied |
| D077 | `TaskDialog_File_Name_In_Use_LT` | `1002` | denied |
| D078 | `TaskDialog_Folder_Not_Empty` | `1001` | denied |
| P001 | `TaskDialog_Project_Not_Saved_Recently` | `1003` | temporarily denied |
| P002 | `TaskDialog_Changes_Not_Synchronized_With_Central` | `1003` | temporarily denied |
| P003 | `TaskDialog_Location_Position_Changed` | `1002` | denied |
| P004 | `TaskDialog_Location_Position_Changed` | `1003` | temporarily denied |
| P005 | `TaskDialog_Location_Position_Changed_For_Unloading_Locally` | `1002` | denied |
| P006 | `TaskDialog_Update_Saved_Position` | `Ok` | temporarily denied |
| P007 | `TaskDialog_Confirm_Save_Location` | `No` | temporarily denied |
| P008 | `TaskDialog_Changes_Not_Saved_To_Local_File` | `No` | temporarily denied |
| P009 | `TaskDialog_Editable_Elements` | `1002` | temporarily denied |
| P010 | `TaskDialog_Save_Changes_To_Local_File` | `1002` | temporarily denied |
| P011 | `TaskDialog_Serious_Error_Occurred_Save_Recovery_File` | `No` | temporarily denied |
| P012 | `TaskDialog_Serious_Error_Occurred_Recoverable` | `No` | temporarily denied |
| P013 | `TaskDialog_Fatal_Error_Occured` | `No` | temporarily denied |
| P014 | `TaskDialog_Model_Upgrade_Local` | `1001` | temporarily denied |
| P015 | `TaskDialog_Detach_Model_From_Central` | `1001` | temporarily denied |
| P016 | `TaskDialog_Detach_Model_From_Central` | `1002` | temporarily denied |
| P017 | `TaskDialog_Open_Model_With_Unsupported_Feature` | `1001` | temporarily denied |
| P018 | `TaskDialog_Schema_Conflict_Load` | `1002` | temporarily denied |
| P019 | `TaskDialog_Shortcut_Changes_Not_Saved` | `1001` | temporarily denied |
| P020 | `TaskDialog_Shortcut_Changes_Not_Saved` | `1002` | temporarily denied |
| P021 | `TaskDialog_Replace_Existing_Keyboard_Shortcuts` | `1001` | temporarily denied |
| P022 | `TaskDialog_Replace_Existing_Keyboard_Shortcuts` | `1002` | temporarily denied |
| P023 | `TaskDialog_Save_Settings` | `Yes` | temporarily denied |
| P024 | `TaskDialog_Save_Settings` | `No` | temporarily denied |
| P025 | `TaskDialog_Asset_Not_Saved` | `Yes` | temporarily denied |
| P026 | `TaskDialog_Asset_Not_Saved` | `No` | temporarily denied |
| P027 | `TaskDialog_Overwrite_Property_Set_Library` | `Yes` | temporarily denied |
| P028 | `TaskDialog_Changes_To_Panel_Schedules_Unsaved` | `1002` | temporarily denied |
| P029 | `TaskDialog_Missing_Third_Party_Updater` | `1002` | temporarily denied |
| P030 | `TaskDialog_Missing_Third_Party_Updaters` | `1002` | temporarily denied |
| P031 | `TaskDialog_Save_File` | `No` | temporarily denied |
| P032 | `TaskDialog_Modified_Shared_Parameter_File` | `No` | temporarily denied |
| P033 | `TaskDialog_Cannot_Relinquish_Renamed_Workset` | `Yes` | temporarily denied |
| P034 | `TaskDialog_Edit_Workset_At_Risk` | `Yes` | temporarily denied |
| P035 | `TaskDialog_Model_Opened_By_Another_User` | `1001` | temporarily denied |
| P036 | `TaskDialog_Energy_Model_Creation_Will_End_If_File_Closed` | `Yes` | temporarily denied |
| P037 | `TaskDialog_Energy_Model_Geometry_Wont_Be_Saved` | `Ok` | temporarily denied |
| P038 | `TaskDialog_Fabrication_Network_Inline_Fittings_Need_Attention` | `1002` | temporarily denied |
| T001 | `TaskDialog_Security_Unsigned_File_Loading` | `1001` | denied |
| T002 | `TaskDialog_Security_Unsigned_File_Loading` | `1002` | denied |
| T003 | `TaskDialog_Security_Invalid_Signature` | `1001` | denied |
| T004 | `TaskDialog_Security_Signed_File_Not_In_Trusted_Folder` | `1001` | denied |
| A001 | `TaskDialog_Model_Upgrade_Central` | `1002` | allowed if in target catalogue |
| A002 | `TaskDialog_Model_Upgrade_Local` | `1002` | allowed if in target catalogue |
| A003 | `TaskDialog_Model_Upgrade` | `1001` | allowed if in target catalogue |
| A004 | `TaskDialog_Model_Upgrade` | `1002` | allowed if in target catalogue |
| A005 | `TaskDialog_Unresolved_References` | `1002` | allowed if in target catalogue |
| A006 | `TaskDialog_Views_Related_To_Analytical_Changed` | `Close` | allowed if in target catalogue |
| A007 | `TaskDialog_Save_As_Central_Model` | `1001` | allowed if in target catalogue |
| A008 | `TaskDialog_Save_File_As_Central_Model` | `No` | allowed if in target catalogue |
| A009 | `TaskDialog_Invalid_Central_Model_Extension` | `No` | allowed if in target catalogue |
| A010 | `TaskDialog_Loading_Transmitted_File` | `1002` | allowed if in target catalogue |
| A011 | `TaskDialog_Host_Model_Has_Different_Geo_Location_Data` | `1002` | allowed if in target catalogue |
| A012 | `TaskDialog_Host_Model_Has_No_Geo_Location_Data` | `1002` | allowed if in target catalogue |
| A013 | `TaskDialog_Cannot_Modify_Linked_File` | `No` | allowed if in target catalogue |
| A014 | `TaskDialog_Cannot_Modify_Imported_File` | `1003` | allowed if in target catalogue |
| A015 | `TaskDialog_Repair_Local_Missing_Unique_Elements` | `1002` | allowed if in target catalogue |
| A016 | `TaskDialog_Warn_To_Repair_Corrupt_Central_Model` | `Cancel` | allowed if in target catalogue |
| A017 | `TaskDialog_Unsaved_Changes_In_Worksets` | `Close` | allowed if in target catalogue |
| A018 | `TaskDialog_Changed_Worksets_Not_Saved` | `Close` | allowed if in target catalogue |
| A019 | `TaskDialog_Incompatible_Version_Of_Central_Model` | `Close` | allowed if in target catalogue |
| A020 | `TaskDialog_Local_File_Incompatible_With_Central_Model` | `Close` | allowed if in target catalogue |
| A021 | `TaskDialog_Save_As_Central_Model_Old_Version` | `Close` | allowed if in target catalogue |
| A022 | `TaskDialog_Worksets_In_Central_Model_Not_Upgraded` | `Close` | allowed if in target catalogue |
| A023 | `TaskDialog_Security_Unsigned_File_Loading` | `1003` | allowed if in target catalogue |
| A024 | `TaskDialog_Security_Invalid_Signature` | `1002` | allowed if in target catalogue |
| A025 | `TaskDialog_Security_Signed_File_Not_In_Trusted_Folder` | `1002` | allowed if in target catalogue |

The catalogue validates available buttons; it is not native acceptance evidence for every Revit year/build/language. Windows dialogs, uncatalogued or anonymous dialogs, the agent's own message and `Dialog_Revit_DocWarnDialog` are excluded.
