# Installer upgrade verification — 2026-09-22

## Update 2026-09-24 — Revit-side guarantees (unreleased, supersedes the client-wiring notes below)

The installer no longer reads or writes MCP client configs; agents or users connect clients themselves (`AGENTS.md` Step 3). The installer now guarantees the Revit side:

- **Detection.** A Revit year counts only when a `Revit.exe` exists, found via `InstallationLocation` or the default Program Files folder. On the dev machine, the leftover 2023/2025 registry keys are no longer detected.
- **Package validation.** Each plugin ZIP manifest must carry RvtMcp's fixed AddInId for its year and `<Assembly>RvtMcp\RvtMcp.Plugin.dll</Assembly>`. All six v0.6.2 ZIPs pass.
- **Duplicates and legacy copies.**
  - Per-user manifests with the same AddInId — Bimwright-era copies — are moved into the rollback transaction, together with assembly folders only they use.
  - A machine-wide copy under `%ProgramData%` blocks the install before any change.
- **Fixed server path.** `%LOCALAPPDATA%\RvtMcp\rvt\server\current\rvt-mcp.exe` for every version.
  - A previous copy that a running MCP client still uses is kept whole, detected by trying to delete its exe first, and swept at the next install.
  - Legacy versioned folders are listed and removed with `-PruneOldServers`.
- **Server check.** The installed server is unblocked (Mark-of-the-Web) and started once with `--help`. Failure rolls back add-ins and server.
- **Verification.** Installed add-ins are compared byte for byte with the package. Exactly one manifest per year may carry RvtMcp's AddInId.
- **Uninstall.** `install.ps1 -Uninstall` covers 2022–2027 without detection, legacy copies included. `uninstall-all.ps1` never half-deletes a running server copy.

**Automated evidence (2026-09-24):**

- `tests/installer/upgrade.Tests.ps1`: 25/25 on Windows PowerShell 5.1 and on PowerShell 7.
- `tests/installer/uninstall.Tests.ps1`: 11/11 on Windows PowerShell 5.1 and on PowerShell 7.
- In-use behaviour is tested with a real running process (a copy of `PING.EXE`).
- `tests/installer/upgrade-package.ps1`, from the real v0.6.2 payload to a package built from this code: all six years match the package; the **real** `rvt-mcp.exe --help` smoke check passed; the legacy `0.6.2` folder was kept.

Sections below describe the v0.6.2 release and remain as its historical record.

## v0.6.2 record

Status: local installer fixes complete and v0.6.2 setup ZIP built and verified; not published. Server project, MCP handshake and server registry metadata now agree on 0.6.2. This record does not replace the earlier live Revit issue acceptance.

## Initial working-tree v0.6.2 package (historical)

- Artifact: `build/client-setup/v0.6.2/RvtMcp.Setup-v0.6.2-win-x64.zip`, **89,662,949 bytes**.
- SHA-256: `441274D30140A6BE48E797C06A20C1442E4CB9CC816769E5D4536C1DA160F501`; a `.zip.sha256` sidecar accompanies the ZIP.
- [Package checks and executable smoke](package-0.6.2.json): outer/inner ZIP CRCs, all 15 manifest file hashes, six plugin payloads and native SQLite assets pass. Executing the extracted self-contained server returns version 0.6.2 and 40 default / 229 all-toolset tools. Hosted-family and pipe-selector schemas match the fixes.
- [Actual-payload upgrade](upgrade-0.6.1-to-0.6.2.json): seed an isolated installation from the real v0.6.1 payload, then run the new ZIP's installer control flow against all six years. Every installed payload file matches v0.6.2. Old server files, read-only/toolset arguments, environment, disabled state, other config entries and the config backup are preserved. Process discovery and destination/config paths are redirected; this is not deployment to a real Revit installation.
- The extracted installer also passes its real `-WhatIf -Years 2027 -Client none` entrypoint. The extracted server successfully reads the active Revit 2027 view; the installed 2027 plugin DLL is byte-identical to the package. No model mutation or real plugin replacement occurred.
- **564/564 xUnit tests pass**, none skipped. Packaging builds all six years with deployment disabled. [Build provenance](build-0.6.2.json) records test/build hashes and changed source inputs.

This initial ZIP was built from the working tree, including uncommitted fixes. Its manifest's `commit` is the base HEAD (`0426fb6`), not a claim that this commit alone contains the package sources. The hashes and JSON reports in this section identify that initial candidate only.

The owner has authorized committing these changes, rebuilding from that exact commit and verifying the resulting setup. Build the replacement in a clean detached worktree with deployment disabled, using a new output directory named `build/client-setup/v0.6.2-<commit>/`. Keep its checksum, build/test logs and package/upgrade reports together there; require the ZIP manifest's `commit` to match the checkout. Do not overwrite these historical reports or label them as verification of the rebuilt binaries. No push, release or NuGet publish has occurred.

## Reproduction and change

The original production config functions failed all four client preservation tests: Codex, Claude, OpenCode and Kilo discarded `--read-only`. The Codex replacement also consumed following non-MCP TOML sections. See [before-fix.json](before-fix.json).

The installer now updates only the managed executable path, preserving arguments, environment, timeouts, enabled/disabled state and unrelated entries. Legacy per-year aliases are retained for manual cleanup. Unrecognized launchers, remote entries or unsupported TOML layouts fail with manual-wiring guidance instead of overwriting custom configuration. TOML multiline strings deliberately require manual wiring; there is no general TOML parser in the setup ZIP.

All selected plugin archives and manifest checksums are validated before replacement. Payload extraction/copy completes in a staging directory first. Running Revit blocks apply, with another check after staging. Old plugin directories, addin manifests and an existing target server version are moved to unique adjacent backups. Config writes remain atomic and receive transaction snapshots. A later error restores earlier changes in reverse order. If one restore fails, other restores still run and the failed backup path is reported and retained.

`-WhatIf` performs validation and previews but creates no staging/config files. Versioned older server directories remain untouched unless `-PruneOldServers` is passed. No full uninstall or personal ToolBaker/cache deletion is part of upgrade.

## Automated evidence

Run from the repository root; no Revit, .NET SDK or Pester dependency:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/installer/upgrade.Tests.ps1
pwsh -NoProfile -File tests/installer/upgrade.Tests.ps1
```

The harness loads production function definitions and the actual install control flow via the PowerShell AST. Only process discovery and destination/config routing are redirected for full-flow tests. It uses real ZIP extraction, file moves, atomic config replacement and an exclusive Windows file lock. Dummy binary payloads are intentionally never executed. Profile environment variables (`USERPROFILE`, `APPDATA`, `LOCALAPPDATA`) are redirected to a per-run sandbox so no test can reach real user data. CI runs both PowerShell shells.

- [Windows PowerShell 5.1](powershell-5.1.json): 19/19 passed.
- [PowerShell 7](powershell-7.json): 19/19 passed.
- [Cross-volume Windows PowerShell run](cross-volume.json): 19/19 passed with staging on C: and test destinations on D:.
- [Existing release compatibility](release-061-validation.json): all manifest checksums and six plugin archive layouts in the local v0.6.1 release ZIP pass. ZIP SHA-256 is `7A5059C2DFFBDF6EA9CB0DB01D4737ED794FE0E618CDB3BE1B5F7A8F1F7E4AFD`. The actual running Revit process is rejected by the production guard. No real installation was modified.

The actual script entrypoint also passed `-WhatIf -Years 2027 -Client none` against that extracted release. This caught a Windows PowerShell 5.1 quirk where `Get-FileHash` provider reads inherited `WhatIf` and returned no hash; package validation now hashes streams directly. The regular WhatIf regression includes a real checksum manifest.

Coverage includes repeated installation without changing backups, disabled/read-only entries, custom wrappers, adjacent/quoted TOML tables, missing/corrupt/incomplete ZIPs, multiple Revit years, same-version server replacement, a later config failure after an earlier config write, locked later-year addin, checksum mismatch, cleanup of newly created files on rollback, and retained recovery backups when rollback itself is blocked.

## Limits and next release gate

Rollback covers caught errors in the install process. It is not a persistent crash-recovery journal: power loss or forcible termination can require restoring adjacent backups manually. Concurrent external edits/launches are not locked for the entire installation; users must keep Revit and MCP sessions closed during upgrade. Empty parent directories and `.rvtmcp.bak` files can remain after rollback.

The initial v0.6.2 package checks above cover actual binaries, an isolated payload upgrade and a live read through that packaged server. A full deployment/restart using the setup on a real Revit client has not been performed. Existing Revit 2027 issue-test evidence remains separately recorded. Publication remains on hold; the authorized next step is the source-commit rebuild and package verification described above.
