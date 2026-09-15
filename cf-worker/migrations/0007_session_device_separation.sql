-- Additive model: web sessions never require or consume an APP binding.
-- Historical rows are classified separately by the reviewed migration manifest.
CREATE TABLE IF NOT EXISTS app_device_bindings (
 user_id TEXT NOT NULL REFERENCES users(id), device_id TEXT NOT NULL,
 device_name TEXT NOT NULL DEFAULT '', platform TEXT NOT NULL DEFAULT 'Windows',
 first_seen TEXT NOT NULL, last_seen TEXT NOT NULL, revoked INTEGER NOT NULL DEFAULT 0 CHECK(revoked IN (0,1)),
 PRIMARY KEY(user_id,device_id)
);
CREATE INDEX IF NOT EXISTS idx_app_bindings_active ON app_device_bindings(user_id,revoked,last_seen);
CREATE TRIGGER IF NOT EXISTS app_binding_limit_insert BEFORE INSERT ON app_device_bindings
WHEN NEW.revoked=0 AND NOT EXISTS(SELECT 1 FROM app_device_bindings WHERE user_id=NEW.user_id AND device_id=NEW.device_id AND revoked=0)
 AND (SELECT COUNT(*) FROM app_device_bindings WHERE user_id=NEW.user_id AND revoked=0)>=3
BEGIN SELECT RAISE(ABORT,'device_limit'); END;
CREATE TRIGGER IF NOT EXISTS app_binding_limit_reactivate BEFORE UPDATE OF revoked ON app_device_bindings
WHEN OLD.revoked=1 AND NEW.revoked=0 AND (SELECT COUNT(*) FROM app_device_bindings WHERE user_id=NEW.user_id AND revoked=0)>=3
BEGIN SELECT RAISE(ABORT,'device_limit'); END;
CREATE TRIGGER IF NOT EXISTS app_binding_identity_immutable BEFORE UPDATE OF user_id,device_id ON app_device_bindings
WHEN NEW.user_id<>OLD.user_id OR NEW.device_id<>OLD.device_id
BEGIN SELECT RAISE(ABORT,'binding_identity_immutable'); END;
CREATE TABLE IF NOT EXISTS session_contexts (
 session_id TEXT PRIMARY KEY REFERENCES sessions(id) ON DELETE CASCADE,
 client_kind TEXT NOT NULL CHECK(client_kind IN ('web','app'))
);
CREATE TABLE IF NOT EXISTS device_migration_audit (
 user_id TEXT NOT NULL, device_id TEXT NOT NULL,
 classification TEXT NOT NULL CHECK(classification IN ('web','app','unknown')),
 evidence TEXT NOT NULL, reviewed_at TEXT NOT NULL,
 PRIMARY KEY(user_id,device_id)
);
