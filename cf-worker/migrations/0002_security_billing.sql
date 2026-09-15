-- Additive security and idempotent billing state. Existing users and usage are preserved.
CREATE TABLE IF NOT EXISTS request_limits (key TEXT PRIMARY KEY, window_start INTEGER NOT NULL, count INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS translation_requests (
 user_id TEXT NOT NULL REFERENCES users(id), request_id TEXT NOT NULL, payload_hash TEXT NOT NULL,
 year_month TEXT NOT NULL, reserved INTEGER NOT NULL, billed INTEGER NOT NULL DEFAULT 0,
 state TEXT NOT NULL DEFAULT 'reserved', response_json TEXT, response_status INTEGER,
 expires_at INTEGER NOT NULL, PRIMARY KEY(user_id,request_id)
);
CREATE INDEX IF NOT EXISTS idx_translation_requests_expiry ON translation_requests(user_id,state,expires_at);
CREATE TRIGGER IF NOT EXISTS translation_reserve_guard BEFORE INSERT ON translation_requests
WHEN NOT EXISTS(SELECT 1 FROM translation_requests WHERE user_id=NEW.user_id AND request_id=NEW.request_id)
 AND NOT EXISTS(SELECT 1 FROM usage_monthly WHERE user_id=NEW.user_id AND year_month=NEW.year_month AND chars_used+NEW.reserved<=chars_quota)
BEGIN SELECT RAISE(ABORT,'quota_exceeded'); END;
CREATE TRIGGER IF NOT EXISTS translation_reserve AFTER INSERT ON translation_requests
BEGIN UPDATE usage_monthly SET chars_used=chars_used+NEW.reserved WHERE user_id=NEW.user_id AND year_month=NEW.year_month; END;
CREATE TRIGGER IF NOT EXISTS translation_settle AFTER UPDATE OF state ON translation_requests
WHEN OLD.state='reserved' AND NEW.state='settled'
BEGIN UPDATE usage_monthly SET chars_used=chars_used-OLD.reserved+NEW.billed,task_count=task_count+ CASE WHEN NEW.billed>0 THEN 1 ELSE 0 END WHERE user_id=NEW.user_id AND year_month=NEW.year_month; END;
CREATE TRIGGER IF NOT EXISTS device_owner_guard BEFORE INSERT ON devices
WHEN EXISTS(SELECT 1 FROM devices WHERE device_id=NEW.device_id AND user_id<>NEW.user_id)
BEGIN SELECT RAISE(ABORT,'device_conflict'); END;
CREATE TRIGGER IF NOT EXISTS device_limit_insert BEFORE INSERT ON devices
WHEN NEW.revoked=0 AND NOT EXISTS(SELECT 1 FROM devices WHERE device_id=NEW.device_id AND user_id=NEW.user_id AND revoked=0)
 AND (SELECT COUNT(*) FROM devices WHERE user_id=NEW.user_id AND revoked=0)>=3
BEGIN SELECT RAISE(ABORT,'device_limit'); END;
CREATE TRIGGER IF NOT EXISTS device_limit_reactivate BEFORE UPDATE OF revoked ON devices
WHEN OLD.revoked<>0 AND NEW.revoked=0 AND (SELECT COUNT(*) FROM devices WHERE user_id=NEW.user_id AND revoked=0)>=3
BEGIN SELECT RAISE(ABORT,'device_limit'); END;
