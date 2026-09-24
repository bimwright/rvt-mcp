# Send-code source forms and failure handling

The source-builder and `SafeFailuresPreprocessor` additions below shipped in v0.6.2. v0.6.1 and earlier support a plain body or complete source, but detect complete source using the literal substring `class `, including inside comments/strings. Revit must restart after installing an updated plugin.

## Source forms

The updated handler accepts a body with `app`, `doc`, and `uidoc`, ending in `return ...;`. Helper type declarations can accompany the body; the compiler wrapper keeps them outside `Run`:

```csharp
var helper = new ExampleHelper();
return helper.Value;
public class ExampleHelper { public int Value => 14; }
```

A complete compilation unit continues to work, including on v0.6.1. Include your own imports and the exact public entrypoint:

```csharp
using Autodesk.Revit.UI;
public class McpDynamicScript
{
    public static object Run(UIApplication app)
    {
        var doc = app.ActiveUIDocument.Document;
        return doc.Title;
    }
}
// Additional helper types may follow.
```

The language remains C#. TypeScript or Python payloads are not supported by this Roslyn-based endpoint. A different language engine would still need to respect Revit API transactions, interface implementations, and failure handling.

## Stairs and failure handling

For a stair creation request, start with the [stairs conversation workflow](stairs-workflow.md). It links the tested scripts, explains their dependencies, and provides questions for unresolved design choices. Use it to agree the layout and railing intent before adapting the technical pattern below; a dedicated `create_stairs` tool is deferred.

`StairsEditScope` is in `Autodesk.Revit.DB`; `Stairs` and `StairsRun` are in `Autodesk.Revit.DB.Architecture`. Begin the scope with no open transaction, then create runs/landings inside an inner transaction. Check the transaction result before committing the scope.

The updated plugin provides `RvtMcp.Plugin.SafeFailuresPreprocessor`. It records each failure in `Messages`, flags `HadWarnings`, deletes warnings from Revit's failure processing, and requests a silent rollback for errors by setting `ClearAfterRollback`. Warning details must still be returned to the caller. This is opt-in; it does not make invalid geometry valid, and deleting warnings is not a design-quality check. In particular, a below-minimum tread-depth warning still requires review even when Revit permits the commit.

Use the same helper for the inner transaction and scope so failures remain inspectable:

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
            // Create and validate runs/landings here.
            txStatus = tx.Commit();
        } // Close the inner transaction before committing/cancelling the scope.
        if (txStatus != TransactionStatus.Committed)
            return new { outcome = "not_committed", failures.Messages };

        scope.Commit(failures);
        bool committed = !scope.IsActive && !failures.HadErrors
            && doc.GetElement(stairsId) != null;
        return new
        {
            committed,
            outcome = !committed ? "not_committed"
                : failures.HadWarnings ? "committed_with_warnings" : "committed",
            requiresReview = failures.HadWarnings,
            failures.Messages
        };
    }
    finally
    {
        // Also runs when geometry creation or Commit throws; do not swallow cleanup errors.
        if (scope.IsActive) scope.Cancel();
    }
}
```

This is a control-flow template, not a complete stair generator. `scope.Commit(new RvtMcp.Plugin.SafeFailuresPreprocessor())` also works, but retaining the instance allows error inspection. Returning an element ID alone does not prove the scope committed; it may have rolled back. `committed` describes persistence only, not dimensional compliance. Do not report a warning-bearing result as clean success. An exception propagates to send-code's error response after the cleanup attempt; a failed cleanup must not be represented as a successful rollback.

## Verification limits

Revit 2027 build 27.0.10.13 also passed these source-form, warning-reporting, injected-Error rollback, and exception-cleanup probes in a fresh process loading the deployed plugin. A separate retained stair was then created entirely through `revit_send_code_to_revit`, with 15 risers, 280 mm tread depth, and 1,200 mm run width; the helper reported no warnings or errors. See the [tested payload, image, and verification record](testing/issue-14/README.md). This does not reproduce the original Revit 2025 crash. The native `create_stairs` proposal is deferred in favor of the documented conversation and send-code workflow.

Live probes on Revit 2022 build 22.0.2.392 created a valid straight stair through both full-source and body/helper forms, then rolled back the outer diagnostic group. An injected Error caused transaction rollback without a remaining stair. A 100 mm tread depth produced a Warning on the tested model, so this does not reproduce the reported Revit 2025 crash or establish the same severity across models/versions.

Follow-up probes verified `committed_with_warnings` with the tread warning preserved in `Messages`. Both a deliberately thrown geometry-path exception and `Commit(null)` left the scope active before cleanup; the `finally` block cancelled it, and a subsequent stairs scope could start. This validates those cleanup paths, not recovery from an arbitrary native crash.

Source: [GitHub issue #14](https://github.com/bimwright/rvt-mcp/issues/14); [Autodesk failure-processing results](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/f147e6e6-4b2e-d61c-df9b-8b8e5ebe3fcb.htm). Local Revit 2022 API XML and live reflection were used to verify the 2022 namespace and signatures.
