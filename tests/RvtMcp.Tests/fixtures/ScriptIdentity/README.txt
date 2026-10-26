P01 version-1 identity encoding

Canonical objects sort decoded property names with StringComparer.Ordinal (UTF-16
code-unit order). Arrays retain order; strings are decoded and minimally JSON-escaped,
without Unicode normalization. Object duplicate names, including equivalent escaped
spellings, are errors. Root depth is one; at most 64 nested containers are accepted.

Nonzero numbers render as [-]coefficient + "e" + decimal exponent, with no coefficient
leading/trailing zeros, no exponent plus sign/leading zeros. All zero forms render 0.
Examples: 1, 1.0 and 1e0 become 1e0; 1200 becomes 12e2. No double/decimal rounding occurs.
Compact dispatch JSON keeps the original valid number lexemes and object order;
canonical text is used only for identity. Data retains both strings until dispatch.
Future gateway parsing must pass the original JSON slice, before JToken/double conversion.

UTF-8 hashes do not insert a BOM. Source normalization changes CRLF/CR to LF only.
ScriptIdentity.CodeSha256 is the shared entry point for code identity in script keys,
source-constructed metadata and future journal integration. Dependency contracts reject
duplicate names and direct self-reference; multi-module cycle detection belongs to P05.
Module revision preimage: canonical {identity_version:1,name,code_sha256,requires}.
Manifest preimage: canonical ordered array of {name,hash,requires}.
Script key: script_v1: followed by SHA-256 of canonical
{identity_version:1,script_code_sha256,module_manifest,entrypoint_mode,result_contract}.
Entrypoint modes app/app_data identify the absence/presence of object data; actual data
does not enter the script key. Description and host information do not enter revision
identity. Parameter-schema/source_revision vectors are deferred to P07 after G2.

The checked-in expected bytes/hashes come from an independent Python reference kept
with private P01 evidence, not from the C# implementation under test. They are portable,
synthetic inputs. Runtime entrypoint/type identity remains subject to native P01A.

Specification 1.3 bounds raw data at 1 MiB UTF-8 before parsing, then compact data at
512 KiB before arbitrary-precision exponent normalization. McpTaskResult.Fail defaults
null/empty/whitespace codes to TASK_FAILED. ScriptTaskOutcome remains script-only;
typed read-only not_started receipts must not use it.
