# Send-code source forms and failure handling

## JSON inputs, helper modules and explicit task results

Enhanced calls require a selected, authenticated gateway/add-in pair advertising `send_code_v2`.
Update both components together and restart the selected Revit host and MCP client before using the new surface.
`SCRIPT_CAPABILITY_REQUIRED` means no enhanced script was sent; inputs are never dropped to retry as code-only.
Revit Home is supported after explicitly selecting the host. Read-only/send-code policy and pairing guards still apply.

| Argument | Default | Purpose |
|---|---|---|
| `code` | required | C# body or complete source |
| `data` | `null` | JSON object; arrays belong inside it |
| `modules` | `[]` | Stored helper names and their dependencies |
| `compile_only` | `false` | Compile/emit without loading or invoking user code |
| `result_contract` | `"legacy"` | `"task_v1"` requires the add-in's actual `McpTaskResult` type |
| `usage_context` | `"unknown"` | Declare `"project"` or `"test"`; no inference from source text |

Omitted/null data, no modules and all other defaults preserve the legacy code-only request.
With data, body scripts receive `JObject data`. Complete source uses exactly
`public static object Run(UIApplication app, JObject data)` on `public class McpDynamicScript`;
include `using Autodesk.Revit.UI; using Newtonsoft.Json.Linq;` in complete source.
Without data, retain `Run(UIApplication app)`. An entry shim parses JSON text inside the generated assembly;
do not pass add-in-owned JSON objects into dynamically loaded code yourself.

Save a declarations-only helper once with `revit_save_code_module`:

```json
{
  "name": "quantity_checks",
  "code": "public static class QuantityChecks { public static bool Within(double actual, double expected, double tolerance) => System.Math.Abs(actual - expected) <= tolerance; }",
  "description": "Compare a quantity with an expected value and tolerance",
  "requires": [],
  "expected_hash": null
}
```

Then call `revit_send_code_to_revit` with a small body and fresh data:

```json
{
  "code": "return QuantityChecks.Within((double)data[\"actual\"], (double)data[\"expected\"], (double)data[\"tolerance\"]) ? McpTaskResult.Pass(new { within = true }) : McpTaskResult.Fail(\"OUT_OF_TOLERANCE\", \"Quantity differs from the expected value\");",
  "data": { "actual": 12.5, "expected": 12.5, "tolerance": 0.01 },
  "modules": ["quantity_checks"],
  "result_contract": "task_v1",
  "usage_context": "test"
}
```

`task_v1` body scripts import `RvtMcp.Plugin` automatically. Return `McpTaskResult.Pass(value)` or
`McpTaskResult.Fail(code, message, details)`; an anonymous object with `passed=true` is not the contract.
Missing task results produce `TASK_RESULT_REQUIRED`. A task failure after a committed edit does not undo it:
inspect `_task` and `_operation` separately. Native partial/rollback/cancellation remains a failure even if
the script returns Pass. Native `unknown` is not proof of verified model behavior. Legacy top-level
`passed/ok/success=false` adds an advisory hint without changing legacy success semantics.

Set `compile_only=true` to validate the same source/data shape and helper graph without running constructors,
initializers or methods. Compile success reports `compiled=true`, `executed=false` and
`_task.status="not_reported"`; it proves compilation for that host/runtime, not successful model work.
Internal module validation has its own operation/child logical ID linked to the save and is ineligible for learning.

### Module storage, limits and recovery

`revit_list_code_modules` works without Revit: filter by `name_filter`, page with `start_index` and `limit`
(default 100, maximum 500), and request source only with `include_code=true`. It returns revision hash,
dependency status, signatures and scoped compile metadata. Large output uses the existing budget/spill path.
Save/delete mutate local storage and cannot be undone with Revit Undo. They work independently of adaptive bake.

New names require `expected_hash=null`; replacements and `revit_delete_code_module` require the current revision hash.
Read/list first, review source/dependencies, then submit the exact hash. `MODULE_CONFLICT` leaves storage unchanged;
read the current record and review the conflict before resubmitting. Dependency changes during compile also refuse the save.
If `dependency_status` is changed/missing or a pinned operation reports `MODULE_CHANGED`, inspect the graph and revalidate
against the intended host. Do not replay a model mutation just to refresh metadata. Delete refuses live module dependents;
there is no force delete. Compile metadata is scoped to a year/runtime; it never verifies every Revit year.

Names match `^[a-z][a-z0-9_]{0,63}$`. Duplicate/self dependencies and cycles are refused. Limits are UTF-8:
64 KiB per module, 32 resolved modules, 256 KiB total module source, dependency depth 16;
data is at most 1 MiB raw and 512 KiB compact with JSON depth 64. Duplicate keys, scalar/array roots and invalid modes
are rejected. The complete escaped wire request, including resolved sources and metadata, must fit 1 MiB.
Oversized requests fail before dispatch; the gateway does not truncate, split or automatically replay them.

Credential-like module source/description is rejected for persistence; supply sensitive values as fresh runtime data.
Saved helpers live in the existing local `bake.db` with immutable revisions. Explicit module persistence is independent
of body-cache/journal opt-ins. Audit retains input hashes/lengths and module identity, not raw data/module bodies;
source display caching and journaling retain their existing opt-in/TTL policies. History explains why enhanced calls cannot
be rerun from hash-only records: call the MCP tool with fresh inputs and current module selection instead.

Compiled ToolBaker tools retain their existing list/run/accept behavior; helpers do not create baked tools.

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
