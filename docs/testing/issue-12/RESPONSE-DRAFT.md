# Draft response — issue #12

**Unpublished. Local implementation and Revit 2027 acceptance are complete. Do not post until the owner authorizes publication; update release availability at that time.**

Thanks for the clear reproduction. We reproduced the reported behavior: omitting the system type created a Hydronic Supply pipe, and `connect_mep_elements` joined it to the Sanitary pipe without changing their different system types.

The local fix addresses both steps:

- When `create_pipe` has no explicit system type, it looks for an open piping connector within 1 mm of the start. A unique match uses Revit's connector-based `Pipe.Create` overload, inheriting the connector's system and diameter and connecting immediately. No subsequent connect call is needed for that extension.
- With no matching connector, the existing first-system-type fallback remains, and the response identifies the actual selected type and its source. Multiple matching connectors return candidates for explicit selection. A conflicting requested diameter is rejected rather than silently changed.
- `connect_mep_elements` refuses connections between different assigned piping or HVAC system type IDs and reports both types. Separate systems with the same type and unassigned equipment ports remain allowed. Explicit connector selection uses the same validation.
- Connections through one shared pipe/duct fitting are recognized, including on repeat calls. In a follow-up test, the old handler could connect the opposite free ends of two curves already joined by an elbow; the corrected handler returns `already_connected=true` without changing the model.
- For the public `revit_create_pipe` tool, retry with `startElementId` / `startConnectorId`, or choose an independent pipe type assignment with `systemTypeId`. These public argument names differ from the internal snake_case wire fields.

Latest local validation: 564 automated tests passed; all six Revit 2022–2027 plugin builds passed (2026 retains three existing Rebar deprecation warnings). The restarted Revit 2027 plugin passed all 17 previous cases plus four real pipe/duct fitting regressions. A fresh MCP server also exercised ambiguous selection, retry with the public selectors, explicit system type and mismatch refusal. Independent API reads confirmed the results. Test fixtures were rolled back or discarded by reopening the dedicated test copy; all 10,213 baseline element IDs and the unmodified state were restored. We have not live-tested the reporter's Revit 2026/Portuguese environment.

The API-documented automatic fitting-insertion branch is covered with API doubles; 18 raw Revit 2027 probes produced direct connections without inserting fittings, so we do not claim a live reproduction of that branch. Real fitting-mediated connectivity and repeat-call behavior were tested separately. See [the follow-up verification record](REVIEW-FOLLOWUP.md).

This is currently a local fix, not a published release. No release availability is claimed here.
