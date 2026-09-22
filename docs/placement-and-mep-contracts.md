# Family placement and MEP behavior (unreleased)

These changes address issues #11–#13 in the local source tree. They are **not part of the published v0.6.1 ZIP**. Update both the server and the matching Revit plugin when adopting this source build; close Revit before deploying, then restart Revit and the client's MCP server connection so the client sees the new schemas. Tool counts remain 40 by default, 229 with `--toolsets all`, and 232 with adaptive bake.

## Place a door, window, or furniture instance

`revit_create_point_based_element` accepts `typeId`, `x`, `y`, optional `z` (default 0), `level` (name), and optional `host_id`. Coordinates are model coordinates in millimeters. Resolve IDs from the active document; linked-model IDs are not local hosts.

| Family placement type | Behavior |
|---|---|
| `OneLevelBased` | Place by point and level. An unnecessary `host_id` is ignored with a warning. |
| `OneLevelBasedHosted` | Require `host_id`; hosted doors/windows require a local wall. Use the host + level overload. |
| `WorkPlaneBased` | Reject: a host element alone does not identify a face/work plane and orientation. |
| Other types | Reject as unsupported by this tool. |

There is no automatic host search. If a hosted family fails because its host is missing, resolve the intended host from the user's context; ask when that choice is unclear. Family-list tools do not currently expose `FamilyPlacementType`. An explicitly named missing level is an error; omitting the name retains the lowest-level default.

Example arguments after resolving a door type, a wall and a level in the current model (IDs below are illustrative):

```json
{"typeId":12345,"host_id":67890,"x":2500,"y":2000,"z":0,"level":"Level 1"}
```

Before committing, the handler regenerates and checks the actual host and `LocationPoint` against the request, with a 1 mm Euclidean tolerance. A mismatch rolls back. Success retains `elementId`, family/type/category and adds `placement_type`, actual `host_id`, `location_mm`, and `warning`. Some constrained families cannot honor a requested elevation; those placements fail rather than reporting the wrong location as success. Lighting and air-terminal tools retain their own contracts.

## Extend a pipe from an open connector

With `system_type_id` omitted, `revit_create_pipe` searches for open physical piping End connectors within 1 mm of its start point:

- **One match:** use Revit's connector-based overload. The new pipe inherits system type and diameter and is connected during creation. A separate connect call is unnecessary.
- **Several matches:** return `created: false`, `reason: ambiguous_start_connector`, and candidate element/connector IDs. Resubmit with `start_element_id` and, if needed, `start_connector_id` (`Connector.Id`, not an ordinal).
- **No match:** retain the first available `PipingSystemType` fallback and report the selected type. An explicit connector selection that does not match fails instead of falling back.

A requested diameter differing from the connector by more than 0.01 mm fails before mutation. Omit it to inherit or model an explicit transition. Providing `system_type_id` creates an independent pipe and cannot be combined with start-connector selection. Successful responses identify the actual type ID/name, diameter, `system_type_source` (`connector`, `explicit`, or `default`), and connection information.

`revit_connect_mep_elements` still uses `ConnectTo`. It rejects different assigned piping/HVAC system type IDs with `connected: false`, `reason: system_type_mismatch`, and both types. This applies to the selected ports, including explicit selections. Separate systems with the same type and unassigned equipment ports are allowed; unreadable system metadata fails explicitly. Physical connector domains must match. A successful connection does not promise that Revit merges system instances or fills a geometric gap with new pipe/duct geometry.

## Interpret MEP membership and connector counts

`revit_list_mep_systems`, `revit_analyze_mep_network`, and `revit_get_system_inventory` share membership reads:

- Piping uses `PipingNetwork`; HVAC uses `DuctNetwork`; electrical uses `Elements`.
- `element_count` reflects network membership. Terminals and base equipment are also inspected; inventory deduplicates their union by element ID. Do not add the counts blindly: network collections can already include equipment/terminals.
- An empty/delete recommendation requires no network members, no terminals, and no base equipment. Failed membership reads are errors, not empty systems.
- Open-connector analysis counts physical End connectors in the system's domain. Electrical ports on HVAC equipment do not inflate the HVAC count.

## Evidence and limits

The [acceptance index](testing/2026-09-22-issue-handoff.md) links the local commits, response drafts and evidence. Revit 2022/2024/2027 builds pass; live acceptance was on Revit 2027. The reporters' Revit 2025/2026 environments remain untested. These changes do not add tools or announce a release.
