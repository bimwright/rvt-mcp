# Issues #11–#14 — local acceptance and publication handoff

Updated 2026-09-22. Repository: `rvt-mcp`, branch `master`.

**Publication remains on hold.** The owner authorized local fixes, tests, separate commits and prepared responses. Do not push, comment on GitHub, close issues, release or publish packages until explicitly instructed. Completion of these issues is not authorization to publish.

## Latest review follow-up (#12)

Installer follow-up: the owner subsequently authorized fixing upgrade safety and building the v0.6.2 ZIP. See [installer/package verification](installer-upgrade/README.md): the local package passes checksum, stdio smoke and isolated v0.6.1 upgrade checks. This is separate from the Revit runtime acceptance below; no publication has occurred.

The owner authorized the two review corrections and immediate testing on a dedicated Revit 2027 model. See [the full follow-up record](issue-12/REVIEW-FOLLOWUP.md) and [updated unpublished response](issue-12/RESPONSE-DRAFT.md).

- The connection handler recognizes a direct connection or one shared pipe/duct fitting. Real Revit tests reproduced incorrect repeat-call mutation of the opposite free ends; the fixed handler now returns a no-op. The public pipe-selector documentation uses the correct camelCase arguments.
- **564 automated tests pass**. All 2022–2027 builds pass; 2026 retains three existing Rebar warnings. Only 2027 was deployed, with Revit closed.
- Fresh installed DLL: **17/17 existing #12 cases + 4/4 real-fitting cases pass**. A fresh 229-tool stdio server verifies public schema, ambiguous-start retry, inherited diameter/system, explicit system selection and mismatch refusal. All typed fixtures were discarded; the dedicated `Review-MEP-2027.rvt` is unmodified with its original 10,213 element IDs.
- Eighteen raw `ConnectTo` probes created no fittings. Automatic fitting insertion is tested with API doubles, not claimed as live-reproduced. Real elbows separately establish the fitting-mediated graph and no-op behavior.
- Current 2027 plugin SHA-256: `9838B26B4230AA00F55C69D462BDF8E46E24AF6FCE5538C6ADC2C9069753F336`; MVID: `c6d2bd26-72eb-4145-856a-8f85a08845be`. Test server: `publish/server-issue12-review/RvtMcp.Server.exe`; DLL SHA-256: `2458A28A6EA994DA35AF8405E6750CC3C6315C60F23CB6EB21FBFCEAB9FC4313`.
- #11, #13 and #14 code and their recorded live acceptance remain unchanged. They were not rerun on this latest DLL; use their linked evidence with its original provenance. Reporter Revit 2025/2026 acceptance is still outstanding. Existing client server processes/configuration remain untouched.

The follow-up's tested source/build/log hashes are retained in [review-verification.json](issue-12/evidence/review-verification.json); do not identify these working-tree changes solely by the previous commit IDs below.

## Local commits and response material

| Work | Local commit | Evidence / response |
|---|---|---|
| #11: MEP membership and connector domains; build/dependency hardening | `5944fcfa6a206a5fc147582f02886be2cee27c41` | [Handoff](issue-11/HANDOFF.md), [unpublished response](issue-11/RESPONSE-DRAFT.md) |
| #12: inherit pipe systems; reject incompatible MEP connections | `1127a2b6cfbf6dc97c56aea8040fe5a2f5e41376` | [Handoff](issue-12/HANDOFF.md), [unpublished response](issue-12/RESPONSE-DRAFT.md) |
| Toast crash discovered during #13 final acceptance | `05a9429` | [Diagnosis and WPF regression](toast-positioning.md) |
| #13: explicit family hosts and verified placement | `849e9992666310d8955c2dd6ba01dfb06083a729` | [Handoff](issue-13/HANDOFF.md), [unpublished response](issue-13/RESPONSE-DRAFT.md), [final typed-server evidence](issue-13/evidence/typed-server-final.json) |
| #14: earlier stairs work, preserved in this session | `087b3bc` (workflow), `8133a30` (reporter payload handoff) | [Existing evidence](issue-14/README.md), [stairs workflow](../stairs-workflow.md) |

The original README/changelog update after #13 is commit `0426fb6`. The review follow-up above supersedes its current-binary summary.

## Verification before the review follow-up

- At the preceding checkpoint, the xUnit suite had **549 passed, 0 failed, 0 skipped**. Real WPF executable: **2 checks pass**, following an exact pre-fix reproduction of the `Top = NaN` animation exception.
- Preceding Release builds and deployment: **Revit 2022 / 2024 / 2027, zero warnings/errors**, performed with Revit closed and verified by matching DLL hashes.
- #11's recorded live 2027 checks include 148 HVAC systems, domain filtering, isolated pipe/duct fixtures and SQLite loading. #12's recorded installed-plugin suite passes 17 cases. Those handler implementations were unchanged by #13; their prior evidence remains attached to their commits, rather than being presented as fresh reruns on the final DLL.
- #13's final restarted-host suite passes **16/16** again after the toast fix. A fresh stdio server exposes 229 tools, accepts `host_id`, rejects missing host, and creates a door whose host/location match independent Revit API reads. Toast remains enabled and the host survives expiry. All 16 fixtures roll back; the typed test's disposable project closes without saving its door. The original HVAC sample is active again with `IsModified=false`.
- Live host: **Revit 2027 build 27.0.10.13**. Revit 2022/2024 have build/deployment coverage only. The reporters' Revit 2025/2026 environments and exact family/locale combinations were not live-tested.
- #14 evidence and limitations are unchanged; this session did not rerun or expand its stair acceptance.

## Previous runtime/client checkpoint

Previous installed 2027 plugin SHA-256: `3889F5AB7BCFE734BAD067FCB071BED30E8B293F43D17A9281B48A60341B0B1D`; loaded MVID: `94493180-72b2-434e-a2fb-472b20053112`. See [all three previous deployment hashes](issue-13/evidence/post-toast-deployed-hashes.json); current 2027 identity is above.

The fresh acceptance server is a local framework-dependent publish at `publish/server-issues-11-13/RvtMcp.Server.exe`, with DLL SHA-256 `1C9A5E4D61B1346A3AF1780101B861B50814D78CE43608E39FA61666268F4F7D`. The test owns and shuts down only that subprocess. Existing MCP client processes and client configuration were not changed; their sessions may still expose the old schema. Rollout to those clients requires selecting an updated server build and restarting the MCP connection, preserving existing recorder wrappers and other settings. Do not overwrite/kill their locked executables blindly.

At that previous runtime/client checkpoint the server version string was `0.6.1`; it alone cannot identify the source build. The newly built local setup reports `0.6.2`, as recorded in the installer/package verification above. Use hashes and the observed tool schema. No ZIP, package or release was published.

## Resume safely

Read Git status first and preserve unrelated `tests/RvtMcp.Tests/TestResults/`. Full logs, the temporary Revit fixture files and local publish outputs remain ignored under `artifacts/` and `publish/`; compact evidence is committed. The response drafts are ready for review but must retain their local/unreleased wording until publication is actually authorized and completed.

See [placement/MEP contracts](../placement-and-mep-contracts.md) for user-facing behavior, parameter selection and limitations. Re-enumerate Revit targets before any later live test; recorded process IDs and open documents are not persistent guarantees.
