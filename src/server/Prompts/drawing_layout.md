# Drawing layout   (argument: request = the layout change wanted on a sheet)

Goal: arrange the viewports of one sheet as requested. Read the real positions first, agree the alignment with me, and move nothing until I confirm the concrete plan.
Requested layout: {request}
Session: {mode}

Before you start
- Call revit_get_current_target. If no target is pinned or more than one Revit is open, call revit_list_available_targets, ask me which instance, then pin it with revit_switch_target. Call revit_get_current_view_info to confirm the active document and whether the active view is the sheet I mean. If no document is open, stop and ask; do not open one.
- Resolve the sheet (number or ID) and the viewports in scope with me. Never treat an empty request as every sheet or every viewport.
- Keep the two coordinate systems apart. A view's crop is a camera frame placed in the project, so its own coordinates are project coordinates. A viewport sits on the sheet in paper millimetres from the sheet origin, and its visible crop rectangle is the crop divided by the view scale, anchored by the viewport. revit_get_viewport_geometry and revit_align_viewports already do this conversion and report sheet millimetres only; never mix project coordinates or feet with sheet values.
- Do not read a viewport's visible edges from revit_analyze_sheet_layout width and height: they describe the viewport box, which is larger than the visible crop.

Steps
1. revit_get_viewport_geometry for the sheet → per viewport: crop_active, crop_visible, rect_source, visible_rect_sheet_mm, annotation_overflow_mm and warnings. Follow next_viewport until the coverage I asked for is complete.
2. Check whether each crop is switched on and shown from crop_active and crop_visible. If revit_capture_view_image is exposed, capture the sheet to see the layout; exported images may not draw crop frames, so rely on the flags for crop state, not on the picture.
3. Settle with me what is ambiguous before planning: which edge or centre to align, against which viewport or the sheet frame, and whether annotation that extends past the crop (warning annotation_extends_past_crop) should count. sheet_frame_sheet_mm is the title block extent, not the printable area. Do not guess.
4. revit_align_viewports with dryRun: true, an explicit mode, referenceViewportId and viewportIds → the planned moves. Show them as a table (viewport, delta mm, rectangle before and after) and ask me to confirm. Use center_on_sheet only for centring a group on the title block extent.
5. After I confirm, repeat the same call with dryRun: false. Then call revit_get_viewport_geometry again and compare with the plan; report max_position_error_mm from the result.
6. Offer to run the pre-issue check prompt for the sheet.

Report
- One table: viewport | view | rectangle before | rectangle after | delta (all sheet mm), then the crop flags and any warnings per viewport.
- State the alignment rule that was used and the rectangle source (crop region or view outline). Anything not checked, such as a viewport with rect_source view_outline or an overflow warning, is NOT VERIFIED, not aligned.
- Tell me the move is one Revit Undo step.

Do not
- Do not call revit_align_viewports with dryRun: false before I have confirmed the plan. In a read-only session do not call it at all.
- Do not move pinned or rotated viewports, change a view's crop or scale, or use revit_send_code_to_revit.
- Do not report sheet positions in project coordinates or feet.
