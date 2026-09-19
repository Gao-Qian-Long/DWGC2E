-- Durable operations configuration. No secrets or payment state are stored here.
CREATE TABLE IF NOT EXISTS operation_settings (
 section TEXT PRIMARY KEY CHECK(section IN ('release','content','controls')),
 value_json TEXT NOT NULL CHECK(json_valid(value_json)), revision INTEGER NOT NULL DEFAULT 1 CHECK(revision>0)
);
CREATE TABLE IF NOT EXISTS operation_changes (
 id TEXT PRIMARY KEY, section TEXT NOT NULL REFERENCES operation_settings(section), actor TEXT NOT NULL,
 before_revision INTEGER NOT NULL, before_snapshot TEXT NOT NULL, value_json TEXT NOT NULL CHECK(json_valid(value_json)),
 reason TEXT NOT NULL, created_at TEXT NOT NULL
);
INSERT OR IGNORE INTO operation_settings VALUES('release','{}',1),('content','{}',1),('controls','{}',1);
CREATE TRIGGER IF NOT EXISTS operation_change_guard BEFORE INSERT ON operation_changes BEGIN
 SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM operation_settings WHERE section=NEW.section AND revision=NEW.before_revision AND value_json=NEW.before_snapshot) THEN RAISE(ABORT,'operation_conflict') END;
END;
CREATE TRIGGER IF NOT EXISTS operation_change_apply AFTER INSERT ON operation_changes BEGIN
 UPDATE operation_settings SET value_json=NEW.value_json,revision=revision+1 WHERE section=NEW.section;
END;
CREATE TABLE IF NOT EXISTS operation_notes (
 id TEXT PRIMARY KEY, kind TEXT NOT NULL CHECK(kind IN ('order','feedback')), target_id TEXT NOT NULL,
 actor TEXT NOT NULL, status TEXT NOT NULL, note TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_operation_notes_target ON operation_notes(kind,target_id,created_at);
DROP TRIGGER IF EXISTS app_binding_age_guard;
CREATE TRIGGER app_binding_age_guard BEFORE UPDATE OF revoked ON app_device_bindings
WHEN OLD.revoked=0 AND NEW.revoked=1 AND (julianday(OLD.first_seen) IS NULL OR julianday('now')<=julianday(OLD.first_seen)+COALESCE((SELECT json_extract(value_json,'$.device_wait_days') FROM operation_settings WHERE section='controls'),20))
BEGIN SELECT RAISE(ABORT,'device_binding_locked'); END;

