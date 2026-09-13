PRAGMA foreign_keys = ON;
CREATE TABLE IF NOT EXISTS users (id TEXT PRIMARY KEY NOT NULL, account TEXT NOT NULL UNIQUE, password_hash TEXT NOT NULL, display_name TEXT NOT NULL DEFAULT '', email TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL, is_active INTEGER NOT NULL DEFAULT 1);
CREATE UNIQUE INDEX IF NOT EXISTS idx_users_email_lower ON users(lower(email)) WHERE email <> '';
CREATE TABLE IF NOT EXISTS sessions (id TEXT PRIMARY KEY NOT NULL, user_id TEXT NOT NULL, token_hash TEXT NOT NULL UNIQUE, device_id TEXT NOT NULL, expires_at TEXT NOT NULL, created_at TEXT NOT NULL, revoked_at TEXT, FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE);
CREATE INDEX IF NOT EXISTS idx_sessions_token_hash ON sessions(token_hash);
CREATE TABLE IF NOT EXISTS subscriptions (user_id TEXT PRIMARY KEY NOT NULL, plan_name TEXT NOT NULL DEFAULT 'free', starts_at TEXT, expires_at TEXT, auto_renew INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL, FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE);
CREATE TABLE IF NOT EXISTS usage_monthly (user_id TEXT NOT NULL, year_month TEXT NOT NULL, chars_used INTEGER NOT NULL DEFAULT 0, chars_quota INTEGER NOT NULL DEFAULT 100000, task_count INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(user_id, year_month), FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE);
CREATE TABLE IF NOT EXISTS devices (device_id TEXT PRIMARY KEY NOT NULL, user_id TEXT NOT NULL, device_name TEXT NOT NULL DEFAULT '', platform TEXT NOT NULL DEFAULT 'Windows', first_seen TEXT NOT NULL, last_seen TEXT NOT NULL, revoked INTEGER NOT NULL DEFAULT 0, FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE);
CREATE INDEX IF NOT EXISTS idx_devices_user_id ON devices(user_id);
CREATE TABLE IF NOT EXISTS usage_logs (id TEXT PRIMARY KEY NOT NULL, user_id TEXT NOT NULL, device_id TEXT NOT NULL, src_lang TEXT NOT NULL, tgt_lang TEXT NOT NULL, chars INTEGER NOT NULL DEFAULT 0, cached INTEGER NOT NULL DEFAULT 0, model TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL, FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE);

CREATE TABLE IF NOT EXISTS email_verification_codes (id TEXT PRIMARY KEY NOT NULL, email TEXT NOT NULL, purpose TEXT NOT NULL, code_hash TEXT NOT NULL, expires_at TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, used_at TEXT);
CREATE INDEX IF NOT EXISTS idx_email_codes_lookup ON email_verification_codes(email,purpose,created_at);

CREATE TABLE IF NOT EXISTS user_glossaries (user_id TEXT PRIMARY KEY NOT NULL, entries_json TEXT NOT NULL DEFAULT '[]', updated_at TEXT NOT NULL, FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE);

-- Additive upgrade only: never deletes users, codes or existing routing state.
CREATE TABLE IF NOT EXISTS mail_routing_state (
  id TEXT PRIMARY KEY NOT NULL CHECK (id = 'verification'),
  slot INTEGER NOT NULL CHECK (slot IN (0, 1))
);

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
BEGIN UPDATE usage_monthly SET chars_used=chars_used-OLD.reserved+NEW.billed,task_count=task_count+CASE WHEN NEW.billed>0 THEN 1 ELSE 0 END WHERE user_id=NEW.user_id AND year_month=NEW.year_month; END;
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
