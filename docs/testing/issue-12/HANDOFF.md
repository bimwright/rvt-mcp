# Issue #12 — pipe system inheritance and connection validation

Issue: https://github.com/bimwright/rvt-mcp/issues/12

Local work based on issue #11 commit `5944fcfa6a206a5fc147582f02886be2cee27c41`. The owner authorized committing the #12 fix, handoff and evidence on 2026-09-22. Identify the containing commit with `git log -1 --format=%H -- docs/testing/issue-12/HANDOFF.md`. Publication remains on hold: no push, GitHub response, release, or issue closure until the owner explicitly resumes it after the remaining issues.

## Implemented behavior

- `create_pipe`, with no `system_type_id`, finds open physical piping End connectors within 1 mm of the start. One match uses Revit's connector overload, inheriting its system and diameter and connecting during creation. A requested diameter that differs by more than 0.01 mm is rejected before mutation.
- Multiple matches return `created: false`, `reason: ambiguous_start_connector`, and owner/connector IDs. Optional `start_element_id` and `start_connector_id` select a candidate; an invalid explicit selection fails instead of falling back.
- No match retains the first available PipingSystemType fallback. Explicit `system_type_id` creates an independent pipe. Results report the actual system type ID/name, diameter, source (`connector`, `explicit`, or `default`), and start connection information.
- `connect_mep_elements` validates the selected physical connectors and their domains. Different assigned piping or HVAC system type IDs return `connected: false` and both type IDs/names before mutation. Different system instances with the same type are allowed.
- Type lookup uses the selected connector's system, with the pipe/duct type parameter as fallback for an unassigned MEPCurve. An unassigned equipment port is allowed; another port on the equipment is not used to infer its type. Unreadable or invalid assigned metadata fails validation.
- Existing direct connections are recognized as a no-op. Creation/connection exceptions roll back their transaction.
- Typed MCP descriptions and snapshots include the changed contract and the new optional selector parameters.

## Verification status — 2026-09-22

| Layer | Result |
|---|---|
| Original installed-plugin reproduction | Confirmed omitted type created Hydronic Supply; ConnectTo joined it to Sanitary while types remained different. Fixture rolled back. |
| Automated tests | 523 passed, 0 failed, 0 skipped; includes 13 tests of the production type-resolution helper using Revit API doubles. |
| Release builds, Revit 2022 / 2024 / 2027 | All passed with 0 warnings and 0 errors. |
| Deployment, 2022 / 2024 / 2027 | Completed with Revit closed; deployed plugin hashes match build outputs. |
| Candidate-source live checks, Revit 2027 | 14 functional cases passed. A separate negative fixture did not force the expected API failure, so its assertion failed; all cases rolled back cleanly. |
| Final installed-plugin live acceptance | **17/17 passed** on Snowdon Towers Sample HVAC, Revit 2027 build `27.0.10.13`; all cases restored element IDs and modified state after rollback. |
| Additional connection probes | Four raw-API/installed-handler probes with perpendicular and differing-diameter pipes agreed: direct connection established, no fitting inserted, rollback clean. These are connectivity observations, not routing/fitting design validation. |

The unsuccessful negative fixture removed elbow routing rules but Revit still connected successfully, including a second direction variant. It was replaced with a deterministic below-minimum-length API rejection, which passed on the installed plugin. Fitting-port inheritance and explicit cross-domain rejection also passed. The minimum-length case proves a rejected API call leaves no elements behind; it does not prove recovery from an API failure after partial element creation.

Live tests use temporary transaction groups and compare all element IDs (including types) and document modified state before/after rollback. No model save is required. Candidate-source results are not acceptance of the deployed binary. Revit 2022/2024 have build/deployment coverage only; the reporter's Revit 2026 environment is not live-tested.

The final run used the installed production handler classes through `send_code_to_revit`, with independent Revit API reads for types, diameter, physical connection and element inventories. It did not exercise newly exposed optional parameters through a restarted typed MCP server. The loaded DLL hash matched the 2027 deployment below; module MVID was `85a9c42e-e1ad-4420-ad78-df73957f2735`. The document started and ended with `IsModified=false`.

Acceptance covered: Sanitary pipe inheritance and automatic connection; inheritance from an open fitting port; refusal of different pipe system types with default and explicit connector selection; allowing separate systems of the same type; idempotent repeated connection; reported default type with no start match; conflicting diameter; ambiguous start and explicit disambiguation; inside/outside the 1 mm search tolerance; invalid selector without fallback; API creation rejection with rollback; Supply Air/Return Air refusal; explicit cross-domain refusal; and an unassigned equipment port.

## Deployed plugin SHA-256

| Revit | SHA-256 |
|---|---|
| 2022 | `482D3E0979A2939F14EC47D754C680A32120B64D920A193B364916D3066865C4` |
| 2024 | `80EE25B4B8CA13497D534F9742A9A3B5FA00D5DE9318A176DB4F9B96AD8D8B95` |
| 2027 | `F9FD042C9C9A6B8D1223A91E640ED430CFB599FF36740303D97CE7DDC59700E4` |

The existing MCP server process has not been restarted or replaced. Updated typed-tool metadata was built in test staging and verified by snapshots; this is separate from plugin deployment.

## Resume

1. Inspect Git status and preserve unrelated generated `tests/RvtMcp.Tests/TestResults/` output.
2. Local acceptance is complete. If later changes affect these handlers, rerun the relevant checks. Enumerate targets and verify the loaded plugin before any rerun; do not assume the recorded process is still running.
3. [live-regression.cs](live-regression.cs) is the 17-case harness. Its `candidate` flag is `false`, so it invokes installed production handlers. [connection-probes.cs](connection-probes.cs) contains the four supplemental observations. Both expect the Autodesk HVAC sample's piping/HVAC types and mechanical equipment.
4. [RESPONSE-DRAFT.md](RESPONSE-DRAFT.md) is an unpublished response draft. Preserve the publication hold and update availability/version information only after an actual release or push.

Compact evidence is preserved under [evidence](evidence): original baseline, final runtime, all 17 final results, supplemental connection probes, deployed/source hashes, and hashes of the local logs. Source hashes were rechecked after final live acceptance and still matched. These files accompany the fix in its local commit. Hashes describe tested working-tree bytes before Git newline normalization.

Full ignored logs remain in `artifacts/issue-12/`: requests, candidate runs, test/TRX logs, per-year build/deploy logs. Never auto-deploy with Revit running; compile with `-p:RvtMcpSkipDeploy=true`.
