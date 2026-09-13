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
