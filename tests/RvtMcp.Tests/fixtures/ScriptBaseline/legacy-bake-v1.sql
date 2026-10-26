-- Frozen schema-version-1 storage fixture. Do not create this through BakeDb.Migrate:
-- future migrations must be exercised against the old schema and populated records.
CREATE TABLE schema_version(version INTEGER NOT NULL);
INSERT INTO schema_version VALUES(1);
CREATE TABLE usage_events(
    id TEXT PRIMARY KEY, ts_utc TEXT NOT NULL, source TEXT NOT NULL,
    tool TEXT NOT NULL, normalized_key TEXT NOT NULL, payload_json TEXT NOT NULL,
    body_hash TEXT NULL, success INTEGER NOT NULL
);
CREATE TABLE suggestions(
    id TEXT PRIMARY KEY, cluster_key TEXT NOT NULL, source TEXT NOT NULL,
    title TEXT NOT NULL, description TEXT NOT NULL, state TEXT NOT NULL,
    score REAL NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
    snooze_until TEXT NULL, never_reason TEXT NULL, payload_json TEXT NOT NULL,
    version_history_blob TEXT NOT NULL
);
CREATE UNIQUE INDEX ux_suggestions_cluster_key ON suggestions(cluster_key);
CREATE TABLE registry(
    name TEXT PRIMARY KEY, description TEXT NOT NULL, source TEXT NOT NULL,
    params_schema TEXT NOT NULL, compat_map TEXT NOT NULL, dll_bytes BLOB NULL,
    source_code TEXT NULL, created_from_suggestion_id TEXT NULL,
    reviewed_by_user INTEGER NOT NULL, created_at TEXT NOT NULL,
    last_used_at TEXT NULL, usage_count INTEGER NOT NULL DEFAULT 0,
    failure_rate REAL NOT NULL, lifecycle_state TEXT NOT NULL DEFAULT 'accepted',
    version_history_blob TEXT NOT NULL
);
INSERT INTO registry VALUES
('baseline_preset','Synthetic preset','preset','{"type":"object"}',
 '{"output_choice":"mcp_only"}',NULL,
 '// BIMWRIGHT_BAKED_RUNTIME_SPEC {"kind":"preset","tool":"get_current_view_info","fixed_args":{}}',
 'suggestion_accepted',1,'2026-01-01T00:00:00Z','2026-01-02T00:00:00Z',4,0.25,'accepted',
 '[{"event":"accepted"},{"event":"run_failed"}]'),
('baseline_macro','Synthetic macro','macro','{"type":"object"}',
 '{"output_choice":"mcp_only"}',NULL,
 '// BIMWRIGHT_BAKED_RUNTIME_SPEC {"kind":"macro","sequence":["get_current_view_info","get_selection"]}',
 NULL,1,'2026-01-01T00:00:00Z',NULL,0,0,'accepted','[{"event":"accepted"}]'),
('baseline_compiled','Synthetic compiled storage witness','send_code','{"type":"object"}',
 '{"output_choice":"mcp_only","last_compiled_revit_version":"2024"}',$legacy_dll,$legacy_source,
 NULL,1,'2026-01-01T00:00:00Z','2026-01-02T00:00:00Z',9,0.3333333333333333,'accepted',
 '[{"event":"accepted"},{"event":"run_succeeded"}]'),
('baseline_imported','Synthetic pre-0.3 import','legacy_pre_v0.3','{"type":"object"}',
 '{}',NULL,$legacy_source,NULL,1,'2026-01-01T00:00:00Z',NULL,2,0,'accepted',
 '{"source_status":"legacy_imported"}'),
('baseline_archived','Synthetic archived compiled record','send_code','{"type":"object"}',
 '{}',$legacy_dll,$legacy_source,NULL,1,'2026-01-01T00:00:00Z',NULL,3,0,'archived',
 '[{"event":"accepted"},{"event":"archived"}]');
INSERT INTO suggestions VALUES
('suggestion_open','preset:baseline:open','preset','Open','Synthetic open suggestion','open',1,
 '2026-01-01T00:00:00Z','2026-01-01T00:00:00Z',NULL,NULL,'{"tool":"get_selection","fixed_args":{}}','[]'),
('suggestion_snoozed','macro:baseline:snoozed','macro','Snoozed','Synthetic snoozed suggestion','snoozed',2,
 '2026-01-01T00:00:00Z','2026-01-02T00:00:00Z','2099-01-01T00:00:00Z',NULL,
 '{"sequence":["get_current_view_info","get_selection"]}','[{"event":"snoozed"}]'),
('suggestion_never','send_code:baseline:never','send_code','Never','Synthetic suppressed suggestion','never',3,
 '2026-01-01T00:00:00Z','2026-01-02T00:00:00Z',NULL,'user',
 '{"samples":[]}','[{"event":"never"}]'),
('suggestion_accepted','preset:baseline:accepted','preset','Accepted','Synthetic accepted suggestion','accepted',4,
 '2026-01-01T00:00:00Z','2026-01-02T00:00:00Z',NULL,NULL,
 '{"tool":"get_current_view_info","fixed_args":{}}','[{"event":"accepted","tool_name":"baseline_preset"}]');
INSERT INTO usage_events VALUES
('usage_ok','2026-01-01T00:00:00Z','preset','baseline_preset','baseline:ok','{"success":true}',NULL,1),
('usage_fail','2026-01-02T00:00:00Z','send_code','baseline_compiled','baseline:failed','{"success":false}',
 'synthetic-body-hash',0);
