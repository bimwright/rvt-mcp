# Firm Profiles

Profile JSON files loaded by `detect_firm_profile` and `suggest_view_name_corrections` when called with `profile=<id>`.

**Empty in v0.2.1.** Seed profiles land in v0.3+ once we have real user feedback on which conventions matter.

## Discovery

Files are scanned from:

1. `<plugin-dir>/firm-profiles/*.json` — shipped with the plugin (read-only for users).
2. `%LOCALAPPDATA%\Bimwright\rvt-mcp\firm-profiles\*.json` — user-added.

User profiles override shipped profiles on `id` collision.

## Schema

```json
{
  "id": "iso-19650-uk",
  "name": "ISO 19650 UK National Annex",
  "description": "Sheet numbering per PAS 1192, view naming per UK BIM Framework",
  "matchHints": [
    { "kind": "sheet_prefix",  "regex": "^[A-Z]{2,4}-\\d{3}$", "weight": 0.4 },
    { "kind": "view_dominant", "regex": "^L\\d{2}-.+$",         "weight": 0.3 },
    { "kind": "level_pattern", "regex": "^L\\d{2}$",             "weight": 0.3 }
  ],
  "rules": {
    "viewName":    "L{NN}-{Name}",
    "sheetNumber": "{Discipline}-{NNN}",
    "level":       "L{NN}"
  }
}
```

| Field | Type | Required | Notes |
|---|---|---|---|
| `id` | string | yes | Unique key. `a-z0-9-` convention. |
| `name` | string | yes | Human-facing. |
| `description` | string | no | One-sentence context. |
| `matchHints` | array | no | Hints that fingerprint this profile. Weights should sum to ≤ 1.0. |
| `matchHints[].kind` | enum | yes | `sheet_prefix` \| `view_dominant` \| `level_pattern` |
| `matchHints[].regex` | string | yes | Regex applied to evidence from `detect_firm_profile`. |
| `matchHints[].weight` | number | yes | 0..1. Summed across matching hints → confidence. |
| `rules.viewName` | string | no | Token template. `{NN}` = digits, `{Name}` = variable text. |
| `rules.sheetNumber` | string | no | Same token syntax. |
| `rules.level` | string | no | Same token syntax. |

## Matching

A profile qualifies when sum of matching-hint weights ≥ 0.50. Best match = highest weight; ties broken by most matching hints, then alphabetical id.

## Token syntax

- `{NN}` — all-digit token (any length)
- `{Name}` — variable alpha token
- Literal text — kept as-is

Example: `L{NN}-{Name}` matches `L01-Lobby`, `L12-Office`, but not `Level 1` (would tokenize to `{Name} {NN}`).

## Contributing a profile

v0.2.1 ships with no profiles shipped. If you'd like to propose one (generic standards like ISO 19650 or a published firm convention — not a private client's), open a PR adding `<id>.json` in this folder.
