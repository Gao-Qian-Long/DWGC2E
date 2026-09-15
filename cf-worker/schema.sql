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
BEGIN
 SELECT RAISE(ABORT,'quota_exceeded');
END;
CREATE TRIGGER IF NOT EXISTS translation_reserve AFTER INSERT ON translation_requests
BEGIN
 UPDATE usage_monthly SET chars_used=chars_used+NEW.reserved WHERE user_id=NEW.user_id AND year_month=NEW.year_month;
END;
CREATE TRIGGER IF NOT EXISTS translation_settle AFTER UPDATE OF state ON translation_requests
WHEN OLD.state='reserved' AND NEW.state='settled'
BEGIN
 UPDATE usage_monthly SET chars_used=chars_used-OLD.reserved+NEW.billed,task_count=task_count+ CASE WHEN NEW.billed>0 THEN 1 ELSE 0 END WHERE user_id=NEW.user_id AND year_month=NEW.year_month;
END;
CREATE TRIGGER IF NOT EXISTS device_owner_guard BEFORE INSERT ON devices
WHEN EXISTS(SELECT 1 FROM devices WHERE device_id=NEW.device_id AND user_id<>NEW.user_id)
BEGIN
 SELECT RAISE(ABORT,'device_conflict');
END;
CREATE TRIGGER IF NOT EXISTS device_limit_insert BEFORE INSERT ON devices
WHEN NEW.revoked=0 AND NOT EXISTS(SELECT 1 FROM devices WHERE device_id=NEW.device_id AND user_id=NEW.user_id AND revoked=0)
 AND (SELECT COUNT(*) FROM devices WHERE user_id=NEW.user_id AND revoked=0)>=3
BEGIN
 SELECT RAISE(ABORT,'device_limit');
END;
CREATE TRIGGER IF NOT EXISTS device_limit_reactivate BEFORE UPDATE OF revoked ON devices
WHEN OLD.revoked<>0 AND NEW.revoked=0 AND (SELECT COUNT(*) FROM devices WHERE user_id=NEW.user_id AND revoked=0)>=3
BEGIN
 SELECT RAISE(ABORT,'device_limit');
END;
-- Additive only; current products are available to authenticated users when payments are enabled.
CREATE TABLE IF NOT EXISTS plans (
 id TEXT PRIMARY KEY, name TEXT NOT NULL, price_cents INTEGER NOT NULL CHECK(price_cents>0),
 duration_days INTEGER NOT NULL CHECK(duration_days>0), membership_level TEXT NOT NULL CHECK(membership_level='pro'), enabled INTEGER NOT NULL DEFAULT 0
);
INSERT OR IGNORE INTO plans VALUES('test_pro_019','Pro 7 天 A',19,7,'pro',1),('test_pro_029','Pro 7 天 B',29,7,'pro',1);
CREATE TABLE IF NOT EXISTS orders (
 order_no TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id), plan_id TEXT NOT NULL REFERENCES plans(id),
 plan_name TEXT NOT NULL, duration_days INTEGER NOT NULL, membership_level TEXT NOT NULL,
 amount_cents INTEGER NOT NULL CHECK(amount_cents>0), payable_cents INTEGER NOT NULL CHECK(payable_cents>0),
 provider TEXT NOT NULL DEFAULT 'ezfpy', channel TEXT NOT NULL CHECK(channel IN ('alipay','wxpay')),
 provider_trade_no TEXT, status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','paid','expired','failed','cancelled','refunded')),
 create_state TEXT NOT NULL DEFAULT 'creating', qr_code TEXT, qr_image_url TEXT,
 idempotency_key TEXT NOT NULL, created_at TEXT NOT NULL, expires_at TEXT NOT NULL, paid_at TEXT, last_error_code TEXT,
 UNIQUE(user_id,idempotency_key)
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_orders_trade ON orders(provider,provider_trade_no) WHERE provider_trade_no IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_orders_user ON orders(user_id,created_at,order_no);
CREATE INDEX IF NOT EXISTS idx_orders_expiry ON orders(status,expires_at);
CREATE TABLE IF NOT EXISTS payment_events (
 id INTEGER PRIMARY KEY AUTOINCREMENT, order_no TEXT, event_type TEXT NOT NULL, reason TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_payment_events_order ON payment_events(order_no,id);
CREATE TABLE IF NOT EXISTS payment_settlements (
 order_no TEXT PRIMARY KEY REFERENCES orders(order_no), provider_trade_no TEXT NOT NULL,
 amount_cents INTEGER NOT NULL, settled_at TEXT NOT NULL
);
CREATE TRIGGER IF NOT EXISTS payment_settlement_guard BEFORE INSERT ON payment_settlements
WHEN NOT EXISTS(SELECT 1 FROM payment_settlements WHERE order_no=NEW.order_no)
BEGIN
 SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM orders WHERE order_no=NEW.order_no AND status='pending' AND create_state='ready'
 AND provider_trade_no=NEW.provider_trade_no AND payable_cents=NEW.amount_cents)
 THEN RAISE(ABORT,'payment_not_ready') END;
END;
CREATE TRIGGER IF NOT EXISTS payment_settlement_apply AFTER INSERT ON payment_settlements
BEGIN
 INSERT INTO subscriptions(user_id,plan_name,starts_at,expires_at,auto_renew,updated_at)
 SELECT user_id,membership_level,NEW.settled_at,strftime('%Y-%m-%dT%H:%M:%fZ',NEW.settled_at,'+'||duration_days||' days'),0,NEW.settled_at
 FROM orders WHERE order_no=NEW.order_no
 ON CONFLICT(user_id) DO UPDATE SET plan_name=excluded.plan_name,
 starts_at= CASE WHEN subscriptions.plan_name='pro' AND julianday(subscriptions.expires_at)>julianday(NEW.settled_at) THEN subscriptions.starts_at ELSE NEW.settled_at END,
 expires_at=strftime('%Y-%m-%dT%H:%M:%fZ',CASE WHEN subscriptions.plan_name='pro' AND julianday(subscriptions.expires_at)>julianday(NEW.settled_at) THEN subscriptions.expires_at ELSE NEW.settled_at END,
 '+'||(SELECT duration_days FROM orders WHERE order_no=NEW.order_no)||' days'),auto_renew=0,updated_at=NEW.settled_at;
 UPDATE orders SET status='paid',paid_at=NEW.settled_at WHERE order_no=NEW.order_no;
 UPDATE usage_monthly SET chars_quota=1000000 WHERE user_id=(SELECT user_id FROM orders WHERE order_no=NEW.order_no) AND year_month=substr(NEW.settled_at,1,7);
 INSERT INTO payment_events(order_no,event_type,created_at) VALUES(NEW.order_no,'payment_settled',NEW.settled_at);
END;

-- Hide unpaid records without deleting financial history or cancelling provider orders.
CREATE TABLE IF NOT EXISTS payment_order_hidden (
 order_no TEXT PRIMARY KEY REFERENCES orders(order_no),
 hidden_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS feedback (id TEXT PRIMARY KEY, email TEXT NOT NULL, category TEXT NOT NULL, message TEXT NOT NULL, page TEXT NOT NULL DEFAULT '', status TEXT NOT NULL DEFAULT 'new' CHECK(status IN ('new','resolved')), created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_feedback_created ON feedback(created_at DESC,id DESC);
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
