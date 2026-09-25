# Stairs via send_code   (no arguments)

Goal: create or adapt a stair with me, through revit_send_code_to_revit. This prompt writes to the model only after I confirm the resolved design.

Before you start
- Call revit_get_current_target and confirm the active document; if more than one Revit is open, ask me which.
- This prompt includes its execution safeguards; no source checkout or external documentation is required. The template uses the plugin's SafeFailuresPreprocessor (v0.6.2+); restart Revit after upgrading the plugin. If that helper is unavailable, stop and resolve the version mismatch rather than bypassing failure handling.

Steps
1. Read first: levels and offsets, existing stair and railing types, any selected or reference stair, editable state. Resolve names to element ids in this document — never reuse ids from another project.
2. Ask the smallest useful set of questions: purpose and scope; levels and offsets; footprint and ascent direction; layout (straight, L, U); which existing stair/railing or project types to match; which dimensions are fixed and which I may propose.
3. State the resolved design in one short paragraph — levels, direction, run count, riser allocation, width, landing, stair type, railing per edge. List unresolved choices instead of filling them with defaults.
4. Get my confirmation before writing anything.
5. Adapt a C# send_code payload using the self-contained template below. Supply geometry and types from the resolved design; do not create test levels, switch views, save/export files, or substitute railing types unless I asked for that behavior.
6. Run revit_send_code_to_revit once.
7. Verify the built stair against the agreed design and report.

### Execution template for step 5

This is a control-flow template, not a complete stair generator. Resolve bottomLevelId/topLevelId in the active document and implement the agreed runs/landings before execution. The send_code body provides app, doc and uidoc; use C#, not Python or TypeScript. StairsEditScope is in Autodesk.Revit.DB; Stairs and StairsRun are in Autodesk.Revit.DB.Architecture.

Begin the scope with no open transaction. Use one failure helper for both commits. Dispose the inner transaction before committing or cancelling the scope. The helper records warnings in Messages and removes them from Revit failure processing; errors request rollback. Return those messages even if the transaction committed: deleting a warning is not proof of design quality.

```csharp
// bottomLevelId/topLevelId are validated level IDs from the current document.
var failures = new RvtMcp.Plugin.SafeFailuresPreprocessor();
using (var scope = new StairsEditScope(doc, "Create stairs"))
{
    try
    {
        var stairsId = scope.Start(bottomLevelId, topLevelId);
        TransactionStatus txStatus;
        using (var tx = new Transaction(doc, "Create stair components"))
        {
            tx.Start();
            tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions()
                .SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
            // Create and validate the agreed runs/landings here.
            txStatus = tx.Commit();
        } // Dispose the inner transaction before committing/cancelling the scope.
        if (txStatus != TransactionStatus.Committed)
            return new { outcome = "not_committed", failures.Messages };

        scope.Commit(failures);
        bool committed = !scope.IsActive && !failures.HadErrors
            && doc.GetElement(stairsId) != null;
        return new
        {
            committed,
            element_id = committed ? stairsId.ToString() : null,
            outcome = !committed ? "not_committed"
                : failures.HadWarnings ? "committed_with_warnings" : "committed",
            requiresReview = failures.HadWarnings,
            failures.Messages
        };
    }
    finally
    {
        // Runs on exceptions too; do not swallow a failed cleanup.
        if (scope.IsActive) scope.Cancel();
    }
}
```

An element ID alone does not prove persistence; check commit status, HadErrors and element existence. Preserve exception cleanup, and never report a failed cleanup as a successful rollback. This template is for creating stairs, not a generic editor for existing stairs: verify the supported API path before adapting an existing stair. Do not remove existing stairs or railings as an unrequested fallback.

Report
- Created element ids and the outcome committed | committed_with_warnings | not_committed, with warning details.
- What was actually verified vs. what could not be measured. Whether the model was saved.

Do not
- Do not run revit_send_code_to_revit before I confirm the resolved design. Do not edit shared stair, railing, or handrail types unless I authorized it. Do not rerun creation blindly after a timeout or ambiguous response — inspect the model first.
