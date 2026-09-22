# Issue #11 response — prepared, not posted

**Owner hold:** Do not post or push until the owner explicitly resumes publication after the other two issues. Before posting, update the availability paragraph and add the actual pushed commit/release link. The text below is the proposed comment, not a sent message.

---

Thanks for the detailed reproduction. We have implemented and locally verified the fix on Revit 2027 (27.0.10.13).

The problem was using `MEPSystem.Elements` as the complete network. That collection can be empty even when the pipes and fittings are present in `PipingNetwork`.

Piping and HVAC now use `PipingNetwork` / `DuctNetwork`, with terminal counts reported separately. Inventory includes network members, terminals and base equipment without duplicate IDs. Failed membership reads return an error rather than an empty-system result. Open connector counts are restricted to the system's domain, so an electrical connector on equipment is not reported as a piping/HVAC opening.

On the final installed plugin, we reproduced two pipes joined by an elbow plus an isolated third pipe:

| Check | Result |
|---|---|
| Raw `Elements` | Empty |
| Raw `PipingNetwork` | The two connected pipes and elbow |
| `list_mep_systems` / `analyze_mep_network` | `element_count=3`, `terminal_count=0` |
| `analyze_mep_network` | `open_connector_count=2` |
| `get_system_inventory` | Exactly those three elements; isolated pipe excluded |
| Empty/delete recommendation | Not returned |

The equivalent duct fixture passed too. We compared all 148 systems in the HVAC sample against raw Revit API data on the final installed plugin, including mixed-domain equipment. Earlier live runs also covered the plumbing and electrical samples. All temporary fixtures were rolled back.

The automated suite passed 510 tests. Revit 2022, 2024 and 2027 Release builds completed with zero warnings/errors. **Live verification was performed on Revit 2027; we have not yet tested the reporter's Revit 2026 / pt-BR environment.**

The fix is currently committed locally and has not been pushed or released. Once it is available, we would appreciate verification of the same fixture on Revit 2026.
