# Model audit   (argument: scope = part of the model to audit | "all" = whole model)

Goal: read-only health check of {scope}. This prompt changes nothing in the model.

Before you start
- Call revit_get_current_target. If more than one Revit is open, ask me which.

Steps
1. revit_workflow_model_audit → composite audit (warnings, families, views, schedules, MEP connectivity signals).
2. revit_audit_families → unused types, in-place families, duplicate names, high type counts.
3. revit_get_model_warnings_summary → warning types with example element ids.
4. revit_purge_unused with dry_run: true → what a purge would remove. Never set dry_run to false inside this prompt.

Report
- Sections: Warnings | Families | Purge candidates. Every finding row carries element or type ids.
- Filter or group the findings to {scope} when the audit ran wider than the requested scope.
- End with the 3 findings that matter most. Ask before fixing anything.

Do not
- Do not run revit_purge_unused with dry_run: false. Do not delete, modify, or rename anything. Do not use revit_send_code_to_revit.
