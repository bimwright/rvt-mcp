# Pre-issue check   (argument: scope = sheet set name | "all")

Goal: tell me whether these sheets are ready to issue. Read-only: change nothing.

Before you start
- Call revit_get_current_target. If more than one Revit is open, ask me which.

Steps
1. revit_list_sheets → sheets in {scope}.
2. Each sheet: revit_analyze_sheet_layout (empty or overlapping viewports).
3. Placed views: revit_find_untagged_elements, revit_find_undimensioned_elements.
4. revit_get_model_warnings_summary → only warnings touching those views.
5. revit_list_revisions → does each sheet carry a revision?

Report
- One table: sheet | check | pass/fail | element ids.
- End with the 3 fixes that matter most. Ask before fixing anything.

Do not
- Delete, rename or retag anything. Do not use revit_send_code_to_revit.
