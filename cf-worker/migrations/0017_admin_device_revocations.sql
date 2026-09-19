-- Admin override is authorized by a one-use, audited SQL trigger, never by a user payload.
CREATE TABLE IF NOT EXISTS admin_device_revocations (
 id TEXT PRIMARY KEY, user_id TEXT NOT NULL, device_id TEXT NOT NULL, first_seen TEXT NOT NULL,
 actor TEXT NOT NULL, reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 500),
 created_at TEXT NOT NULL, applied INTEGER NOT NULL DEFAULT 0 CHECK(applied IN (0,1))
);
CREATE TRIGGER IF NOT EXISTS admin_device_revocation_guard BEFORE INSERT ON admin_device_revocations BEGIN
 SELECT CASE WHEN NEW.applied<>0 OR NOT EXISTS(SELECT 1 FROM app_device_bindings WHERE user_id=NEW.user_id AND device_id=NEW.device_id AND first_seen=NEW.first_seen AND revoked=0) THEN RAISE(ABORT,'device_binding_changed') END;
END;
DROP TRIGGER IF EXISTS app_binding_age_guard;
CREATE TRIGGER app_binding_age_guard BEFORE UPDATE OF revoked ON app_device_bindings
WHEN OLD.revoked=0 AND NEW.revoked=1
 AND NOT EXISTS(SELECT 1 FROM admin_device_revocations WHERE user_id=OLD.user_id AND device_id=OLD.device_id AND first_seen=OLD.first_seen AND applied=0)
 AND (julianday(OLD.first_seen) IS NULL OR julianday('now')<=julianday(OLD.first_seen)+COALESCE((SELECT json_extract(value_json,'$.device_wait_days') FROM operation_settings WHERE section='controls'),20))
BEGIN SELECT RAISE(ABORT,'device_binding_locked'); END;
CREATE TRIGGER IF NOT EXISTS admin_device_revocation_apply AFTER INSERT ON admin_device_revocations BEGIN
 UPDATE app_device_bindings SET revoked=1 WHERE user_id=NEW.user_id AND device_id=NEW.device_id AND first_seen=NEW.first_seen AND revoked=0;
 UPDATE sessions SET revoked_at=NEW.created_at WHERE user_id=NEW.user_id AND device_id=NEW.device_id AND revoked_at IS NULL AND id IN (SELECT session_id FROM session_contexts WHERE client_kind='app');
 UPDATE admin_device_revocations SET applied=1 WHERE id=NEW.id;
END;
