# Issue #11 — local fix and acceptance handoff

Updated: 2026-09-22. Issue: <https://github.com/bimwright/rvt-mcp/issues/11>.
Repository: `rvt-mcp`, branch `master`, implementation baseline `087b3bc`.
The implementation and this handoff are committed together; resolve their commit with:

```powershell
git log -1 --format="%H %s" -- docs/testing/issue-11/HANDOFF.md
```

## Owner's stop condition — do not publish

The owner explicitly requested a local commit and a prepared handoff/reply only. **Do not push, post the reply, close the issue, create a release, or publish packages.** The owner wants to finish two other issues first; their numbers were not specified here. Wait for the owner's later instruction before any of these external actions. Finishing the other issues alone does not authorize automatic publication.

The issue reply is ready in [RESPONSE-DRAFT.md](RESPONSE-DRAFT.md). No response has been posted, and no issue status has been changed. Issue #14 was outside this review and was not reworked.

## Problem and implementation

The report concerns Revit 2026, pt-BR, v0.6.1: two connected pipes A/B plus an elbow and an isolated pipe C. `MEPSystem.Elements` can be empty even though `PipingNetwork` has A/B/elbow. The old list/analyze path reported zero members and suggested deleting the system.

- `MepSystemMembership` reads one snapshot per system: piping uses `PipingNetwork`, HVAC uses `DuctNetwork`, electrical uses `Elements`. Terminals and base equipment are read separately.
- List/analyze expose network `element_count` and separate `terminal_count`. The empty/delete recommendation requires no network members, no terminals and no base equipment.
- Inventory unions network members, terminals and base equipment, deduplicated by element ID. Real network collections can already contain terminals/equipment; counts must not be blindly added together.
- Failed/null membership reads return errors instead of a false zero-member result.
- Analyzer counts only open physical End connectors whose domain matches the system. Live testing exposed electrical connectors on equipment being counted as piping/HVAC openings; the final code fixes this too.
- MCP descriptions and the two golden tool-list snapshots describe the corrected membership semantics.

Changed implementation: `src/shared/Handlers/{MepSystemMembership,MepMembershipPolicy,ListMepSystemsHandler,AnalyzeMepNetworkHandler,GetSystemInventoryHandler}.cs`, plus `src/server/Program.cs`.

## Build warnings and dependency fixes included

The earlier 2027 build's 78 warnings were 76 CA1416 diagnostics plus two NU1903 occurrences for the same vulnerable SQLite dependency, not 78 platform-only warnings.

- Modern plugin shells disable generated assembly metadata. `src/shared/Properties/PlatformSupport.cs` restores the Windows platform annotation; analyzer rules are not suppressed and the cross-platform server is not annotated.
- All six plugin shells and the server pin `SQLitePCLRaw.lib.e_sqlite3` 2.1.13. Its Windows x64 native engine is SQLite 3.53.3, replacing 3.41.2. Existing Microsoft.Data.Sqlite 8.0.11 and managed SQLitePCLRaw bindings are retained.
- Transitive NuGet auditing is enabled for every plugin and the server. The recorded solution audit found no vulnerable packages.
- net48 deployment/package staging includes the existing Memory/Buffers/Vectors/Unsafe dependencies. Deployment and packaging reject a missing native SQLite asset; the native DLL ships at both the plugin root and `runtimes/win-x64/native`.
- No Revit.exe.config change was made. The standalone net48 SQLite probe used probe-only binding redirects; it is not a live Revit 2022/2024 loading test.

## Verification and limits

| Gate | Result |
|---|---|
| Full automated suite on final source | 510 passed, 0 failed, 0 skipped |
| Revit 2022 / 2024 / 2027 Release builds | Each 0 warnings, 0 errors |
| Final deployment to 2022 / 2024 / 2027 | Plugin/dependency hashes verified; deployment performed with Revit closed |
| Final installed 2027 DLL after restart | Loaded hash and module identity match deployment |
| Final HVAC sample | 148/148 systems match independent API membership, inventory and domain-filtered connector counts |
| Mixed-domain regression on final DLL | 148 electrical connector occurrences excluded across 148 HVAC systems; whole-model electrical scan reports 37 distinct openings separately |
| Final installed pipe fixture | Exactly A/B/elbow: 3 network members, 2 piping openings, zero terminals, C excluded, no empty/delete advice |
| Final installed duct fixture | Same 3-member / 2-opening result, isolated duct excluded |
| Fixture rollback | Both runs restore all 10,213 original element IDs including types; created elements absent; IsModified=false |
| Earlier electrical sample | 448 circuits; exact inventories/counts, base-only circuit avoids false delete advice |
| Earlier plumbing sample | 40 piping + 31 HVAC systems; membership matches; discovered connector-domain bug subsequently fixed |
| SQLite inside Revit 2027 | 3.53.3; in-memory create/insert/read and integrity check pass |

Earlier 2023/2025 builds also passed; 2026 had three existing Rebar CS0618 warnings. These were not part of the final requested 2022/2024/2027 build lane, and the handoff does not claim a warning-free 2026 build.

**Live acceptance is Revit 2027 only**, build `27.0.10.13`. Revit 2026 (the reporter's host and locale) is not live-tested. Revit 2022/2024 have build/deployment coverage only. Generic electrical connectivity/healthy recommendations are not an electrical design validation; `is_well_connected` is an existing fallback for electrical systems.

Full sweeps invoked actual installed handler classes through `send_code_to_revit`, using independent API reads as the oracle. Representative calls also traversed the MCP `batch_execute` dispatcher. The client's default toolset did not expose individual typed MEP tools. A temporary source candidate was used during diagnosis, but the final restarted-host tests use the installed DLL, not that candidate.

## Binary and source provenance

Final Revit 2027 plugin SHA-256:
`AD1245D1AFF684441491352955DA863BFD25645AFAA2221515A05BE0741A7BED`

Loaded module MVID: `3f4cccb0-6ab9-4835-8fa0-1ed417d5a943`.
Native SQLite SHA-256:
`B7385D722C83FB52142A00477A726723745916D22A555711EE89834C1111FB2E`

The installed add-in path is `%APPDATA%/Autodesk/Revit/Addins/2027/RvtMcp/`. The last live target was pinned to 2027 and the active document was Snowdon Towers Sample HVAC. Re-enumerate targets on resume; process IDs and open models are not persistent guarantees. The running MCP server was not forcibly restarted or redeployed; the updated server was built in test staging.

Tracked evidence survives a fresh checkout:

- [final-runtime.json](evidence/final-runtime.json): final host identity and result totals.
- [final-hvac-systems.json](evidence/final-hvac-systems.json): all 148 raw member/terminal IDs, actual inventory IDs, connector domains and per-system checks.
- [final-pipe-fixture.json](evidence/final-pipe-fixture.json), [final-duct-fixture.json](evidence/final-duct-fixture.json): actual installed handler outputs and rollback proof.
- [deployed-hashes.json](evidence/deployed-hashes.json): all three deployments.
- [tested-source-hashes.json](evidence/tested-source-hashes.json): source snapshot corresponding to final tests/build/live verification; unchanged at commit preparation. Hashes refer to recorded working-tree bytes, before any Git newline normalization.
- [local-artifact-hashes.json](evidence/local-artifact-hashes.json): hashes of the original local logs/envelopes.

JSON evidence is compact to avoid adding thousands of formatting-only lines. It contains results from Autodesk sample models, no discovery auth tokens. Full local requests, responses, TRX/build/audit logs and diagnostic attempts remain under ignored `artifacts/issue-11/`, especially `live-2027-hvac-final/`, `live-2027-plumbing/` and `hardening/`. These local folders will not appear in a fresh clone. Generated `tests/RvtMcp.Tests/TestResults/` is also deliberately excluded from this commit.

## Resume and eventual response checklist

1. Preserve the publication hold until the owner explicitly resumes it after the other two issues.
2. Check branch, HEAD and dirty files; identify this commit using the command above. Do not overwrite later issue work or assume the installed plugin matches a newer combined source tree.
3. If combined changes touch these paths/dependencies, rerun tests/builds and the affected live cases. Otherwise use the recorded evidence; do not relabel 2027 evidence as 2026.
4. Safe compile while Revit is open: `dotnet build src/plugin-r27/RvtMcp.Plugin.R27.csproj -c Release -p:RvtMcpSkipDeploy=true`. Full suite: `dotnet test tests/RvtMcp.Tests/RvtMcp.Tests.csproj -c Release`. Never use an auto-deploy build against an open Revit host.
5. After the later publication instruction, determine the actual pushed commit/release and edit the draft's availability paragraph accordingly. Local installation is not a published release. Add a verified commit/release link only when one exists remotely.
6. Post the reviewed draft only when instructed. Do not close the issue or promise 2026 compatibility based solely on 2027 live evidence.

Stop point: implementation and requested local acceptance complete; local commit prepared with reply and evidence; publication intentionally held by the owner.
