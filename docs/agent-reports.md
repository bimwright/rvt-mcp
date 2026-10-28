# Agent reports

`revit_report_task_result` lets an agent leave one report for the Revit user when a
job is finished. The add-in stores the report on the user's machine, shows an
"Agent sent a report" toast and lists the report in the **Reports** window (ribbon
button next to History). Links in the report select elements or open views in Revit.

The tool is in the `meta` toolset, so it is on by default. It does not change the
model and stays available with `--read-only`, because it only writes per-user report
data. It needs a gateway and Revit add-in that both support `agent_reports_v1`. An
older add-in returns `AGENT_REPORTS_CAPABILITY_REQUIRED`: update the gateway and the
add-in together from the same Setup release.

## When to call

Call it once per job, after you have verified the whole result, never after each
tool. A report is the agent's own account: the window labels it "Self-reported by the
agent" and shows next to it the changes rvt-mcp actually recorded.

- Use one `task_id` per job. Sending the same `task_id` again to the same Revit
  process replaces that report. Use this to fix links the response lists as
  unresolved.
- `batch_execute` rejects this command; call it directly.
- A job that ran in two Revit processes sends one report to each process.

| Parameter | Rule |
|---|---|
| `task_id` | 1–80 characters, unique per job |
| `outcome` | `completed`, `failed` or `cancelled` |
| `summary` | 1–120 characters, one line; the report title in the list and the toast |
| `body` | Optional Markdown subset (below), at most 32 KiB as UTF-8 |

The server checks these limits before anything is sent to Revit.

## Response

```json
{
  "report_id": "<32 hex>", "replaced": false,
  "links": { "total": 12, "resolved": 11,
    "unresolved": [ { "label": "Pipe DN50", "target": "revit:999", "reason": "element_not_found" },
                    { "label": "Level 2 branch", "target": "revit:5,6", "reason": "element_not_found", "ids": [6] } ],
    "warnings": [ { "target": "revit:123?view=5", "reason": "view_hint_invalid" } ] },
  "changes_attached": { "documents": 1, "added": 4, "modified": 8, "deleted": 0, "complete": true },
  "toast": "shown"
}
```

- `links.total` counts every complete `[…](revit:…)` link, including ones with bad
  syntax.
- `resolved` counts links with at least one element found.
- A partly resolved link counts as resolved and also has an `unresolved` entry whose
  `ids` lists the IDs that were not found.
- `reason` is one of `bad_syntax`, `document_not_open`, `element_not_found`,
  `no_document` or `limit_exceeded`.
- An invalid `view=` hint is dropped and reported under `warnings`; the link still
  works.
- `changes_attached` is `null` when no MCP command has finished since the add-in
  started or since the previous report.
- `toast` is `shown`, or `disabled` when the user has turned toasts off.

## Body format

The body is a small Markdown subset, parsed by the add-in itself:

- headings `#`, `##`, `###` followed by a space;
- paragraphs separated by a blank line; a single line break inside a paragraph is kept;
- bullet (`-`, `*`) and numbered (`1.`) lists, nested one level by indenting two or
  more spaces;
- `**bold**`, `*italic*`, `` `code` `` and fenced code blocks, shown verbatim;
- pipe tables with a separator row (`|---|---|`);
- a line of three or more `-` as a rule.

`_` is always a literal character. A backslash escapes `` \ ` * _ [ ] ( ) | # - ``.
HTML, images, block quotes and autolinks show as plain text. Links to anything other
than `revit:` show their label as plain text and do not open a browser.

## Links

```
[label](revit:<id>[,<id>...][?view=<viewId>][&doc=<document_id>])
```

- **Without `doc`,** the link targets the document that was active when the report
  arrived.
- **For another open document,** add `doc=<_target.document_id>`. Use `?doc=` when
  there is no view parameter.
- **`view=`** is a hint: the view to try first when the user clicks.
- **A link to a view or sheet** needs no special syntax. The add-in recognizes it,
  and a click opens that view.
- **Labels and targets:**
  - the label must not be empty and must fit on one line;
  - the target has no spaces;
  - `view` and `doc` may each appear once.
  - A bare `revit:123` without the bracket form is plain text.
- **Limits:** 300 links per report, 500 IDs per link and 2,000 IDs per report.
  - They are counted in body order, over well-formed links only.
  - A link past a limit is `limit_exceeded`.

When the report arrives, the add-in looks up every ID and stores the element's unique
ID, category and name. A link with no element found shows its label as plain text, so
the user never sees a broken link. The response tells you which links to fix.

## What the user sees

The Reports window has two modes, switched in its title bar:
- **Wide:** the report list on the left, the selected report on the right;
- **Compact:** one column of cards.

Both modes:
- filter reports to the open model (default) or show all models;
- show the report body, then "Actual changes recorded by rvt-mcp";
- let the user copy or delete a report.

The window remembers its mode, its size and position per mode, and its text size
(90–150 %, "Aa" button or Ctrl+Plus / Ctrl+Minus / Ctrl+0).

A link is live when its document is the active document in that Revit process, and
dimmed otherwise. The tooltip of a dimmed link names its model. A click on a live link:

1. finds the elements by unique ID, never by element ID, because another copy of the
   model can reuse IDs for other elements;
2. opens the view or sheet, for a view or sheet link;
3. for elements, picks a view that shows them, in this order:
   - the `view=` hint;
   - the active view;
   - the open views;
   - closed plans, and then `{3D}`, chosen by the add-in within about two seconds;
4. selects the elements and zooms to the ones visible in that view.

A click never opens a transaction or shows a Revit dialog. When nothing matches, the
status line explains why and the active view and selection stay as they were.

## Recorded changes

After every MCP command, the add-in adds that command's changes to a buffer, kept per
document. The next report takes the whole buffer and empties it.
- Add then modify counts as added; add then delete disappears.
- Deleted elements keep only their ID.
- A report sent again with the same `task_id` keeps the changes it already had and
  adds newer ones.
- An incomplete capture, or an Undo/Redo outside MCP commands, marks the changes as
  incomplete. Other manual edits between commands are not tracked.

See [change tracking](change-tracking.md) for how changes are captured.

## Storage and privacy

- **Location:** `%LOCALAPPDATA%\Bimwright\rvt-mcp\reports\`, one JSON file per
  report. Every Revit process reads the folder, so a report written by one process
  shows in the others.
- **Window:** shows the newest 500 reports.
- **Retention:** reports are deleted after 30 days, when the add-in starts. Change this
  with `reportRetentionDays` (1–3650) in `rvtmcp.config.json`. Age is the only reason
  for automatic deletion; the user can also delete a report from the window.
- **Privacy:** `summary` and `body` pass through secret masking before they are
  stored. Reports stay on the user's machine. They can contain project and element
  names, so do not copy them into issues or tests.

## Not in this version

- elements in linked models;
- navigation across Revit processes;
- opening a closed model or switching to a document that is not active;
- comparing the agent's text with the recorded changes;
- search, export and replies from the user to the agent.
