# Stairs through conversation and send-code

Use this guide when a user wants to create or adapt stairs through `revit_send_code_to_revit`. The current approach is a conversation about design intent followed by an adapted C# payload. A dedicated `create_stairs` tool is deferred. These are instructions for agents and users, not constraints enforced by the MCP server.

There are two separate resources:

1. **Tested examples** provide working API patterns and evidence with explicit limits.
2. **Design questions** establish which geometry and railing choices the user wants before adapting an example.

A working sample is not a default design. Ask only for missing decisions that affect the result; reuse answers, drawings, selected elements, and authority already given in the conversation.

## 1. Choose an example and understand its limits

| Example | Dependencies and behavior | Recorded verification |
|---|---|---|
| [Straight stair](testing/issue-14/create-stair-snowdon-2027.cs) | Revit 2027 Snowdon fixture IDs and title; updated plugin helper; creates one run and an isolated view | Created, retained, exported, and saved in the separate demo model |
| [U stair with landing](testing/issue-14/create-u-stair-snowdon-2027.cs) | Saved Snowdon-based demo, existing level/type IDs; updated plugin helper; two opposite runs and one landing | Created and retained; initial default railing emitted a continuity warning |
| [U-stair presentation](testing/issue-14/present-u-stair-2027.cs) | Requires that U stair's IDs; changes its railing instances, camera, crop, and exports an image | Final railing change committed without warnings; this was an authorized demo choice, not an automatic repair rule |
| [Portable reporter example](testing/issue-14/reporter-u-stair.cs) / [JSON tool arguments](testing/issue-14/reporter-u-stair.tool-arguments.json) | Full source with its own preprocessor; architectural test project; creates two levels, one U stair, and a view at the project origin using project defaults; does not save | Tested on the updated Revit 2027 plugin with `KeepResult=false`; outer group rolled back. The default retained branch and Revit 2025/v0.6.1 runtime were not tested for this exact payload |

See the [verification record and images](testing/issue-14/README.md) and [source forms/failure handling](send-code.md). The built-in `RvtMcp.Plugin.SafeFailuresPreprocessor` and mixed body/helper support are available since v0.6.2. The portable example uses the existing full-class entrypoint and includes its own helper; do not strip its class or imports when submitting it.

When adapting a script, distinguish these parts:

- **Retain the execution safeguards:** transaction and edit-scope ordering, failure messages, checked commit results, exception cleanup, and verification of persisted elements.
- **Resolve design inputs:** existing levels and offsets, position, direction, run layout, dimensions, stair type, landing geometry, and railing arrangement.
- **Remove or explicitly retain demo behavior:** creating test levels, hard-coded IDs/coordinates, switching views, changing railing types, exporting images, or saving files. A request for stairs in a project does not automatically request these extras. An explicit request to run the standalone demo does authorize its described test geometry.

An adapted payload is a new case to verify. It does not inherit the example's live-test result or establish building-code compliance.

## 2. Read the model before asking

Confirm the target Revit instance and active document, especially when multiple versions are running. Read the relevant levels, existing stair and railing types, selected/reference stair, available placement geometry, and editable state. Resolve names to IDs in this document; never reuse fixture IDs in another project.

If the user says "match stair A at location B," inspect A first. Determine which properties come from its type and which are instance/path overrides. Reusing a type alone may not reproduce its layout, rail transitions, or handrail details. Identify what changes at B instead of asking the user to re-enter everything.

Treat inputs as **specified**, **derived**, **delegated**, or **unresolved**. For example, landing elevation may be derived from an agreed riser allocation; an equal split is a choice when several allocations are possible. If the user delegates a choice within limits, make it within those limits and report it.

## 3. Ask the smallest useful set of questions

Start with the unresolved items below, in the user's language. This is a question bank, not a mandatory questionnaire.

| Decision | Example question |
|---|---|
| Purpose and scope | "Is this a test example, a new project stair, or an edit to an existing stair?" |
| Vertical connection | "Which levels should it connect, and are there offsets from either level?" |
| Position and orientation | "Where is the footprint, where does the ascent start, and which way should it turn?" |
| Layout | "Straight, L-shaped, or U-shaped? By 'two floors,' do you mean one connection between two levels or a stair continuing across multiple storeys?" |
| Reference design | "Which existing stair/railing or project types should it match?" |
| Fixed dimensions and flexibility | "Which dimensions are fixed, and which may I propose: width, tread, riser allocation, landing size, or overall footprint?" |

Follow up only where the chosen design needs it:

- **Landing:** "Must it be at half-height or a specific elevation? What size and gap between runs are required?" Do not silently assume a 10+10 split or a centred landing.
- **Railing:** "Which edges need guards or handrails, which are against walls, and which existing type or reference should each use?" Establish direction relative to ascent so left/right is unambiguous.
- **Railing details:** "Should I inherit the reference's placement on treads/stringers, offsets, transitions, extensions, posts, and materials, or are there exceptions?" Do not ask every type parameter if an accepted reference supplies them.
- **Space conflicts:** "If this does not fit, which constraints may change?" Present concrete alternatives and their consequences when no preference was delegated.
- **Acceptance:** "Are there project rules or specific checks this must satisfy?" Do not infer a jurisdiction, compliance standard, or permission to ignore a rail-continuity warning.

Example user prompt:

> Create a U stair between the selected levels in the marked area, matching the selected reference stair. Keep the specified clear width and use the reference's materials and railings. Ask about unresolved layout or railing choices, then adapt a tested send-code example and show the measured result and warnings.

## 4. State the resolved design, then adapt the code

Give a short description before execution, such as:

> L2 to L3, ascent from the south and turning left; two runs with the agreed 10+10 risers, 1,200 mm run width, and a mid-height landing. Use stair type A, railing B on the open edge, and the specified wall handrail on the other side. Create a new stair and an iso view.

Those dimensions are illustrative, not defaults. Distinguish run width from clear width between obstructions. List any outstanding decision instead of filling it with an arbitrary default. Do not request another confirmation if the user already specified or delegated the choices and authorized execution; ask only when the design or permitted scope is still unresolved.

Adapt the payload to that description. In particular:

- Do not select the first available stair/railing type, infer wall-side requirements from proximity alone, or substitute a different railing to eliminate a warning.
- Do not edit shared stair, railing, handrail, or top-rail types without authorization covering their other instances. A requested local variant may require separate types and dependencies; duplicating only the parent type does not prove isolation.
- Preserve existing model elements and railings outside the requested scope. The demo's successful Cable Railing-to-Pipe change is not a production fallback.
- Verify that the chosen API path supports the requested arrangement. Host-based railing creation applies to all sides and expects a host without associated railings; it does not express different types on selected edges. Do not present unsupported per-edge behavior as implemented.
- Keep scope cancellation after the inner transaction is disposed, retain the failure helper instance, and check both commit results and element existence as described in [send-code.md](send-code.md).

If the geometry or API cannot satisfy the resolved design, explain the specific limitation and present alternatives. Do not change design constraints silently just to obtain a committed element.

## 5. Verify and report the actual result

Verify against the agreed design: level IDs/offsets, start and ascent direction, run and landing counts, riser allocation, elevations, actual tread and widths, stair/railing types, railing coverage, and any promised clearance checks. Compare with the model state before execution to detect unintended changes. Check only what can actually be measured; label unperformed checks explicitly.

Report created IDs and distinguish `committed`, `committed_with_warnings`, and `not_committed`. Return warning details even when the preprocessor removed them from Revit failure processing. Apply the warning policy agreed for this task; a warning category alone does not establish that a design defect is acceptable. If a committed result needs a new design choice, report that state and ask about the choice without performing an unrequested repair. Do not rerun creation blindly after a timeout or ambiguous response; inspect whether elements were already created.

Provide an iso view/image when requested or useful for reviewing the layout. A clean image is evidence of appearance, not a substitute for dimensional, continuity, clearance, or compliance checks. State whether the model was saved; save/export only within the requested scope.

## API references and future scope

- [Railing placement on stairs](https://help.autodesk.com/cloudhelp/2022/ENU/RevitLT-ArchDes/files/GUID-3404CC3F-6F33-43EB-B006-8E90D619580C.htm)
- [Railing system type properties](https://help.autodesk.com/cloudhelp/2015/ENU/Revit-Model/files/GUID-6385104B-4295-4659-855F-BA7DD2793CD5.htm)
- [Continuous rail properties](https://help.autodesk.com/cloudhelp/2020/ENU/Revit-Model/files/GUID-B6A3CEF7-E14C-46DA-8F0C-61556908EB99.htm)
- [Host-based Railing.Create](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/1c9de88b-8038-4ac9-37bd-9b6fd5e1f801.htm)

Confirm API availability for the target Revit year when adapting beyond the tested examples. Reconsider a dedicated tool only if repeated real requests establish a stable, useful input contract. The current choice to document a conversational workflow does not resolve the outstanding Revit 2025 crash confirmation in [issue #14](https://github.com/bimwright/rvt-mcp/issues/14).
