# Tool descriptor benchmark — 2026-10-18

This measures discovery payload size, not model tokens, tool execution time or task accuracy. It invokes no Revit tool. The separate [validation record](../testing/2026-10-18-v1.2.0-validation.md) lists retained native checks and pending release coverage.

| Setup | Value |
|---|---|
| Source | `e03f4d16c1746beb4ddd1d6bd0add997cbeacaf8`, clean checkout |
| Server version | `1.2.0+e03f4d16c1746beb4ddd1d6bd0add997cbeacaf8` |
| Machine | Windows x64 development workstation; .NET 8.0.32 |
| Driver | xUnit through `dotnet test`; SDK-created protocol descriptors |
| Host/year | None contacted; Revit execution is outside this measurement |
| Surface | Built-in tools; adaptive bake off; default, all, all read-only |
| Serialization | SDK `ListToolsResult`, sorted by ordinal tool name, UTF-8 |
| Normalization | Remove only per-tool `_meta`, which contains volatile runtime identity; retain names, descriptions, schemas and annotations |

| Configuration | Tools | Descriptor bytes | Result |
|---|---:|---:|---|
| Default `query,create,view,meta` | 50 | 35,483 | PASS |
| `--toolsets all` | 237 | 158,941 | PASS |
| `--toolsets all --read-only` | 98 | 58,037 | PASS |

These are canonical descriptor bytes, excluding the JSON-RPC envelope and runtime `_meta`. They are not actual model context sizes. No tokenizer, LLM, native tool call or spill size policy was exercised. Per-tool duration/result-size/token/size-policy measurements are NOT_RUN in this record.

Reproduce from the pinned revision with the .NET SDK and restored test dependencies:

```powershell
dotnet test tests/RvtMcp.Tests/RvtMcp.Tests.csproj -c Release --no-restore -p:RvtMcpSkipDeploy=true --filter FullyQualifiedName~ToolSurfaceBudgetTests.Full_surface_reports_protocol_bytes --logger "console;verbosity=detailed"
```

Implementation: [ToolSurfaceBudgetTests](../../tests/RvtMcp.Tests/Server/Tools/ToolSurfaceBudgetTests.cs). Three cases passed. The clean-source run was performed specifically to publish this measurement; unrelated passing suites were not repeated. Full/adaptive descriptor counts remain 237/240; this table does not measure adaptive mode.

This scoped record does not close the release requirements for a live per-tool benchmark, every-tool acceptance, a weak-model benchmark or green CI. Those remain pending. Lazy mode is excluded from the candidate and from this measurement.
