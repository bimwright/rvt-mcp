# Stairs via send_code   (no arguments)

Goal: create or adapt a stair with me, through revit_send_code_to_revit. This prompt writes to the model only after I confirm the resolved design.

Before you start
- Call revit_get_current_target and confirm the active document; if more than one Revit is open, ask me which.
- If docs/stairs-workflow.md is available in your context, read it; the rules below already condense it.

Steps
1. Read first: levels and offsets, existing stair and railing types, any selected or reference stair, editable state. Resolve names to element ids in this document — never reuse ids from another project.
2. Ask the smallest useful set of questions: purpose and scope; levels and offsets; footprint and ascent direction; layout (straight, L, U); which existing stair/railing or project types to match; which dimensions are fixed and which I may propose.
3. State the resolved design in one short paragraph — levels, direction, run count, riser allocation, width, landing, stair type, railing per edge. List unresolved choices instead of filling them with defaults.
4. Get my confirmation before writing anything.
5. Adapt a send_code payload: keep the transaction, edit-scope, failure-handling and commit-check pattern from docs/send-code.md; remove demo behavior unless I asked for it.
6. Run revit_send_code_to_revit once.
7. Verify the built stair against the agreed design and report.

Report
- Created element ids and the outcome committed | committed_with_warnings | not_committed, with warning details.
- What was actually verified vs. what could not be measured. Whether the model was saved.

Do not
- Do not run revit_send_code_to_revit before I confirm the resolved design. Do not edit shared stair, railing, or handrail types unless I authorized it. Do not rerun creation blindly after a timeout or ambiguous response — inspect the model first.
