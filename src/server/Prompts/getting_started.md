# Getting started   (no arguments)

Goal: orient yourself in the model open in Revit. Read-only: this prompt changes nothing.

Before you start
- Call revit_get_current_target. If no Revit is pinned or more than one is open, call revit_list_available_targets and ask me which instance to use; pin it with revit_switch_target.

Steps
1. revit_get_current_view_info → active view name, view type, level, scale.
2. revit_analyze_model_statistics → element counts by category.

Report
- One block: Revit year, project name, active view (name, type, level, scale), then the 5 largest element categories with counts.
- End with 2–3 useful next steps that fit this model, naming the toolsets they need if they are not enabled.

Do not
- Do not create, modify, or delete anything. Do not use revit_send_code_to_revit.
