# Draft response — issue #12

**Unpublished. Local implementation and Revit 2027 acceptance are complete. Do not post until the owner authorizes publication; update release availability at that time.**

Thanks for the clear reproduction. We reproduced the reported behavior: omitting the system type created a Hydronic Supply pipe, and `connect_mep_elements` joined it to the Sanitary pipe without changing their different system types.

The local fix addresses both steps:

- When `create_pipe` has no explicit system type, it looks for an open piping connector within 1 mm of the start. A unique match uses Revit's connector-based `Pipe.Create` overload, inheriting the connector's system and diameter and connecting immediately. No subsequent connect call is needed for that extension.
- With no matching connector, the existing first-system-type fallback remains, and the response identifies the actual selected type and its source. Multiple matching connectors return candidates for explicit selection. A conflicting requested diameter is rejected rather than silently changed.
- `connect_mep_elements` refuses connections between different assigned piping or HVAC system type IDs and reports both types. Separate systems with the same type and unassigned equipment ports remain allowed. Explicit connector selection uses the same validation.

Validation: 523 automated tests passed; builds for Revit 2022, 2024 and 2027 completed without warnings or errors. On a restarted Revit 2027 host, 17 live cases passed, including Sanitary inheritance, fitting-port inheritance, piping/HVAC mismatch rejection, ambiguous connector selection, diameter conflict and rollback. Temporary test elements were rolled back. Revit 2022/2024 have build coverage; we have not live-tested the reporter's Revit 2026/Portuguese environment.

This is currently a local fix, not a published release. No release availability is claimed here.
