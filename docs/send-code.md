# Send-code source forms and failure handling

The source-builder and `SafeFailuresPreprocessor` additions below shipped in v0.6.2. v0.6.1 and earlier support a plain body or complete source, but detect complete source using the literal substring `class `, including inside comments/strings. Revit must restart after installing an updated plugin.

## Discarding documents opened by a script

During a synchronous `send_code_to_revit` call, direct, statically bound Revit calls to
`OpenDocumentFile`, `EditFamily`, `NewFamilyDocument`, and `NewProjectDocument` track documents
newly opened by that script. An explicit `document.Close(false)` can discard such a document,
including during cleanup after a failed transaction. It never implies permission to save.
Documents already open at the start of the call, documents left over from an earlier call,
and `Close(true)` retain the existing native-operation checks.

The compiler instruments these Revit calls by symbol, not by matching text. Reflection,
dynamic dispatch, conditional access, method-group delegates and precompiled helpers are
not automatically instrumented; conditional syntax and null short-circuiting stay intact. For those cases, explicitly wrap the open result with
`RvtMcp.Plugin.McpDocument.TrackOpened(...)` and close via
`RvtMcp.Plugin.McpDocument.Close(document, false)` in the same call.
Revit's own restrictions, such as not closing the active UI document or a document with an
open transaction, still apply. Check the returned Boolean; the plugin does not force a close.

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

For a stair creation request, start with the [stairs conversation workflow](stairs-workflow.md). It explains execution safeguards and provides questions for unresolved design choices. Use it to agree the layout and railing intent before adapting the technical pattern below; a dedicated `create_stairs` tool is deferred.

`StairsEditScope` is in `Autodesk.Revit.DB`; `Stairs` and `StairsRun` are in `Autodesk.Revit.DB.Architecture`. Begin the scope with no open transaction, then create runs/landings inside an inner transaction. Check the transaction result before committing the scope.

The plugin provides `RvtMcp.Plugin.SafeFailuresPreprocessor`. During an MCP operation it records failures in `Messages`, preserves severity in `HadWarnings`/`HadErrors`, and sets `WasRolledBack` when the controlled policy requests rollback. Warnings default to rollback; matching warning decisions/rules or the explicit `accept-and-log` [warning policy](controlled-failures.md#warning-policy) can permit continuation after complete capture and durable recording. Error, DocumentCorruption, incomplete capture and current persistence failure still require rollback. Outside an MCP operation, the helper retains its existing opt-in behavior. Warning details must still be returned; acceptance does not prove design quality.

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
        if (txStatus != TransactionStatus.Committed || failures.WasRolledBack)
            return new { outcome = "not_committed", failures.Messages };

        scope.Commit(failures);
        bool committed = !scope.IsActive && !failures.HadErrors && !failures.WasRolledBack
            && doc.GetElement(stairsId) != null;
        return new
        {
            committed,
            outcome = !committed ? "not_committed"
                : failures.HadWarnings ? "committed_with_warnings" : "committed",
            requiresReview = failures.HadWarnings,
            failures.WasRolledBack,
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

The older stair probes below predate controlled failure handling; they do not verify the new default rollback policy. The [1.2.1 source validation summary](testing/2026-10-20-v1.2.1-validation.md) separately records scoped native Warning/Error policy checks on Revit 2026; it does not establish native stair acceptance on every year. Inspect `_failure` and `_operation` even when custom code returns success. A policy stop prevents subsequent supported transaction commits in the same call but cannot undo earlier commits or external side effects; those calls may have partial outcomes. Custom preprocessors and arbitrary code have unverified failure coverage. Do not retry unchanged after a failure, timeout, or pending result; inspect the operation, incident and model first.

Revit 2027 build 27.0.10.13 passed source-form, warning-reporting, injected-Error rollback, and exception-cleanup probes in a fresh process loading the deployed plugin. These checks do not reproduce the original Revit 2025 crash. The native `create_stairs` proposal is deferred in favor of the documented conversation and send-code workflow.

Live probes on Revit 2022 build 22.0.2.392 created a valid straight stair through both full-source and body/helper forms, then rolled back the outer diagnostic group. An injected Error caused transaction rollback without a remaining stair. A 100 mm tread depth produced a Warning on the tested model, so this does not reproduce the reported Revit 2025 crash or establish the same severity across models/versions.

Follow-up probes verified `committed_with_warnings` with the tread warning preserved in `Messages`. Both a deliberately thrown geometry-path exception and `Commit(null)` left the scope active before cleanup; the `finally` block cancelled it, and a subsequent stairs scope could start. This validates those cleanup paths, not recovery from an arbitrary native crash.

Source: [GitHub issue #14](https://github.com/bimwright/rvt-mcp/issues/14); [Autodesk failure-processing results](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/f147e6e6-4b2e-d61c-df9b-8b8e5ebe3fcb.htm). Local Revit 2022 API XML and live reflection were used to verify the 2022 namespace and signatures.
