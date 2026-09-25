# Pre-issue check   (argument: scope = explicit sheet numbers/IDs, an explicit number/name filter, or "all")

Goal: check the resolved sheets before issue. Read-only: change nothing.
Requested scope: {scope}.

Before you start
- Call revit_get_current_target. If more than one Revit is open, ask me which.
- If the scope is a named sheet set or is ambiguous, stop and ask me for its member sheet numbers or IDs. revit_list_sheets cannot resolve saved sheet-set membership. Do not treat a sheet set name as numberFilter or namePattern, and do not silently expand the scope to all sheets.

Steps
1. revit_list_sheets → resolve the supplied sheet numbers/IDs, an explicitly requested numberFilter/namePattern, or all sheets. Select exact members from the response; confirm missing or ambiguous matches rather than substituting other sheets.
2. Each resolved sheet: revit_analyze_sheet_layout → viewport rectangles and placed view IDs. Follow next_viewport using startViewport until the requested coverage is complete; infer possible overlaps from the rectangles, not a full design-quality verdict.
3. Placed views: revit_find_untagged_elements, revit_find_undimensioned_elements with explicit viewId and category. Ask which categories require tags/dimensions if that was not specified. Do not invent a project annotation standard.
4. revit_get_model_warnings_summary → model-wide warning context, not a view-filtered warning list. It returns bounded examples per warning type (default 5, maximum 100); absence from examples does not prove absence of warnings, even when truncated=false. Per-sheet warning status is NOT VERIFIED when total_warnings > 0 and no complete view-scoped evidence is available. Only a successful total_warnings=0 establishes absence of model warnings at that time, not general issue readiness. Do not assign global warning counts to individual sheets.
5. revit_list_revisions with includeSheets=true → match revision assignments to the resolved sheet IDs. Check the user's revision requirement rather than treating any revision as proof of readiness.

Report
- One table: sheet | check | PASS/FAIL/NOT VERIFIED | element ids | coverage.
- Report warning totals and sampled example IDs separately as model-wide context, not sheet-specific findings.
- PASS requires a completed check against an agreed criterion. Record NOT VERIFIED for unresolved scope, missing criteria, failed calls, skipped items, or incomplete/truncated responses (including list_sheets limit_hit/skipped and unfinished viewport pages). Zero returned rows without proven complete coverage, or no sampled match, is not a pass.
- End with the 3 most important observed findings or verification gaps. Do not declare the set ready to issue while required checks remain NOT VERIFIED. Ask before fixing anything.

Do not
- Do not delete, rename or retag anything. Do not use revit_send_code_to_revit. Do not turn missing or sampled evidence into a clean bill of health.
