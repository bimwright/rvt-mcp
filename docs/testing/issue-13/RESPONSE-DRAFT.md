# Draft response — issue #13

**Unpublished. Local installed-plugin and fresh typed-server acceptance are complete. Do not post or claim release availability until authorized.**

Thanks for the reproducible example. We reproduced the same symptom twice: the point-placement tool reported success, but the door had no host and its actual location was `(0, 6.35, 0)` mm. The handler was using the overload without a host. Providing the wall to the host-aware overload placed the same family correctly.

The local fix adds optional `host_id` and validates the family's placement type:

- Hosted families require an explicit host. Missing or invalid hosts fail without leaving a new instance behind; no wall is chosen automatically.
- Level-based families retain point + level placement. An unnecessary host ID is ignored with a warning.
- Work-plane/face-based and other unsupported placement types return a clear error instead of attempting an unsuitable overload.
- The handler regenerates and verifies the actual host and position before committing. A mismatch beyond 1 mm rolls back. Successful responses include actual `host_id`, `placement_type` and `location_mm`.

Validation: 549 automated tests passed; Revit 2022/2024/2027 builds completed with zero warnings and errors. Sixteen live cases passed on the installed plugin after restarting Revit 2027, including hosted doors/windows, non-hosted furniture, upper-level placement, invalid inputs and rollback. All temporary test elements were rolled back. The live test used the imperial Door-Passage-Single-Flush sample counterpart, not the exact reported metric type; Revit 2025 has not been live-tested.

A fresh stdio MCP server also passed the complete typed `host_id` flow: missing host was rejected, an explicit wall produced a correctly hosted door, and an independent Revit API read confirmed its coordinates. This test used a disposable scratch project that was closed without saving the test instance; the original HVAC sample remained unmodified. A separate toast animation crash found during this step was fixed and the entire sequence passed with notifications enabled.

Adoption requires both the updated server and plugin and a restarted MCP connection. Existing client sessions were not forcibly replaced. Update availability after publication; this draft does not announce a published release.
