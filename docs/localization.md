# UI Localization

rvt-mcp's **plugin UI** (ribbon labels, tooltips, toasts, History window, dialogs,
Bake Inbox) is localized. The MCP-facing surface — tool names, schemas,
descriptions, wire payloads, logs, and anything an agent writes — stays English.

## Which language shows

Resolution order, evaluated once per Revit session:

1. `BIMWRIGHT_UI_LANGUAGE` environment variable (Windows user/machine env var
   read by the **Revit.exe process** — the `env` block in an MCP client config
   does *not* reach the plugin).
2. `uiLanguage` in `%LOCALAPPDATA%\RvtMcp\rvtmcp.config.json` — written when you
   pick a language in the ribbon.
3. `auto` → follow Revit's UI language (`ControlledApplication.Language`).

Accepted values: `auto`, or one of the 15 shipped codes — `en`, `zh-CN`,
`zh-TW`, `ja`, `ko`, `de`, `fr`, `es`, `it`, `nl`, `pt-BR`, `ru`, `cs`, `pl`,
`hu` (case-sensitive). Anything else, e.g. `vi` or `pt-br`, falls back to
`auto`. A missing key inside a locale falls back to the English string; a
missing catalog falls back to English entirely.

Pick a language from the **Language** combo in the ribbon slide-out (RvtMcp
panel → expand the slide-out). The combo always shows the *effective* language,
including when the env var is overriding. Selection persists across restarts
unless `BIMWRIGHT_UI_LANGUAGE` is set — the env var wins every launch.

Two Revit instances share one config file and one `locales\` folder: a language
picked in one session applies to the other after its next restart (same as
`enableToast`).

## Fixing a translation yourself

Override files live in `%LOCALAPPDATA%\RvtMcp\locales\`:

```
locales\
  strings.<locale>.json     your overrides (e.g. strings.de.json)
  _report.<locale>.json     generated — keys missing from the shipped catalog,
                            plus entries of yours that were rejected (with reason)
  _active.<locale>.json     generated — every key's effective value + locked list
```

Create or edit `strings.<locale>.json` as a flat `{"key": "text"}` map:

```json
{
  "_meta": { "locale": "de" },
  "ribbon.history.text": "Verlauf ({count:n})",
  "toast.rooms.found": "Räume gefunden: {count:n}"
}
```

Rules enforced on save (validated, then hot-reloaded — no restart needed):

- Keys must exist in the shipped catalog — `unknown_key` otherwise.
- Placeholder tokens must match English exactly (`{count:n}`, `{path}`, …) —
  `placeholder_mismatch` otherwise. `{name:n}` renders the value with
  locale-aware number formatting; bare `{name}` is invariant (ids, scales,
  hashes).
- `security.*` keys are **locked** — always rejected (`locked_key`). These are
  the send_code warnings/redaction notices; overriding them would let a file
  soften a security warning.
- Values must be strings ≤ 1000 chars; file ≤ 1 MiB; `_meta` is optional and
  ignored.
- A file that fails JSON parsing keeps the previous good overrides live; check
  `_report.<locale>.json` for `rejected` entries and reasons.

The watcher only reacts to `strings.<active-locale>.json` and
`strings.en.json` — editing an inactive locale's file takes effect when you
switch to it.

`strings.en.json` overrides are allowed (English is just another layer), and
the lookup order is always: **your overrides → shipped locale → shipped
English → the key itself**.

## For agents

`rvt-mcp/AGENTS.md` documents this workflow for the install-side agent. In
short: read `_active.<locale>.json` to see what the user currently sees, write
the fix into `strings.<locale>.json`, re-read `_report.<locale>.json` to confirm
the key was accepted, and tell the user it applied live.

## Caveats

- `uninstall.ps1` removes `%LOCALAPPDATA%\RvtMcp\` wholesale — including
  `locales\` and your overrides. Back them up before a full uninstall.
- MessageBox buttons (OK/Cancel/Yes/No) come from Windows and follow the OS
  language, not this setting.
- The History window re-renders in the new language on switch — including
  Summary lines, which are regenerated from stored call data — but the logged
  JSONL history on disk stays in the language it was written in.
- Translations in the shipped catalogs are machine-translated; corrections via
  override files (or a PR to the catalog) are welcome.

## Manual verification checklist

Run once per release on at least the oldest and newest shells (Revit 2022 /
2027). Requires a Revit install; close other Revit instances first.

**Language selection**

- [ ] Default launch: UI follows the Revit UI language (verify on a non-English
      Revit if available, else pick a non-English language and confirm chrome
      changes).
- [ ] Ribbon slide-out → Language combo lists `Auto` + 15 native names; the
      current effective language is pre-selected.
- [ ] Pick `Deutsch` → ribbon labels, tooltips, History window re-render
      immediately; restart Revit → still German (`uiLanguage` persisted).
- [ ] Set `BIMWRIGHT_UI_LANGUAGE=fr` as a Windows user env var, restart Revit →
      UI is French regardless of the saved pick; combo shows French. Unset.

**Overrides + hot reload**

- [ ] With `en` active, create `%LOCALAPPDATA%\RvtMcp\locales\strings.en.json`
      with one changed key (e.g. `ribbon.history.text`) → ribbon updates within
      ~1s, no restart. (Under another locale the shipped translation still wins —
      en overrides are the fallback layer.)
- [ ] Write an entry with a wrong placeholder (`{count}` → `{n}`) → string
      stays unchanged; `_report.en.json` lists it under `rejected` with
      `placeholder_mismatch`.
- [ ] Write an unknown key → `rejected`/`unknown_key`. Write a `security.*`
      key → `rejected`/`locked_key`.
- [ ] Break the JSON (trailing comma) → previous good values stay live;
      report shows `invalid_json` file error.
- [ ] `_active.<locale>.json` exists, reflects the effective value, and lists
      `security.*` under `locked`.

**Surfaces** (with a non-English locale active)

- [ ] Ribbon: toggle button text + tooltips, History button + count, Toast
      button tooltip, Bake Inbox, Language combo.
- [ ] Toast: run `revit_get_rooms` on an empty and a populated model →
      localized empty-state and "label: count" lines; run `revit_capture_…` →
      localized filename line.
- [ ] History: open → column headers, toolbar, filters, detail pane, section
      headers localized; re-run a `send_code` entry → confirm dialog +
      redaction warnings localized; Summary column re-renders on language
      switch.
- [ ] Dialogs: Copy Connection Info toast/dialog text; baked-slot empty
      message; Bake Inbox window (title, instruction, empty state).
- [ ] MCP-facing check: `tools/list` names/descriptions and error payloads
      remain English.

**Shells**

- [ ] Repeat smoke (launch + language switch + one toast) on each installed
      Revit year: 2022, 2023, 2024, 2025, 2026, 2027.
