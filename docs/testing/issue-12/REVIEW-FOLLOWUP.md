# Issue #12 review follow-up — 2026-09-22

Local follow-up to review of `5c127f8...0426fb6`. No push, issue comment, closure or release was authorized or performed. This record supersedes the current-binary details in the earlier #12 handoff; its historical evidence remains intact.

## Findings and correction

The handler checked only `first.IsConnectedTo(second)`. It now accepts that direct connection or physical connections to two distinct ports of one shared pipe/duct fitting. It checks the same condition when finding an existing connection, validating explicitly selected ports, and verifying `ConnectTo` before commit. Equipment, unrelated fittings, logical references and longer network paths do not qualify. Different assigned system types are still refused before mutation.

The original review identified a possible false rollback when `ConnectTo` inserts a fitting, as permitted by the [Autodesk API contract](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/04ee99c9-f411-aabe-7b87-013a6f9adb1d.htm). The 18 live raw-API probes (pipe, round duct and rectangular duct; straight/perpendicular; gaps 0/1/100 mm) all produced direct connections without a new fitting. **Automatic fitting insertion was not reproduced live.** That branch is exercised by API doubles at the complete production handler seam, including failures after partial mutation.

A related repeat-call defect **was reproduced live** with real pipe and duct elbows created using `NewElbowFitting`. The old installed handler either rejected the explicitly selected connected ports or, with automatic selection, connected the opposite free ends 4,310.5 mm apart and changed the fixture. The fixed handler returns `already_connected=true` for all four cases, with identical connector geometry/open state and element IDs. Fixtures are isolated and rolled back; no test change was saved.

The public `revit_create_pipe` guide now uses `systemTypeId`, `startElementId` and `startConnectorId`. Snake_case names are identified as internal wire fields or response fields. A fresh stdio MCP server verified the schema and performed an ambiguous-start call, explicit selector retry, independent pipe creation with an explicit system type, and mismatch refusal. Independent Revit API reads verified the selected pipe connection, untouched other candidate, inherited Sanitary type and 101.6 mm diameter.

## Verification

| Layer | Result |
|---|---|
| New full-handler regression | Before: 4 failed / 11 passed. After: 15/15 passed. |
| Full automated suite | 564 passed, 0 failed, 0 skipped. |
| Release builds 2022–2027 | All pass with deployment disabled; 3 existing Rebar deprecation warnings in 2026, 0 errors. |
| Deployment | Only 2027 updated, after all documents were confirmed unmodified and Revit exited normally. Built/deployed DLL hashes match. |
| Fresh Revit 2027 process | Build `27.0.10.13`, PID `46856`; production handler assembly MVID `c6d2bd26-72eb-4145-856a-8f85a08845be`. |
| Installed fitting regression | 4/4 pass, real pipe/duct elbows, automatic and explicit selection; no-op and rollback checks pass. |
| Existing #12 installed suite | 17/17 pass again on the new DLL. |
| Raw ConnectTo probes | 18/18 complete and restore the fixture; all are direct connections, with no inserted fitting. |
| Fresh typed MCP | 229 tools; schema, ambiguity/retry, selected connection oracle, explicit type and mismatch refusal pass. |
| Cleanup | Test copy reopened without saving typed fixtures; same 10,213 element IDs, `IsModified=false`. Toast remains enabled and host PID unchanged. |

The dedicated test copy is `artifacts/issue-12-review/Review-MEP-2027.rvt`, created by SaveAs from the unmodified Autodesk HVAC sample. The original sample file was not overwritten. The test model and logs are ignored local artifacts, not redistributable issue attachments.

Final plugin SHA-256: `9838B26B4230AA00F55C69D462BDF8E46E24AF6FCE5538C6ADC2C9069753F336`.

Fresh server: `publish/server-issue12-review/RvtMcp.Server.exe`; server DLL SHA-256: `2458A28A6EA994DA35AF8405E6750CC3C6315C60F23CB6EB21FBFCEAB9FC4313`. Existing client-owned server processes/configurations were not replaced. Version string remains `0.6.1`; use hashes/schema to identify this unreleased build.

## Reproduction and retained evidence

- [fitting-regression.cs](fitting-regression.cs): installed production handler by default; optional source-candidate mode. Compares connector state in owner/port order because Revit ConnectorSet enumeration order is unstable.
- [fitting-probe.cs](fitting-probe.cs): raw-API diagnostic matrix, not a fitting-insertion acceptance test.
- [review-typed-smoke.py](review-typed-smoke.py): fresh stdio server, both installed suites, raw probes, actual typed calls and close/reopen cleanup. Run `python docs/testing/issue-12/review-typed-smoke.py` with the dedicated unmodified test copy active and matching build/deployment.
- [Baseline](evidence/review-fitting-baseline.json), [candidate](evidence/review-fitting-candidate.json), [final installed/typed results](evidence/review-typed-live.json), [deployment hashes](evidence/review-deployed-hashes.json), [source/build/log hashes](evidence/review-verification.json).

An early candidate harness compared unsorted connector enumeration and incorrectly flagged unchanged geometry. The final baseline/candidate/installed runs all use stable owner/port ordering. The initial diagnostic result is retained only in ignored artifacts.

Live acceptance remains **Revit 2027 only**. Reporter Revit 2026/pt-BR remains untested. #11, #13 and #14 implementations were unchanged in this follow-up; their previous live evidence is not presented as a new run on this DLL. The #12 response draft and shared acceptance index link this update. Do not publish or close any issue until separately authorized.
