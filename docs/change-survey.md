# Change-impact survey

The development tool `revit_survey_change_impact` belongs to `query` and is available with default toolsets, `--read-only`, and send-code disabled. It reads explicit targets from the active document. It does not edit the model, store a persistent snapshot, or publish a history receipt. Matching plugin/server builds are required; previously packaged Setup/MCPB candidates do not contain this tool.

```json
{
  "elementIds": [12345],
  "scopeThreshold": 20,
  "changeKind": "instance",
  "depth": 1
}
```

The ID and threshold above are examples. Agree a threshold for the actual request; there is no global numeric default. `type` means editing a type definition. Assigning another type to particular instances is an `instance` change. An unknown change kind always requires discussion.

## Inputs

| Input | Default / limits |
| --- | --- |
| `elementIds` | Required, 1–25 active-document IDs; duplicates deduplicated |
| `scopeThreshold` | Required, 1–200000 |
| `changeKind` | `unknown`; `instance`, `type`, or `unknown` |
| `depth` | 1; only 1 or 2, with expansion limited to type/group/join/physical connectors |
| `phaseId` | Optional; otherwise a valid active-view phase. ID 0 is allowed; no last-phase fallback |
| `maxViews` | **0**, so view/schedule iteration is not requested. Opt in with 1–100 |
| `viewScope` / `viewIds` | `sheets` or `all`; optional list of up to 100 view IDs within that scope |
| `proximityPaddingMm` | 100 mm; 0–10000 |
| `maxIdsPerRelation` | 100; 1–200 returned IDs/evidence items per group |
| `budgetMs` | 10000; 1–30000, soft limit between native API calls |
| `maxScannedElements` | 50000; 1–200000 managed scan iterations, not distinct elements or hidden native work |
| `maxGraphNodes` | 10000; 1–20000 discovered nodes per relation and propagation scope |

Setting `viewIds` alone does not enable view scanning. With `maxViews=0`, presentation still returns owner-view evidence where applicable, and marks the skipped iteration with `view_scan_not_requested_set_maxViews`. A native view call can regenerate graphics and exceed the time target before control returns. The tool reports `nativeCallPreemption=false`; it cannot enforce a hard wall-clock deadline. Do not automatically repeat a timed-out call.

## Reading the result

The response always has ten relation groups: type, host, join, connector, group, spatial, annotations, presentation, datum and proximity. Each includes bounded IDs/evidence and individual checks with completeness, reasons, elapsed time and work units.

- `found`: at least one relationship was observed. Partial coverage means a lower bound.
- `none`: all declared checks completed and found zero relationships.
- `not_checked`: no relationships were found but coverage is incomplete; `count=null` and `countKind=unknown`.
- `idsTruncated` / `evidenceTruncated`: preview limits. These do not reduce an independently known exact count. A byte limit may trim previews further while preserving counts/checks.

Missing targets are retained and degrade every group's coverage. Per-type metadata distinguishes completed exact counts from unfinished lower bounds. Type counts verify `GetTypeId`, avoiding centerline overcounts. A family type shared by several requested targets is scanned once; other types retain a bounded general scan.

`scope.count` is a union/lower bound of targets and discovered type/group/join/physical-connector propagation candidates. It excludes tags, views, rooms and proximity and does not predict which elements a future edit will change. `overThreshold` is true when even a lower bound exceeds the threshold, false only for a complete scope within it, and otherwise null. `requiresDiscussion` also covers type/group/datum changes, unknown kind, missing targets and incomplete checks. No flag authorizes a write.

## Coverage limits

Targets and discovered IDs are local to the active document. Linked contents/phase mapping, many hosted classes, cuts/coping/reverse attachments, group exclusions/variation, area/full-volume overlap and reverse grid/constraint coverage remain incomplete. Logical MEP systems are not physical connector traversal. View/schedule results are candidates, not proven visible pixels or displayed rows. Proximity is bounding-box evidence, not clash analysis.

The tool does not assess engineering calculations, cost, construction programmes or design intent. Inspect the returned blind spots and check reasons for the actual request; never treat incomplete evidence as a safe-change certificate.

Parameter observations cover selected built-ins, with raw/storage/display information and truncation markers. `snapshotUse=observation_only_not_trusted_history_baseline` means they are not trusted history before-values or a reusable snapshot token. Re-read proposed properties before writing, especially after manual edits, Undo, document switches, reopening or Save As. Snapshot-to-history provenance/freshness is not implemented by this tool.

Use the `revit_change` prompt to survey, discuss limitations, confirm a concrete proposal, make the smallest agreed change, read back and record the user's reason. That prompt is guidance, not a server-enforced workflow lock.
