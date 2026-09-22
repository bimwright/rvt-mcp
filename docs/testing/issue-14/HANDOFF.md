# Issue #14 handoff — 2026-09-22

Session implementation and local verification are complete. **Do not equate this checkpoint with closing the GitHub issue.** Owner requested local commits now, then notification on #14 with the next authorized push. No push, public comment, release, or issue closure was performed in this session.

## Resume here

1. Inspect current branch/status; this checkpoint is on `master`. Preserve the pre-existing untracked `tests/RvtMcp.Tests/TestResults/`.
2. On the owner's next push request, push the issue #14 commits along with the agreed scope.
3. After successful push, post [ISSUE-COMMENT.md](ISSUE-COMMENT.md) with `gh issue comment 14 --repo bimwright/rvt-mcp --body-file docs/testing/issue-14/ISSUE-COMMENT.md`. It includes the **entire self-contained C# payload**, not just links. Check for an equivalent existing comment first to avoid duplication. This future notification was requested by the owner; do not post before the push.
4. Record the resulting commit/comment URLs here. Do not promise a published plugin until release packaging actually happens.
5. Request reporter verification on Revit 2025 or their failing script/minimal model. Track the optional native `create_stairs` request separately or agree its disposition before closing all of #14.

## Commits and evidence

- `f35a957`: syntax-aware send-code source builder; opt-in SafeFailuresPreprocessor; warning visibility; finally/Cancel documentation. 484 tests passed; plugins for 2022–2027 built with deployment disabled.
- `4769935`: fresh deployed-plugin Revit 2027 verification, retained straight-stair script/result/image, next-push draft.
- `811ea99`: retained U stair with two runs and intermediate landing, instance-only railing adjustment, readback and iso image.
- This handoff adds a portable reporter payload and a ready-to-post full-code comment. No product implementation changes.

The [verification record](README.md) contains build/version details, plugin SHA-256, live matrix, images, and original exact payloads. The original fixture scripts remain in Git; they were not overwritten by the portable version.

## Reporter package

- [reporter-u-stair.cs](reporter-u-stair.cs): complete C# compilation unit with `McpDynamicScript.Run(UIApplication)` and its own failure preprocessor. No Snowdon IDs, no local file paths, and no dependency on the unreleased plugin helper. Creates two test levels (0 / 3,200 mm), a 20-riser U stair, one landing, and an isolated iso view in the active architectural test project. Uses its default stair/railing types; warnings may vary. Keeps the result by default; does not save.
- [reporter-u-stair.tool-arguments.json](reporter-u-stair.tool-arguments.json): ready-made `{ "code": "..." }` arguments for `revit_send_code_to_revit`; generated from the complete `.cs` file.
- [ISSUE-COMMENT.md](ISSUE-COMMENT.md): copy/paste user instructions plus full source. Explain the full-class workaround versus the new wrapper/helper fix; neither requires Reflection.Emit.

Portable payload validation: executed on Revit 2027 with only `KeepResult` changed to `false`. It created/committed the stair scope and view inside its outer group, verified 2 runs / 1 landing / 20 risers, then rolled the group back. HadErrors=false; the sample default Cable Railing emitted the recorded rail-not-continuous warning. This test does not verify the new payload's retained-result branch or Revit 2025/v0.6.1 runtime. The full-source entrypoint form and retained-view flow were independently exercised earlier. Do not label the portable package as reporter-confirmed.

## Local runtime checkpoint

- Connected target: Revit 2027. Recheck routing before any future model operation; 2022 was also running during this work.
- Saved model: `D:\Projects\bimwright\.cursor\issue14-revit2027-20260922\Issue14-stairs-demo-2027.rvt` (local evidence only; not committed).
- Straight stair: 3327759; view `Issue14 - Send-code Stair Demo` (3327821).
- U stair: 3327830; runs 3327831/3327835; landing 3327839; active view `Issue14 - U Stair - ISO` (3327923).
- Retained model has 28 stairs; both demo cases are saved. Portable validation was rolled back, leaving IsModified=false and IsModifiable=false.
- Raw payloads, logs, plugin backup, and crash evidence: sibling `.cursor/issue14-revit2027-20260922/`; 2022 evidence: `.cursor/issue14-revit2022-20260922/`. Do not publish raw crash dumps/logs or the Autodesk sample model as part of the issue comment.
- Never redeploy a plugin DLL while its target Revit version is running.

## Remaining limits

- Original Revit 2025 Error-severity tread-depth crash not reproduced. The tested 100 mm tread was a Warning; Error rollback used an injected Error.
- Earlier local Revit 2027 crash attribution remains unresolved. Subsequent fresh-process probes passed; that is not proof of the earlier crash's cause or fix.
- Warning deletion allows commit and does not establish dimensional/design compliance. Preserve warning messages in reports; the U-stair initial railing warning must not be erased from the history by its later successful type change.
- No native stair creation tool was added. Full-class portable workaround is useful on the existing release; mixed-body declarations and the built-in plugin helper are still unreleased.
