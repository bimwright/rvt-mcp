# Issues #11–#14 — local acceptance and publication handoff

Updated 2026-09-22. Repository: `rvt-mcp`, branch `master`.

**Publication remains on hold.** The owner authorized local fixes, tests, separate commits and prepared responses. Do not push, comment on GitHub, close issues, release or publish packages until explicitly instructed. Completion of these issues is not authorization to publish.

## Local commits and response material

| Work | Local commit | Evidence / response |
|---|---|---|
| #11: MEP membership and connector domains; build/dependency hardening | `5944fcfa6a206a5fc147582f02886be2cee27c41` | [Handoff](issue-11/HANDOFF.md), [unpublished response](issue-11/RESPONSE-DRAFT.md) |
| #12: inherit pipe systems; reject incompatible MEP connections | `1127a2b6cfbf6dc97c56aea8040fe5a2f5e41376` | [Handoff](issue-12/HANDOFF.md), [unpublished response](issue-12/RESPONSE-DRAFT.md) |
| Toast crash discovered during #13 final acceptance | `05a9429` | [Diagnosis and WPF regression](toast-positioning.md) |
| #13: explicit family hosts and verified placement | `849e9992666310d8955c2dd6ba01dfb06083a729` | [Handoff](issue-13/HANDOFF.md), [unpublished response](issue-13/RESPONSE-DRAFT.md), [final typed-server evidence](issue-13/evidence/typed-server-final.json) |
| #14: earlier stairs work, preserved in this session | `087b3bc` (workflow), `8133a30` (reporter payload handoff) | [Existing evidence](issue-14/README.md), [stairs workflow](../stairs-workflow.md) |

The README/changelog update is a separate documentation commit after #13. Resolve it with `git log -1 --format="%H %s" -- docs/testing/2026-09-22-issue-handoff.md`.

## Verification boundaries

- Latest xUnit suite: **549 passed, 0 failed, 0 skipped**. Real WPF executable: **2 checks pass**, following an exact pre-fix reproduction of the `Top = NaN` animation exception.
- Latest Release builds and deployment: **Revit 2022 / 2024 / 2027, zero warnings/errors**, performed with Revit closed and verified by matching DLL hashes.
- #11's recorded live 2027 checks include 148 HVAC systems, domain filtering, isolated pipe/duct fixtures and SQLite loading. #12's recorded installed-plugin suite passes 17 cases. Those handler implementations were unchanged by #13; their prior evidence remains attached to their commits, rather than being presented as fresh reruns on the final DLL.
- #13's final restarted-host suite passes **16/16** again after the toast fix. A fresh stdio server exposes 229 tools, accepts `host_id`, rejects missing host, and creates a door whose host/location match independent Revit API reads. Toast remains enabled and the host survives expiry. All 16 fixtures roll back; the typed test's disposable project closes without saving its door. The original HVAC sample is active again with `IsModified=false`.
- Live host: **Revit 2027 build 27.0.10.13**. Revit 2022/2024 have build/deployment coverage only. The reporters' Revit 2025/2026 environments and exact family/locale combinations were not live-tested.
- #14 evidence and limitations are unchanged; this session did not rerun or expand its stair acceptance.

## Runtime/client handoff

Latest installed 2027 plugin SHA-256: `3889F5AB7BCFE734BAD067FCB071BED30E8B293F43D17A9281B48A60341B0B1D`; loaded MVID: `94493180-72b2-434e-a2fb-472b20053112`. See [all three deployment hashes](issue-13/evidence/post-toast-deployed-hashes.json).

The fresh acceptance server is a local framework-dependent publish at `publish/server-issues-11-13/RvtMcp.Server.exe`, with DLL SHA-256 `1C9A5E4D61B1346A3AF1780101B861B50814D78CE43608E39FA61666268F4F7D`. The test owns and shuts down only that subprocess. Existing MCP client processes and client configuration were not changed; their sessions may still expose the old schema. Rollout to those clients requires selecting an updated server build and restarting the MCP connection, preserving existing recorder wrappers and other settings. Do not overwrite/kill their locked executables blindly.

The current server version string remains `0.6.1`; it alone cannot identify the source build. Use hashes and the observed tool schema. No new ZIP, package or release was published.

## Resume safely

Read Git status first and preserve unrelated `tests/RvtMcp.Tests/TestResults/`. Full logs, the temporary Revit fixture files and local publish outputs remain ignored under `artifacts/` and `publish/`; compact evidence is committed. The response drafts are ready for review but must retain their local/unreleased wording until publication is actually authorized and completed.

See [placement/MEP contracts](../placement-and-mep-contracts.md) for user-facing behavior, parameter selection and limitations. Re-enumerate Revit targets before any later live test; recorded process IDs and open documents are not persistent guarantees.
