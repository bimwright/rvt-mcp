# Issue #13 — explicit family hosts and verified point placement

Issue: https://github.com/bimwright/rvt-mcp/issues/13

Implementation baseline: `1127a2b6cfbf6dc97c56aea8040fe5a2f5e41376` (issue #12). The owner authorized a separate #13 commit including this handoff, tests and evidence. Resolve it with `git log -1 --format=%H -- src/shared/Handlers/CreatePointBasedElementHandler.cs`. A separately discovered toast crash was fixed first in `05a9429`. Publication hold remains: no push, GitHub response, release or closure without the owner's instruction.

## Contract and implementation

`revit_create_point_based_element` now accepts optional `host_id` on both the typed MCP surface and the plugin wire schema. Existing parameters and response fields remain.

- `OneLevelBased`: use the level overload. Ignore a supplied host ID with a warning.
- `OneLevelBasedHosted`: require a resolvable host in the active document and use the host + level overload. Conventional hosted doors/windows require a Wall. Revit validates other family/host combinations; actual host ID is checked after creation.
- `WorkPlaneBased`: reject even when a wall ID is supplied. This tool has no face reference/work-plane orientation contract.
- Other placement types: reject explicitly. Failure to read metadata also fails; it cannot fall through to non-hosted placement.
- No automatic wall/host search. Missing host fails before opening a transaction, allowing the agent to ask the user for the intended host.
- Require finite coordinates. An explicitly named missing level fails instead of silently using another level; omitting level retains the lowest-level default.
- Activate/regenerate, create, regenerate, verify actual host and `LocationPoint` within **1 mm Euclidean distance** of requested model coordinates, then check transaction commit status. API failures, host mismatch or position mismatch roll back. Results add `placement_type`, actual `host_id`, `location_mm`, and `warning`.

Scope is the point-placement tool. The lighting/air-terminal tools and family-list metadata are unchanged. No face-placement API or automatic repair of family constraints was added.

## Evidence and acceptance

Original installed-handler reproduction on Revit 2027: two identical calls for a copied `Door-Passage-Single-Flush` returned success with `Host=null` and position `(0, 6.35, 0)` mm. The same symbol/input with the explicit-host overload yielded the intended wall and `(2500, 2000, 0)` mm. This establishes the overload/host issue independently of the proposed fix. The sample is the imperial counterpart, not the reporter's exact metric type; the reporter used Revit 2025.

| Layer | Result |
|---|---|
| Production handler with API doubles | 26 cases pass, including metadata failure, all unsupported placement modes, pre-transaction validation, correct overloads, wrong/null host, wrong/missing location, API partial-mutation exception and rejected commit. |
| Full automated suite | 549 passed, 0 failed, 0 skipped. |
| Release builds 2022 / 2024 / 2027 | All pass, 0 warnings and 0 errors; `RvtMcpSkipDeploy=true`. |
| Candidate source live in Revit 2027 | 16/16 pass on Snowdon Towers Sample HVAC, including temporary doors/windows/furniture copied from the architectural link. |
| Deployment 2022 / 2024 / 2027 | Completed after the owner closed Revit; deployed plugin SHA-256 values match all three build outputs. |
| Restarted installed-plugin acceptance | **16/16 passed** on Snowdon Towers Sample HVAC, Revit 2027 build `27.0.10.13`. Loaded plugin SHA-256 matches deployment; all fixtures rolled back and `IsModified=false`. |
| Final acceptance after toast fix | **16/16 passed again** on the final installed DLL. Real stdio MCP server exposes optional `host_id`; missing-host rejection and hosted-door creation both pass through the typed tool. Toast remains enabled, the same Revit process survives notification expiry, and scratch-project cleanup restores the unmodified HVAC sample. |

The 16 live cases cover missing host, correct door/window host and coordinates, furniture with/without ignored host, missing host element, wrong host kind, WorkPlaneBased with/without host, ViewBased, unknown level, missing coordinate, door/furniture at an upper level, constrained furniture offset rejection and off-wall projection rollback. Every case rolls back, and the enclosing fixture group removes copied types, walls and levels. Final element IDs and `IsModified=false` are restored. No model was saved.

One initial expected-success test requested a chair 500 mm above its level. Live inspection showed the sample `Chair-Breuer` has a read-only Elevation from Level and Revit places it on the level regardless of input Z; a trial vertical move also did not satisfy the request. The final implementation **does not** retain that move experiment. The acceptance suite separately requires success at the level and clean rejection of the unsupported offset, consistent with the agreed position-verification contract. The duplicate-instance warning during the isolated comparison probe was acknowledged by the owner; the comparison and its instances were rolled back.

## Files and resume

- [baseline.cs](baseline.cs) and [evidence/baseline-final.json](evidence/baseline-final.json): original reproduction and explicit-host control.
- [live-regression.cs](live-regression.cs): candidate/installed harness. `candidate=false` selects installed production handlers, as used for final acceptance.
- [evidence/candidate-final.json](evidence/candidate-final.json): all 16 candidate results.
- [evidence/final-runtime.json](evidence/final-runtime.json) and [evidence/final-live.json](evidence/final-live.json): restarted host identity, loaded DLL hash/MVID and all 16 installed-handler results with rollback evidence.
- [evidence/offset-probe.json](evidence/offset-probe.json): chair elevation constraint observation.
- [evidence/source-hashes.json](evidence/source-hashes.json), [evidence/build-hashes.json](evidence/build-hashes.json): tested working-tree bytes and build binaries before deployment; not a claim about loaded assemblies.

Full logs and diagnostic attempts are ignored under `artifacts/issue-13/`. Preserve unrelated `tests/RvtMcp.Tests/TestResults/` output. Do not repeat earlier issue work or include generated output in commits.

Earlier deployment evidence, before the separate toast fix: [evidence/deployed-hashes.json](evidence/deployed-hashes.json). That 2027 plugin SHA-256 was `DD4154EDEEBFBA58137FBA85027C62B7E15676EB6E0513239727BE8E9602726B`.

The earlier installed-handler acceptance used `send_code_to_revit` to invoke the actual plugin handlers, with independent Revit API reads checking resulting host and location. Loaded MVID: `2df80d30-5154-4d9f-a0d4-2782c8cf37a3`. The door was hosted on the intended test wall at `(2500, 2000, 0)` mm; the window was at `(2500, 2000, 1200)` mm. All 16 assertions and outer fixture rollback passed. No model was saved in that run.

## Final typed-server acceptance

[typed-server-smoke.py](typed-server-smoke.py) launches its own server process from `publish/server-issues-11-13/RvtMcp.Server.exe`, initializes stdio MCP, checks the actual `tools/list` schema, reruns all 16 installed-handler cases, and calls the typed placement tool in an isolated scratch project. Run `python docs/testing/issue-13/typed-server-smoke.py --live` with the Revit 2027 HVAC sample open. The script saves the fixture-only scratch file to ignored artifacts so it can be activated; it closes that project without saving the created door and never saves the original model.

The first typed-server attempt exposed the [separate toast crash](../toast-positioning.md). After its fix, rebuild and deployment with Revit closed, the owner reopened Revit and the full sequence passed. Authoritative latest evidence:

- [typed-server-final.json](evidence/typed-server-final.json): 229-tool real schema, all 16 live results, missing-host rejection, typed creation, independent API oracle and successful cleanup.
- [post-toast-deployed-hashes.json](evidence/post-toast-deployed-hashes.json): all three build/deployment hash matches, with zero-warning/error build logs in ignored artifacts.
- [post-toast-source-hashes.json](evidence/post-toast-source-hashes.json): tested source bytes, including the toast change. Hashes describe working-tree bytes before Git newline normalization.
- Final loaded 2027 plugin SHA-256: `3889F5AB7BCFE734BAD067FCB071BED30E8B293F43D17A9281B48A60341B0B1D`; MVID: `94493180-72b2-434e-a2fb-472b20053112`.
- Published-local server DLL SHA-256: `1C9A5E4D61B1346A3AF1780101B861B50814D78CE43608E39FA61666268F4F7D`. This is a local `dotnet publish` output, not a public release.
- Typed call created door `5641` on wall `5640`, confirmed at `(2500, 2000, 0)` mm. Toast stayed enabled; process `72368` survived notification expiry. Cleanup returned the original HVAC sample with `modified=false`, `scratchClosed=true`.

**Installed-plugin and fresh typed-server acceptance are complete.** Existing client-owned server processes/configuration were not replaced: they use `src/server/bin/Debug/net8.0/` and `publish/server-kei-fix/`. Do not assume those sessions have the new schema or kill them blindly. A future client rollout must update its executable and restart its MCP connection. Revit 2025 (reporter's version) has not been live-tested. Keep [RESPONSE-DRAFT.md](RESPONSE-DRAFT.md) unpublished until authorized.
