# Security Policy

## Supported Versions

Security updates are provided for the latest minor release series only.

| Version | Supported |
|---------|-----------|
| 0.6.x   | ✓         |
| < 0.6   | ✗         |

## Threat Model

bimwright runs on `127.0.0.1` only. The attack surface is:

- Local processes that can read the discovery files (`%LOCALAPPDATA%\RvtMcp\revit-2022.json`..`revit-2027.json`)
- Local processes that can connect to the TCP port or Named Pipe
- Code executed via `send_code_to_revit` or materialized by the ToolBaker engine

## Mitigations in place

### Per-session token authentication
- Each Revit session generates a 32-byte cryptographic random token.
- Token is persisted alongside port/pipe info in the discovery file.
- Every request must include the valid token — otherwise rejected.
- Constant-time string comparison prevents timing attacks.

### Discovery file ACL
- Discovery files are ACL-restricted to the current Windows user (best-effort).
- Disables inheritance, grants `FullControl` only to the current SID.
- Falls back to token-only defense if ACL fails (logged via Debug output).

### Input validation
- `--http` port validated: 1–65535, numeric only.
- `--target` validated: one of `2022`, `2023`, `2024`, `2025`, `2026`, `2027` (4-digit calendar years). Legacy R-codes are rejected.
- Handler parameters validated via `SchemaValidator` before dispatch.
- TCP line size limit: 1 MiB per message.
- Rate limiting: 20 requests per 10 seconds on socket.

### Secret masking
- `SecretMasker` redacts API keys, Bearer tokens, passwords in log output.
- Patterns: `sk-*`, `Bearer *`, `authorization:`, `api_key=`, `password=`.
- `ErrorSanitizer` strips Windows/UNC absolute paths from errors sent to the model — filenames preserved.

### Network binding
- TCP listener: `127.0.0.1` only (not `0.0.0.0`).
- Named Pipe: local machine scope.
- HTTP SSE path: `127.0.0.1` only, middleware rejects non-localhost `Host` headers.
- Any non-localhost plugin bind requires explicit `BIMWRIGHT_ALLOW_LAN_BIND=1` opt-in.

### Dynamic code paths (`send_code_to_revit`, ToolBaker)
- `send_code_to_revit` is on the default `meta` surface (still hidden by `--read-only` or `--disable-toolbaker`) and executes through the same local authenticated MCP channel as the rest of the Revit tools.
- Adaptive bake is separate: it only enables suggestion/logging tools and is not required for `send_code_to_revit`.
- Use `--read-only` or `--disable-toolbaker` when a host profile should not expose dynamic-code execution.
- ToolBaker bakes require user approval per tool + operate under the host Revit process trust boundary. Production hardening, including signed-bake verification, remains tracked as v1.0 hardening work.
- **TTL send_code journal:** When `persistSendCodeBodies` is enabled, raw code bodies (partially redacted for paths and secrets) are stored on the local disk under `%LOCALAPPDATA%\RvtMcp\send-code-journal.jsonl`. This journal has a maximum 2-day TTL and is completely deleted 7 days after expiration or disablement. Secure local environment access is required to prevent unauthorized reading of local journal files.

## Reporting a vulnerability

**Please do not open a public GitHub issue for security-sensitive reports.**

Use one of these private channels:

1. **GitHub private vulnerability report** — go to [the Security tab](https://github.com/bimwright/rvt-mcp/security/advisories/new) and submit a new advisory draft. This is the preferred path.
2. **Email the maintainer** — contact via the address on the commit history.

Include:
- Version (server + plugin) and Revit year.
- Reproduction steps.
- Impact assessment (local vs remote, auth required, user interaction).

Do not publish proof-of-concept exploits in public channels until a fix has shipped.

## Disclosure timeline

- Acknowledgement within 72 hours of report.
- Assessment + fix target within 14 days for high-severity issues (auth bypass, RCE).
- Coordinated disclosure via GitHub Security Advisory with CVE assignment where applicable.

Solo-maintained project — timelines are best-effort, not contractual.
