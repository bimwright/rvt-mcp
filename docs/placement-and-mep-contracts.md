# Family placement and MEP behavior (v0.6.2)

These changes address issues #11–#13 and shipped in **v0.6.2**. Update both the server and the matching Revit plugin together; close Revit before installing, then restart Revit and the client's MCP server connection so the client sees the new schemas. Tool counts remain 40 by default, 229 with `--toolsets all`, and 232 with adaptive bake.

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

With `systemTypeId` omitted, `revit_create_pipe` searches for open physical piping End connectors within 1 mm of its start point:

- **One match:** use Revit's connector-based overload. The new pipe inherits system type and diameter and is connected during creation. A separate connect call is unnecessary.
- **Several matches:** return `created: false`, `reason: ambiguous_start_connector`, and candidate element/connector IDs. Resubmit with `startElementId` and, if needed, `startConnectorId` (`Connector.Id`, not an ordinal).
- **No match:** retain the first available `PipingSystemType` fallback and report the selected type. An explicit connector selection that does not match fails instead of falling back.

A requested diameter differing from the connector by more than 0.01 mm fails before mutation. Omit it to inherit or model an explicit transition. Providing `systemTypeId` creates an independent pipe and cannot be combined with start-connector selection. Successful responses identify the actual type ID/name, diameter, `system_type_source` (`connector`, `explicit`, or `default`), and connection information.

These are public MCP argument names. The internal plugin command uses `system_type_id`, `start_element_id`, and `start_connector_id`; do not send those names to `revit_create_pipe`. Response fields remain snake_case.

`revit_connect_mep_elements` still uses `ConnectTo`. It rejects different assigned piping/HVAC system type IDs with `connected: false`, `reason: system_type_mismatch`, and both types. This applies to the selected ports, including explicit selections. Separate systems with the same type and unassigned equipment ports are allowed; unreadable system metadata fails explicitly. Physical connector domains must match. A successful connection does not promise that Revit merges system instances or fills a geometric gap with new pipe/duct geometry.

Connection verification accepts a direct connection or two distinct physical ports on one shared pipe/duct fitting. Repeating either connection is a no-op (`already_connected: true`), including with explicit connector selection. It does not infer connectivity through arbitrary equipment or a longer network path.

## Interpret MEP membership and connector counts

`revit_list_mep_systems`, `revit_analyze_mep_network`, and `revit_get_system_inventory` share membership reads:

- Piping uses `PipingNetwork`; HVAC uses `DuctNetwork`; electrical uses `Elements`.
- `element_count` reflects network membership. Terminals and base equipment are also inspected; inventory deduplicates their union by element ID. Do not add the counts blindly: network collections can already include equipment/terminals.
- An empty/delete recommendation requires no network members, no terminals, and no base equipment. Failed membership reads are errors, not empty systems.
- Open-connector analysis counts physical End connectors in the system's domain. Electrical ports on HVAC equipment do not inflate the HVAC count.

## Evidence and limits

Revit 2022/2024/2027 builds passed at the v0.6.2 acceptance checkpoint; live acceptance was on Revit 2027. The reporters' Revit 2025/2026 environments were not tested. Build coverage does not establish runtime acceptance on those versions. See the [automated test guide](../tests/RvtMcp.Tests/README.md) for reproducible unit coverage.

## Moving and rotating existing elements (unreleased)

Use `revit_move_elements` and `revit_rotate_elements` from the `modify` toolset
to adjust existing instances without `send_code_to_revit` or ToolBaker.
Read the intended elements' current locations and confirm the active target
and document before editing.

Translation uses explicit model-coordinate deltas in millimetres:

```json
{"elementIds":[12345],"dx_mm":100,"dy_mm":-50,"dz_mm":0}
```

Rotation uses degrees and a vertical Z axis through an explicit model-space
pivot in millimetres. Positive angles turn counterclockwise when viewed
from above. For example, rotate around the point `(1200, 3400, 0)`:

```json
{"elementIds":[12345],"angle_degrees":90,"pivot_x_mm":1200,"pivot_y_mm":3400,"pivot_z_mm":0}
```

All coordinates are required, including zero components. IDs must be positive
integers; the raw selection is limited to 1000 IDs and duplicates are removed.
Missing elements, types, pinned elements and individual group members are
rejected before starting a transaction. The initial implementation supports
point locations and bounded curve locations; other selections are refused.
This is not a host-reassignment or constraint-editing tool.

Both tools return actual `before` and `after` records with element IDs,
locations, curve endpoints where applicable, point rotation in degrees and
available bounding boxes. Revit's actual locations are checked after
regeneration and before commit (1 mm position and 1e-6 radian angle tolerance).
An exception, missing resulting location or constrained transform rolls back
the whole selected set. Revit Undo restores a committed operation. Within
`batch_execute`, the existing `continueOnError` and transaction-group policy
still apply.

These edits are relative and non-idempotent. Inspect the current model before
retrying a call whose outcome is unknown; a repeated successful call applies
the displacement or angle again.

### Transform verification

`ElementTransformTests` compiles the production handlers against API doubles
to test validation, explicit pivots, negative/wrapped angles, curve endpoints,
partial selection failure and transaction rollback. Catalog and wrapper
snapshots cover the public protocol. These checks cannot establish real Revit
constraint, failure-dialog or Undo behavior.

Before accepting the change, use a disposable model in a supported Revit year:

1. Move a free point-based instance by an explicit XYZ delta and verify its
   actual coordinates; Undo once and verify the original location.
2. Rotate an instance by +90 and -90 degrees about an offset pivot; verify both
   position and orientation, then Undo.
3. Repeat with a bounded curve-based element.
4. Select a pinned or missing element alongside a valid one; verify that
   neither moves. Test a constrained translation and rotation as well.
5. Run a move followed by a failing transform in `batch_execute` with
   `continueOnError=false`; verify outer group rollback.
6. Verify read-only mode blocks both commands and the normal target/document
   confirmation and native failure-policy prompts still apply.
