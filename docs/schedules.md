# Schedule inputs and rendered rows

`revit_create_schedule` accepts `phase` as a phase name or element ID string.
When omitted, a phase-aware schedule uses the active view's phase. If that view
has no valid phase, specify `phase`; creation fails with an explanation instead
of choosing a different project phase.

The `fields`, `filters` and `sortGroup` arguments are JSON array strings:

```json
{
  "category": "Rooms",
  "name": "Rooms by level",
  "phase": "New Construction",
  "fields": "[{\"parameterName\":\"Number\"},{\"parameterName\":\"Name\"},{\"parameterName\":\"Level\"}]",
  "filters": "[{\"fieldName\":\"Level\",\"operator\":\"equals\",\"value\":\"Level 1\"}]",
  "sortGroup": "[{\"fieldName\":\"Number\",\"ascending\":false}]"
}
```

Use names present in the active model. A filter or sort field must be included
in the schedule. ElementId fields accept a numeric ID or an unambiguous name
within the referenced element category; use an ID when a name is ambiguous.

`revit_update_schedule_field` takes JSON object strings: use
`fieldRef="{\"fieldName\":\"Level\"}"` and `changes="{\"hidden\":true}"`
to hide a filter column. `fieldIndex` is an alternative field reference; a plain
field name or number is not a JSON object.

Both create and apply-filter/sort accept boolean `ascending`, or string
`order` / `sortOrder` with `Ascending` or `Descending`. Conflicting directions
and invalid alias values are rejected.

`revit_get_schedule_data` reads rendered text through `ViewSchedule.GetCellText`.
`rows` preserves Revit body-row indices for pagination. The parallel `rowKinds`
array identifies `header`, `blank`, `text` and `data` rows; use this array when
counting data rows. `text` covers other native text-only rows, including group
labels, without claiming that every body row is an element.
