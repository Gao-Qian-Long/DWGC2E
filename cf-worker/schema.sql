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
 expires_at INTEGER NOT NULL, source_language TEXT, target_language TEXT, started_at TEXT, completed_at TEXT, PRIMARY KEY(user_id,request_id)
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
CREATE TRIGGER payment_settlement_guard BEFORE INSERT ON payment_settlements
BEGIN
 SELECT CASE WHEN EXISTS(SELECT 1 FROM payment_settlements WHERE order_no=NEW.order_no AND (provider_trade_no<>NEW.provider_trade_no OR amount_cents<>NEW.amount_cents)) THEN RAISE(ABORT,'settlement_conflict') END;
 SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM payment_settlements WHERE order_no=NEW.order_no) AND NOT EXISTS(
 SELECT 1 FROM orders WHERE order_no=NEW.order_no AND status IN ('pending','expired') AND payable_cents=NEW.amount_cents
 AND (provider_trade_no IS NULL OR provider_trade_no=NEW.provider_trade_no) AND length(NEW.provider_trade_no) BETWEEN 1 AND 128
 ) THEN RAISE(ABORT,'payment_mismatch') END;
END;
CREATE TRIGGER payment_settlement_bind AFTER INSERT ON payment_settlements
BEGIN
 UPDATE orders SET provider_trade_no=NEW.provider_trade_no,create_state='ready',last_error_code=NULL WHERE order_no=NEW.order_no;
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

CREATE TABLE IF NOT EXISTS payment_recovery (
 order_no TEXT PRIMARY KEY REFERENCES orders(order_no), next_attempt_at TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0,
 lease_until TEXT, lease_token TEXT, state TEXT NOT NULL DEFAULT 'queued' CHECK(state IN ('queued','waiting_callback','manual','resolved')),
 reason TEXT NOT NULL DEFAULT 'awaiting_confirmation', updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_payment_recovery_due ON payment_recovery(state,next_attempt_at);
CREATE TRIGGER IF NOT EXISTS payment_recovery_created AFTER INSERT ON orders BEGIN
 INSERT OR IGNORE INTO payment_recovery(order_no,next_attempt_at,updated_at) VALUES(NEW.order_no,strftime('%Y-%m-%dT%H:%M:%fZ',NEW.created_at,'+5 minutes'),NEW.created_at);
END;
CREATE TRIGGER IF NOT EXISTS payment_recovery_settled AFTER INSERT ON payment_settlements BEGIN
 UPDATE payment_recovery SET state='resolved',reason='trusted_callback',lease_until=NULL,lease_token=NULL,updated_at=NEW.settled_at WHERE order_no=NEW.order_no;
END;
-- Backfill by age: a recently created order keeps its normal retry schedule, while an order that
-- already exceeded the review threshold is handed to a human queue instead of being swept into
-- `manual` by the first cron run.
INSERT OR IGNORE INTO payment_recovery(order_no,next_attempt_at,lease_until,lease_token,state,reason,updated_at)
 SELECT order_no,strftime('%Y-%m-%dT%H:%M:%fZ',created_at,'+5 minutes'),NULL,NULL,'queued','awaiting_confirmation',created_at
 FROM orders WHERE status IN ('pending','expired') AND julianday(created_at)>julianday('now','-1 day');
INSERT OR IGNORE INTO payment_recovery(order_no,next_attempt_at,lease_until,lease_token,state,reason,updated_at)
 SELECT order_no,strftime('%Y-%m-%dT%H:%M:%fZ','now','+5 minutes'),NULL,NULL,'manual','legacy_order_requires_review',created_at
 FROM orders WHERE status IN ('pending','expired');
-- Additive, administrator membership changes only. Payment records and consumption remain untouched.
CREATE TABLE IF NOT EXISTS admin_membership_changes (
 id TEXT PRIMARY KEY NOT NULL,
 user_id TEXT NOT NULL REFERENCES users(id),
 actor TEXT NOT NULL,
 before_snapshot TEXT NOT NULL,
 plan_name TEXT NOT NULL CHECK(plan_name IN ('free','pro')),
 expires_at TEXT,
 reason TEXT NOT NULL CHECK(length(reason) BETWEEN 5 AND 500),
 created_at TEXT NOT NULL,
 CHECK((plan_name='free' AND expires_at IS NULL) OR (plan_name='pro' AND expires_at IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS idx_admin_membership_user ON admin_membership_changes(user_id,created_at DESC,id DESC);
CREATE TRIGGER IF NOT EXISTS admin_membership_validate BEFORE INSERT ON admin_membership_changes
BEGIN
 SELECT CASE WHEN NEW.before_snapshot <> COALESCE((SELECT json_array(plan_name,starts_at,expires_at,auto_renew,updated_at) FROM subscriptions WHERE user_id=NEW.user_id),'null') THEN RAISE(ABORT,'membership_conflict') END;
END;
CREATE TRIGGER IF NOT EXISTS admin_membership_apply AFTER INSERT ON admin_membership_changes
BEGIN
 INSERT INTO subscriptions(user_id,plan_name,starts_at,expires_at,auto_renew,updated_at)
 VALUES(NEW.user_id,NEW.plan_name,CASE WHEN NEW.plan_name='pro' THEN NEW.created_at ELSE NULL END,NEW.expires_at,0,NEW.created_at)
 ON CONFLICT(user_id) DO UPDATE SET plan_name=excluded.plan_name,
 starts_at=CASE WHEN excluded.plan_name='free' THEN NULL WHEN subscriptions.plan_name='pro' THEN COALESCE(subscriptions.starts_at,excluded.starts_at) ELSE excluded.starts_at END,
 expires_at=excluded.expires_at,auto_renew=0,updated_at=excluded.updated_at;
 UPDATE usage_monthly SET chars_quota=CASE WHEN NEW.plan_name='pro' AND NEW.expires_at>NEW.created_at THEN 1000000 ELSE 100000 END WHERE user_id=NEW.user_id AND year_month=substr(NEW.created_at,1,7);
END;


-- Four-tier catalog; preserves historical products, orders, users and consumption.
CREATE TABLE IF NOT EXISTS plan_catalog (
 id TEXT PRIMARY KEY CHECK(id IN ('free','pro','max','go')), name TEXT NOT NULL,
 price_cents INTEGER NOT NULL CHECK(price_cents BETWEEN 0 AND 10000000),
 quota INTEGER NOT NULL CHECK(quota BETWEEN 0 AND 1000000000),
 duration_days INTEGER NOT NULL CHECK(duration_days BETWEEN 1 AND 366),
 enabled INTEGER NOT NULL CHECK(enabled IN (0,1)), revision INTEGER NOT NULL DEFAULT 1,
 updated_at TEXT NOT NULL,
 CHECK((id='free' AND price_cents=0 AND enabled=1) OR (id<>'free' AND price_cents>0)),
 CHECK(enabled=0 OR quota>0)
);
INSERT OR IGNORE INTO plan_catalog VALUES
 ('free','Free',0,100000,30,1,1,strftime('%Y-%m-%dT%H:%M:%fZ','now')),
 ('pro','Pro',3900,1000000,30,1,1,strftime('%Y-%m-%dT%H:%M:%fZ','now')),
 ('max','Max',5900,8000000,30,1,1,strftime('%Y-%m-%dT%H:%M:%fZ','now')),
 ('go','Go',1,0,30,0,1,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
-- Legacy FK compatibility: actual tier and quota come from immutable order snapshots.
INSERT OR IGNORE INTO plans(id,name,price_cents,duration_days,membership_level,enabled)
 SELECT id,name,price_cents,duration_days,'pro',enabled FROM plan_catalog WHERE id<>'free';
UPDATE plans SET enabled=0 WHERE id IN ('test_pro_019','test_pro_029');
CREATE TABLE IF NOT EXISTS plan_changes (
 id TEXT PRIMARY KEY, actor TEXT NOT NULL, plan_id TEXT NOT NULL REFERENCES plan_catalog(id),
 before_revision INTEGER NOT NULL, price_cents INTEGER NOT NULL, quota INTEGER NOT NULL,
 duration_days INTEGER NOT NULL, enabled INTEGER NOT NULL, reason TEXT NOT NULL CHECK(length(reason) BETWEEN 5 AND 500),
 before_snapshot TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE TRIGGER IF NOT EXISTS plan_change_guard BEFORE INSERT ON plan_changes BEGIN
 SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM plan_catalog WHERE id=NEW.plan_id AND revision=NEW.before_revision)
 THEN RAISE(ABORT,'plan_conflict') END;
END;
CREATE TRIGGER IF NOT EXISTS plan_change_apply AFTER INSERT ON plan_changes BEGIN
 UPDATE plan_catalog SET price_cents=NEW.price_cents,quota=NEW.quota,duration_days=NEW.duration_days,enabled=NEW.enabled,revision=revision+1,updated_at=NEW.created_at WHERE id=NEW.plan_id;
 UPDATE plans SET price_cents=NEW.price_cents,duration_days=NEW.duration_days,enabled=NEW.enabled WHERE id=NEW.plan_id AND id<>'free';
END;
CREATE TABLE IF NOT EXISTS order_entitlements (
 order_no TEXT PRIMARY KEY REFERENCES orders(order_no), tier TEXT NOT NULL, quota INTEGER NOT NULL, revision INTEGER NOT NULL
);
CREATE TRIGGER IF NOT EXISTS order_catalog_snapshot AFTER INSERT ON orders
WHEN EXISTS(SELECT 1 FROM plan_catalog WHERE id=NEW.plan_id) BEGIN
 SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM plan_catalog WHERE id=NEW.plan_id AND id<>'free' AND enabled=1 AND price_cents=NEW.amount_cents AND duration_days=NEW.duration_days AND id=NEW.membership_level)
 THEN RAISE(ABORT,'plan_conflict') END;
 INSERT INTO order_entitlements SELECT NEW.order_no,id,quota,revision FROM plan_catalog WHERE id=NEW.plan_id;
END;
CREATE TABLE IF NOT EXISTS subscription_quotas (
 user_id TEXT PRIMARY KEY REFERENCES users(id), quota INTEGER NOT NULL
);
INSERT OR IGNORE INTO subscription_quotas SELECT user_id,1000000 FROM subscriptions WHERE plan_name='pro';
CREATE TABLE IF NOT EXISTS quota_addons (
 order_no TEXT PRIMARY KEY REFERENCES orders(order_no), user_id TEXT NOT NULL REFERENCES users(id),
 quota INTEGER NOT NULL CHECK(quota>0), starts_at TEXT NOT NULL, expires_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_quota_addons_user ON quota_addons(user_id,expires_at);
CREATE TABLE IF NOT EXISTS addon_allocations (
 user_id TEXT NOT NULL, request_id TEXT NOT NULL, order_no TEXT NOT NULL REFERENCES quota_addons(order_no),
 reserved INTEGER NOT NULL, amount INTEGER NOT NULL CHECK(amount>=0 AND amount<=reserved),
 PRIMARY KEY(user_id,request_id,order_no), FOREIGN KEY(user_id,request_id) REFERENCES translation_requests(user_id,request_id)
);
CREATE VIEW IF NOT EXISTS addon_balances AS
 SELECT a.*,a.quota-COALESCE((SELECT SUM(amount) FROM addon_allocations x WHERE x.order_no=a.order_no),0) remaining FROM quota_addons a;
DROP TRIGGER IF EXISTS payment_settlement_apply;
CREATE TRIGGER payment_settlement_apply AFTER INSERT ON payment_settlements BEGIN
 SELECT CASE WHEN EXISTS(SELECT 1 FROM orders o LEFT JOIN order_entitlements e ON e.order_no=o.order_no
  WHERE o.order_no=NEW.order_no AND o.membership_level<>'go' AND e.quota IS NULL
  AND NOT EXISTS(SELECT 1 FROM plan_catalog c WHERE c.id=o.membership_level))
 THEN RAISE(ABORT,'settlement_quota_mapping_missing') END;
 INSERT INTO subscriptions(user_id,plan_name,starts_at,expires_at,auto_renew,updated_at)
 SELECT user_id,membership_level,NEW.settled_at,strftime('%Y-%m-%dT%H:%M:%fZ',NEW.settled_at,'+'||duration_days||' days'),0,NEW.settled_at
 FROM orders WHERE order_no=NEW.order_no AND membership_level<>'go'
 ON CONFLICT(user_id) DO UPDATE SET plan_name=excluded.plan_name,
 starts_at=CASE WHEN julianday(subscriptions.expires_at)>julianday(NEW.settled_at) THEN COALESCE(subscriptions.starts_at,subscriptions.expires_at) ELSE NEW.settled_at END,
 expires_at=strftime('%Y-%m-%dT%H:%M:%fZ',CASE WHEN julianday(subscriptions.expires_at)>julianday(NEW.settled_at) THEN subscriptions.expires_at ELSE NEW.settled_at END,'+'||(SELECT duration_days FROM orders WHERE order_no=NEW.order_no)||' days'),auto_renew=0,updated_at=NEW.settled_at;
 INSERT INTO subscription_quotas(user_id,quota)
 SELECT o.user_id,COALESCE(s.quota,(SELECT c.quota FROM plan_catalog c WHERE c.id=o.membership_level)) FROM orders o LEFT JOIN order_entitlements s ON s.order_no=o.order_no WHERE o.order_no=NEW.order_no AND o.membership_level<>'go'
 ON CONFLICT(user_id) DO UPDATE SET quota=excluded.quota;
 INSERT INTO quota_addons SELECT o.order_no,o.user_id,s.quota,NEW.settled_at,strftime('%Y-%m-%dT%H:%M:%fZ',NEW.settled_at,'+'||o.duration_days||' days') FROM orders o JOIN order_entitlements s ON s.order_no=o.order_no WHERE o.order_no=NEW.order_no AND o.membership_level='go';
 UPDATE orders SET status='paid',paid_at=NEW.settled_at WHERE order_no=NEW.order_no;
 UPDATE usage_monthly SET chars_quota=(SELECT quota FROM subscription_quotas WHERE user_id=usage_monthly.user_id) WHERE user_id=(SELECT user_id FROM orders WHERE order_no=NEW.order_no AND membership_level<>'go') AND year_month=substr(NEW.settled_at,1,7);
 INSERT INTO payment_events(order_no,event_type,created_at) VALUES(NEW.order_no,'payment_settled',NEW.settled_at);
END;
-- Base consumption excludes Go allocations; Go balances do not reset across calendar months.
DROP TRIGGER IF EXISTS translation_reserve_guard;
DROP TRIGGER IF EXISTS translation_reserve;
DROP TRIGGER IF EXISTS translation_settle;
CREATE TRIGGER translation_reserve_guard BEFORE INSERT ON translation_requests
WHEN NOT EXISTS(SELECT 1 FROM translation_requests WHERE user_id=NEW.user_id AND request_id=NEW.request_id)
BEGIN
 SELECT CASE WHEN NEW.reserved > COALESCE((SELECT MAX(0,chars_quota-chars_used+COALESCE((SELECT SUM(x.amount) FROM addon_allocations x JOIN translation_requests r ON r.user_id=x.user_id AND r.request_id=x.request_id WHERE x.user_id=NEW.user_id AND r.year_month=NEW.year_month),0)) FROM usage_monthly WHERE user_id=NEW.user_id AND year_month=NEW.year_month),0)+COALESCE((SELECT SUM(remaining) FROM addon_balances WHERE user_id=NEW.user_id AND julianday(expires_at)>julianday('now')),0) THEN RAISE(ABORT,'quota_exceeded') END;
END;
CREATE TRIGGER translation_reserve AFTER INSERT ON translation_requests BEGIN
 INSERT INTO addon_allocations(user_id,request_id,order_no,reserved,amount)
 SELECT NEW.user_id,NEW.request_id,order_no,MIN(remaining,MAX(0,spill-prior)),MIN(remaining,MAX(0,spill-prior)) FROM (
 SELECT b.order_no,b.remaining,COALESCE(SUM(b.remaining) OVER(ORDER BY b.expires_at,b.order_no ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING),0) prior,
 NEW.reserved-MAX(0,u.chars_quota-u.chars_used+COALESCE((SELECT SUM(x.amount) FROM addon_allocations x JOIN translation_requests r ON r.user_id=x.user_id AND r.request_id=x.request_id WHERE x.user_id=NEW.user_id AND r.year_month=NEW.year_month),0)) spill
 FROM addon_balances b JOIN usage_monthly u ON u.user_id=b.user_id AND u.year_month=NEW.year_month
 WHERE b.user_id=NEW.user_id AND b.remaining>0 AND julianday(b.expires_at)>julianday('now')) WHERE spill>prior;
 UPDATE usage_monthly SET chars_used=chars_used+NEW.reserved WHERE user_id=NEW.user_id AND year_month=NEW.year_month;
END;
CREATE TRIGGER translation_settle AFTER UPDATE OF state ON translation_requests WHEN OLD.state='reserved' AND NEW.state='settled' BEGIN
 UPDATE addon_allocations SET amount=MIN(reserved,MAX(0,NEW.billed-(OLD.reserved-(SELECT COALESCE(SUM(reserved),0) FROM addon_allocations WHERE user_id=NEW.user_id AND request_id=NEW.request_id))-
 (SELECT COALESCE(SUM(x.reserved),0) FROM addon_allocations x JOIN quota_addons a ON a.order_no=x.order_no JOIN quota_addons b ON b.order_no=addon_allocations.order_no WHERE x.user_id=NEW.user_id AND x.request_id=NEW.request_id AND (a.expires_at<b.expires_at OR (a.expires_at=b.expires_at AND a.order_no<b.order_no))))) WHERE user_id=NEW.user_id AND request_id=NEW.request_id;
 UPDATE usage_monthly SET chars_used=chars_used-OLD.reserved+NEW.billed,task_count=task_count+CASE WHEN NEW.billed>0 THEN 1 ELSE 0 END WHERE user_id=NEW.user_id AND year_month=NEW.year_month;
END;
-- Device binding age is reset only on reactivation, not on routine login.
CREATE TRIGGER IF NOT EXISTS app_binding_age_guard BEFORE UPDATE OF revoked ON app_device_bindings
WHEN OLD.revoked=0 AND NEW.revoked=1 AND (julianday(OLD.first_seen) IS NULL OR julianday('now')<=julianday(OLD.first_seen)+20)
BEGIN SELECT RAISE(ABORT,'device_binding_locked'); END;
CREATE TRIGGER IF NOT EXISTS app_binding_age_reset AFTER UPDATE OF revoked ON app_device_bindings
WHEN OLD.revoked<>0 AND NEW.revoked=0 BEGIN
 UPDATE app_device_bindings SET first_seen=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE user_id=NEW.user_id AND device_id=NEW.device_id;
END;
-- Manual Free/Pro adjustments use the configured quota and discard previous paid snapshots.
DROP TRIGGER IF EXISTS admin_membership_apply;
CREATE TRIGGER admin_membership_apply AFTER INSERT ON admin_membership_changes BEGIN
 INSERT INTO subscriptions(user_id,plan_name,starts_at,expires_at,auto_renew,updated_at)
 VALUES(NEW.user_id,NEW.plan_name,CASE WHEN NEW.plan_name='pro' THEN NEW.created_at ELSE NULL END,NEW.expires_at,0,NEW.created_at)
 ON CONFLICT(user_id) DO UPDATE SET plan_name=excluded.plan_name,starts_at=excluded.starts_at,expires_at=excluded.expires_at,auto_renew=0,updated_at=excluded.updated_at;
 INSERT INTO subscription_quotas SELECT NEW.user_id,quota FROM plan_catalog WHERE id=NEW.plan_name ON CONFLICT(user_id) DO UPDATE SET quota=excluded.quota;
 UPDATE usage_monthly SET chars_quota=(SELECT quota FROM plan_catalog WHERE id=CASE WHEN NEW.plan_name='pro' AND NEW.expires_at>NEW.created_at THEN 'pro' ELSE 'free' END) WHERE user_id=NEW.user_id AND year_month=substr(NEW.created_at,1,7);
END;

-- Additive, administrator membership changes only. Payment records and consumption remain untouched.
CREATE TABLE IF NOT EXISTS admin_membership_changes_v2 (
 id TEXT PRIMARY KEY NOT NULL,
 user_id TEXT NOT NULL REFERENCES users(id),
 actor TEXT NOT NULL,
 before_snapshot TEXT NOT NULL,
 plan_name TEXT NOT NULL CHECK(plan_name IN ('free','pro','max')),
 expires_at TEXT,
 reason TEXT NOT NULL CHECK(length(reason) BETWEEN 5 AND 500),
 created_at TEXT NOT NULL,
 CHECK((plan_name='free' AND expires_at IS NULL) OR (plan_name IN ('pro','max') AND expires_at IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS idx_admin_membership_user_v2 ON admin_membership_changes_v2(user_id,created_at DESC,id DESC);
INSERT OR IGNORE INTO admin_membership_changes_v2 SELECT * FROM admin_membership_changes WHERE NOT EXISTS(SELECT 1 FROM admin_membership_changes_v2 v WHERE v.id=admin_membership_changes.id);
CREATE TRIGGER IF NOT EXISTS admin_membership_validate_v2 BEFORE INSERT ON admin_membership_changes_v2
BEGIN
 SELECT CASE WHEN NEW.before_snapshot <> COALESCE((SELECT json_array(plan_name,starts_at,expires_at,auto_renew,updated_at) FROM subscriptions WHERE user_id=NEW.user_id),'null') THEN RAISE(ABORT,'membership_conflict') END;
END;
CREATE TRIGGER IF NOT EXISTS admin_membership_apply_v2 AFTER INSERT ON admin_membership_changes_v2
BEGIN
 INSERT INTO subscriptions(user_id,plan_name,starts_at,expires_at,auto_renew,updated_at)
 VALUES(NEW.user_id,NEW.plan_name,CASE WHEN NEW.plan_name IN ('pro','max') THEN NEW.created_at ELSE NULL END,NEW.expires_at,0,NEW.created_at)
 ON CONFLICT(user_id) DO UPDATE SET plan_name=excluded.plan_name,
 starts_at=CASE WHEN excluded.plan_name='free' THEN NULL WHEN subscriptions.plan_name IN ('pro','max') THEN COALESCE(subscriptions.starts_at,excluded.starts_at) ELSE excluded.starts_at END,
 expires_at=excluded.expires_at,auto_renew=0,updated_at=excluded.updated_at;
 INSERT INTO subscription_quotas SELECT NEW.user_id,quota FROM plan_catalog WHERE id=NEW.plan_name ON CONFLICT(user_id) DO UPDATE SET quota=excluded.quota;
 UPDATE usage_monthly SET chars_quota=(SELECT quota FROM plan_catalog WHERE id=CASE WHEN NEW.plan_name IN ('pro','max') AND NEW.expires_at>NEW.created_at THEN NEW.plan_name ELSE 'free' END) WHERE user_id=NEW.user_id AND year_month=substr(NEW.created_at,1,7);
END;
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


-- Explicit current-UTC-month compensation, never alters historical charges or orders.
CREATE TABLE IF NOT EXISTS quota_compensations (
 id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id), year_month TEXT NOT NULL,
 amount INTEGER NOT NULL CHECK(amount BETWEEN 1 AND 1000000000), actor TEXT NOT NULL,
 reason TEXT NOT NULL CHECK(length(reason) BETWEEN 5 AND 500), created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_quota_compensation_user ON quota_compensations(user_id,year_month);

-- Optional display copy; product IDs and purchased snapshots remain unchanged.
ALTER TABLE plan_catalog ADD COLUMN description TEXT NOT NULL DEFAULT '';
ALTER TABLE plan_changes ADD COLUMN display_name TEXT;
ALTER TABLE plan_changes ADD COLUMN description TEXT;
DROP TRIGGER IF EXISTS plan_change_apply;
CREATE TRIGGER plan_change_apply AFTER INSERT ON plan_changes BEGIN
 UPDATE plan_catalog SET name=COALESCE(NEW.display_name,name),description=COALESCE(NEW.description,description),price_cents=NEW.price_cents,quota=NEW.quota,duration_days=NEW.duration_days,enabled=NEW.enabled,revision=revision+1,updated_at=NEW.created_at WHERE id=NEW.plan_id;
 UPDATE plans SET name=COALESCE(NEW.display_name,name),price_cents=NEW.price_cents,duration_days=NEW.duration_days,enabled=NEW.enabled WHERE id=NEW.plan_id AND id<>'free';
END;

-- Only hashes of short-lived administrator session tokens; never stores the admin key.
CREATE TABLE IF NOT EXISTS admin_sessions (
 token_hash TEXT PRIMARY KEY, key_fingerprint TEXT NOT NULL, created_at INTEGER NOT NULL, expires_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_admin_sessions_expiry ON admin_sessions(expires_at);


-- Additive short-reason ledgers. Originals retained; copy history before creating apply triggers.
CREATE TABLE plan_changes_v3 (
 id TEXT PRIMARY KEY, actor TEXT NOT NULL, plan_id TEXT NOT NULL REFERENCES plan_catalog(id),
 before_revision INTEGER NOT NULL, price_cents INTEGER NOT NULL, quota INTEGER NOT NULL,
 duration_days INTEGER NOT NULL, enabled INTEGER NOT NULL, reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 500),
 before_snapshot TEXT NOT NULL, created_at TEXT NOT NULL
, display_name TEXT, description TEXT);
INSERT INTO plan_changes_v3 SELECT * FROM plan_changes;
CREATE TRIGGER plan_change_guard_v3 BEFORE INSERT ON plan_changes_v3 BEGIN
 SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM plan_catalog WHERE id=NEW.plan_id AND revision=NEW.before_revision)
 THEN RAISE(ABORT,'plan_conflict') END;
END;
CREATE TRIGGER plan_change_apply_v3 AFTER INSERT ON plan_changes_v3 BEGIN
 UPDATE plan_catalog SET name=COALESCE(NEW.display_name,name),description=COALESCE(NEW.description,description),price_cents=NEW.price_cents,quota=NEW.quota,duration_days=NEW.duration_days,enabled=NEW.enabled,revision=revision+1,updated_at=NEW.created_at WHERE id=NEW.plan_id;
 UPDATE plans SET name=COALESCE(NEW.display_name,name),price_cents=NEW.price_cents,duration_days=NEW.duration_days,enabled=NEW.enabled WHERE id=NEW.plan_id AND id<>'free';
END;
CREATE TABLE admin_membership_changes_v3 (
 id TEXT PRIMARY KEY NOT NULL,
 user_id TEXT NOT NULL REFERENCES users(id),
 actor TEXT NOT NULL,
 before_snapshot TEXT NOT NULL,
 plan_name TEXT NOT NULL CHECK(plan_name IN ('free','pro','max')),
 expires_at TEXT,
 reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 500),
 created_at TEXT NOT NULL,
 CHECK((plan_name='free' AND expires_at IS NULL) OR (plan_name IN ('pro','max') AND expires_at IS NOT NULL))
);
INSERT INTO admin_membership_changes_v3 SELECT * FROM admin_membership_changes_v2;
CREATE INDEX idx_admin_membership_user_v2_v3 ON admin_membership_changes_v3(user_id,created_at DESC,id DESC);
CREATE TRIGGER admin_membership_validate_v2_v3 BEFORE INSERT ON admin_membership_changes_v3
BEGIN
 SELECT CASE WHEN NEW.before_snapshot <> COALESCE((SELECT json_array(plan_name,starts_at,expires_at,auto_renew,updated_at) FROM subscriptions WHERE user_id=NEW.user_id),'null') THEN RAISE(ABORT,'membership_conflict') END;
END;
CREATE TRIGGER admin_membership_apply_v2_v3 AFTER INSERT ON admin_membership_changes_v3
BEGIN
 INSERT INTO subscriptions(user_id,plan_name,starts_at,expires_at,auto_renew,updated_at)
 VALUES(NEW.user_id,NEW.plan_name,CASE WHEN NEW.plan_name IN ('pro','max') THEN NEW.created_at ELSE NULL END,NEW.expires_at,0,NEW.created_at)
 ON CONFLICT(user_id) DO UPDATE SET plan_name=excluded.plan_name,
 starts_at=CASE WHEN excluded.plan_name='free' THEN NULL WHEN subscriptions.plan_name IN ('pro','max') THEN COALESCE(subscriptions.starts_at,excluded.starts_at) ELSE excluded.starts_at END,
 expires_at=excluded.expires_at,auto_renew=0,updated_at=excluded.updated_at;
 INSERT INTO subscription_quotas SELECT NEW.user_id,quota FROM plan_catalog WHERE id=NEW.plan_name ON CONFLICT(user_id) DO UPDATE SET quota=excluded.quota;
 UPDATE usage_monthly SET chars_quota=(SELECT quota FROM plan_catalog WHERE id=CASE WHEN NEW.plan_name IN ('pro','max') AND NEW.expires_at>NEW.created_at THEN NEW.plan_name ELSE 'free' END) WHERE user_id=NEW.user_id AND year_month=substr(NEW.created_at,1,7);
END;
CREATE TABLE quota_compensations_v3 (
 id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id), year_month TEXT NOT NULL,
 amount INTEGER NOT NULL CHECK(amount BETWEEN 1 AND 1000000000), actor TEXT NOT NULL,
 reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 500), created_at TEXT NOT NULL
);
INSERT INTO quota_compensations_v3 SELECT * FROM quota_compensations;
CREATE INDEX idx_quota_compensation_user_v3 ON quota_compensations_v3(user_id,year_month);

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

-- Reversible account administration; financial records and paid subscriptions are untouched.
CREATE TABLE IF NOT EXISTS user_admin_state (
 user_id TEXT PRIMARY KEY REFERENCES users(id), deleted_at TEXT, is_super INTEGER NOT NULL DEFAULT 0 CHECK(is_super IN (0,1)), revision INTEGER NOT NULL DEFAULT 0
);
CREATE VIEW IF NOT EXISTS admin_account_snapshot AS SELECT u.id,u.is_active,s.deleted_at,COALESCE(s.is_super,0) is_super,
 json_array(u.is_active,s.deleted_at,COALESCE(s.is_super,0),COALESCE(s.revision,0)) version FROM users u LEFT JOIN user_admin_state s ON s.user_id=u.id;
CREATE TABLE IF NOT EXISTS admin_account_changes (
 id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id), actor TEXT NOT NULL, before_snapshot TEXT NOT NULL,
 action TEXT NOT NULL CHECK(action IN ('disable','enable','delete','restore','grant_super','revoke_super','revoke_sessions')),
 reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 500), created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_admin_account_history ON admin_account_changes(user_id,created_at);
CREATE TRIGGER IF NOT EXISTS admin_account_guard BEFORE INSERT ON admin_account_changes BEGIN
 SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM admin_account_snapshot WHERE id=NEW.user_id AND version=NEW.before_snapshot) THEN RAISE(ABORT,'account_conflict') END;
 SELECT CASE WHEN (NEW.action='restore' AND (SELECT deleted_at FROM admin_account_snapshot WHERE id=NEW.user_id) IS NULL)
 OR (NEW.action<>'restore' AND (SELECT deleted_at FROM admin_account_snapshot WHERE id=NEW.user_id) IS NOT NULL) THEN RAISE(ABORT,'account_state_invalid') END;
 SELECT CASE WHEN NEW.action='grant_super' AND NOT EXISTS(SELECT 1 FROM plan_catalog WHERE id='max' AND quota>0) THEN RAISE(ABORT,'max_quota_missing') END;
END;
CREATE TRIGGER IF NOT EXISTS admin_account_apply AFTER INSERT ON admin_account_changes BEGIN
 INSERT INTO user_admin_state(user_id) VALUES(NEW.user_id) ON CONFLICT(user_id) DO NOTHING;
 UPDATE user_admin_state SET revision=revision+1,
 deleted_at=CASE WHEN NEW.action='delete' THEN NEW.created_at WHEN NEW.action='restore' THEN NULL ELSE deleted_at END,
 is_super=CASE WHEN NEW.action='grant_super' THEN 1 WHEN NEW.action='revoke_super' THEN 0 ELSE is_super END WHERE user_id=NEW.user_id;
 UPDATE users SET is_active=CASE WHEN NEW.action IN ('disable','delete','restore') THEN 0 WHEN NEW.action='enable' THEN 1 ELSE is_active END WHERE id=NEW.user_id;
 UPDATE sessions SET revoked_at=NEW.created_at WHERE user_id=NEW.user_id AND revoked_at IS NULL AND NEW.action IN ('disable','delete','restore','revoke_sessions');
END;
-- Reject new work after account deactivation.
CREATE TRIGGER IF NOT EXISTS admin_account_reserve_guard BEFORE INSERT ON translation_requests
WHEN NOT EXISTS(SELECT 1 FROM translation_requests WHERE user_id=NEW.user_id AND request_id=NEW.request_id)
BEGIN
 SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM users WHERE id=NEW.user_id AND is_active=1) THEN RAISE(ABORT,'account_inactive') END;
END;

-- Recompute for managed accounts inside the same reservation statement, not a stale HTTP snapshot.
DROP TRIGGER IF EXISTS translation_reserve_guard;
CREATE TRIGGER translation_reserve_guard BEFORE INSERT ON translation_requests
WHEN NOT EXISTS(SELECT 1 FROM translation_requests WHERE user_id=NEW.user_id AND request_id=NEW.request_id)
BEGIN
 UPDATE usage_monthly SET chars_quota=COALESCE((SELECT CASE WHEN COALESCE(a.is_super,0)=1 THEN (SELECT quota FROM plan_catalog WHERE id='max') WHEN s.plan_name IN ('pro','max') AND (s.expires_at IS NULL OR s.expires_at>strftime('%Y-%m-%dT%H:%M:%fZ','now')) THEN COALESCE(q.quota,(SELECT quota FROM plan_catalog WHERE id=s.plan_name)) ELSE (SELECT quota FROM plan_catalog WHERE id='free') END FROM users u LEFT JOIN user_admin_state a ON a.user_id=u.id LEFT JOIN subscriptions s ON s.user_id=u.id LEFT JOIN subscription_quotas q ON q.user_id=u.id WHERE u.id=NEW.user_id),0)+COALESCE((SELECT SUM(amount) FROM quota_compensations_v3 WHERE user_id=NEW.user_id AND year_month=NEW.year_month),0)
 WHERE user_id=NEW.user_id AND year_month=NEW.year_month AND EXISTS(SELECT 1 FROM user_admin_state WHERE user_id=NEW.user_id);
 SELECT CASE WHEN NEW.reserved > COALESCE((SELECT MAX(0,chars_quota-chars_used+COALESCE((SELECT SUM(x.amount) FROM addon_allocations x JOIN translation_requests r ON r.user_id=x.user_id AND r.request_id=x.request_id WHERE x.user_id=NEW.user_id AND r.year_month=NEW.year_month),0)) FROM usage_monthly WHERE user_id=NEW.user_id AND year_month=NEW.year_month),0)+COALESCE((SELECT SUM(remaining) FROM addon_balances WHERE user_id=NEW.user_id AND julianday(expires_at)>julianday('now')),0) THEN RAISE(ABORT,'quota_exceeded') END;
END;

-- Immutable task billing snapshot and cumulative integer rounding across chunks/retries.
CREATE TABLE translation_billing_tasks (
 user_id TEXT NOT NULL REFERENCES users(id), task_id TEXT NOT NULL,
 mode TEXT NOT NULL CHECK(mode IN ('offline','online')),
 percent INTEGER NOT NULL CHECK(percent BETWEEN 1 AND 100),
 original_chars INTEGER NOT NULL DEFAULT 0 CHECK(original_chars>=0),
 PRIMARY KEY(user_id,task_id)
);
ALTER TABLE translation_requests ADD COLUMN billing_task_id TEXT;
ALTER TABLE translation_requests ADD COLUMN original_chars INTEGER NOT NULL DEFAULT 0;
CREATE TRIGGER translation_billing_accumulate AFTER UPDATE OF state ON translation_requests
WHEN OLD.state='reserved' AND NEW.state='settled' AND NEW.billing_task_id IS NOT NULL
BEGIN
 UPDATE translation_billing_tasks SET original_chars=original_chars+NEW.original_chars
 WHERE user_id=NEW.user_id AND task_id=NEW.billing_task_id;
END;

CREATE TABLE numeric_captchas (
 id TEXT PRIMARY KEY, binding TEXT NOT NULL, answer_hash TEXT NOT NULL,
 expires_at INTEGER NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, used INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX numeric_captcha_expiry ON numeric_captchas(expires_at);

-- Server-side OpenAI-compatible provider routing. Provider credentials are AES-GCM
-- ciphertext; the encryption key remains a Worker Secret and is never stored in D1.
CREATE TABLE IF NOT EXISTS ai_providers (
 id TEXT PRIMARY KEY,
 name TEXT NOT NULL CHECK(length(name) BETWEEN 1 AND 80),
 base_url TEXT NOT NULL CHECK(length(base_url) BETWEEN 8 AND 2048),
 model TEXT NOT NULL CHECK(length(model) BETWEEN 1 AND 200),
 credential_ciphertext TEXT,
 credential_nonce TEXT,
 secret_name TEXT,
 enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)),
 weight INTEGER NOT NULL DEFAULT 100 CHECK(weight BETWEEN 1 AND 1000),
 priority INTEGER NOT NULL DEFAULT 100 CHECK(priority BETWEEN 1 AND 1000),
 timeout_ms INTEGER NOT NULL DEFAULT 90000 CHECK(timeout_ms BETWEEN 5000 AND 120000),
 max_failures INTEGER NOT NULL DEFAULT 3 CHECK(max_failures BETWEEN 1 AND 20),
 cooldown_seconds INTEGER NOT NULL DEFAULT 60 CHECK(cooldown_seconds BETWEEN 5 AND 3600),
 temperature REAL NOT NULL DEFAULT 0.1 CHECK(temperature BETWEEN 0 AND 2),
 revision INTEGER NOT NULL DEFAULT 1 CHECK(revision > 0),
 created_at TEXT NOT NULL,
 updated_at TEXT NOT NULL,
 CHECK((credential_ciphertext IS NULL)=(credential_nonce IS NULL)),
 CHECK(NOT(credential_ciphertext IS NOT NULL AND secret_name IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS idx_ai_providers_route ON ai_providers(enabled,priority,id);
CREATE TABLE IF NOT EXISTS ai_provider_health (
 provider_id TEXT PRIMARY KEY REFERENCES ai_providers(id) ON DELETE CASCADE,
 consecutive_failures INTEGER NOT NULL DEFAULT 0 CHECK(consecutive_failures >= 0),
 last_success_at TEXT,
 last_failure_at TEXT,
 last_latency_ms INTEGER,
 cooldown_until TEXT,
 last_error_code TEXT,
 updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS ai_prompt_policies (
 id TEXT PRIMARY KEY,
 version TEXT NOT NULL UNIQUE,
 name TEXT NOT NULL,
 system_prompt TEXT NOT NULL CHECK(length(system_prompt) BETWEEN 40 AND 20000),
 published INTEGER NOT NULL DEFAULT 0 CHECK(published IN (0,1)),
 created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS ai_routing_profiles (
 id TEXT PRIMARY KEY,
 name TEXT NOT NULL,
 prompt_policy_id TEXT NOT NULL REFERENCES ai_prompt_policies(id),
 context_version TEXT NOT NULL UNIQUE,
 active INTEGER NOT NULL DEFAULT 0 CHECK(active IN (0,1)),
 created_at TEXT NOT NULL,
 updated_at TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_ai_routing_one_active ON ai_routing_profiles(active) WHERE active=1;
CREATE TABLE IF NOT EXISTS ai_config_changes (
 id TEXT PRIMARY KEY,
 kind TEXT NOT NULL,
 target_id TEXT NOT NULL,
 actor TEXT NOT NULL,
 before_json TEXT,
 after_json TEXT,
 reason TEXT NOT NULL,
 created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_ai_config_changes_time ON ai_config_changes(created_at DESC,id DESC);
-- Directed user notifications (migration 0023_user_notifications.sql). Kept out of
-- operation_settings: the `content` section is a strict equality whitelist and a per-user
-- directed message has different semantics from the fleet-wide announcement.
CREATE TABLE IF NOT EXISTS notifications (
 id TEXT PRIMARY KEY,
 request_id TEXT NOT NULL UNIQUE,
 title TEXT NOT NULL CHECK(length(title) BETWEEN 1 AND 120),
 body TEXT NOT NULL CHECK(length(body) BETWEEN 1 AND 2000),
 actor TEXT NOT NULL,
 reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 500),
 created_at TEXT NOT NULL,
 updated_at TEXT NOT NULL,
 expires_at TEXT,
 withdrawn_at TEXT,
 withdrawn_actor TEXT,
 revision INTEGER NOT NULL DEFAULT 1 CHECK(revision > 0)
);
CREATE INDEX IF NOT EXISTS idx_notifications_feed ON notifications(created_at DESC,id DESC);
CREATE TABLE IF NOT EXISTS notification_recipients (
 notification_id TEXT NOT NULL REFERENCES notifications(id) ON DELETE CASCADE,
 user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
 read_at TEXT,
 created_at TEXT NOT NULL,
 PRIMARY KEY(notification_id,user_id)
);
CREATE INDEX IF NOT EXISTS idx_notification_recipients_user ON notification_recipients(user_id,read_at,notification_id);
CREATE TABLE IF NOT EXISTS notification_changes (
 id TEXT PRIMARY KEY,
 notification_id TEXT NOT NULL REFERENCES notifications(id) ON DELETE CASCADE,
 actor TEXT NOT NULL,
 action TEXT NOT NULL CHECK(action IN ('edit','withdraw')),
 before_revision INTEGER NOT NULL CHECK(before_revision > 0),
 before_snapshot TEXT NOT NULL CHECK(json_valid(before_snapshot)),
 after_value TEXT NOT NULL CHECK(json_valid(after_value)),
 reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 500),
 created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_notification_changes_target ON notification_changes(notification_id,created_at DESC,id DESC);
CREATE TRIGGER IF NOT EXISTS notification_change_guard BEFORE INSERT ON notification_changes BEGIN
 SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM notifications n WHERE n.id=NEW.notification_id AND n.revision=NEW.before_revision
  AND NEW.before_snapshot=json_object('title',n.title,'body',n.body,'expires_at',n.expires_at,'withdrawn_at',n.withdrawn_at))
 THEN RAISE(ABORT,'notification_conflict') END;
END;
CREATE TRIGGER IF NOT EXISTS notification_change_apply AFTER INSERT ON notification_changes BEGIN
 UPDATE notifications SET title=json_extract(NEW.after_value,'$.title'),
  body=json_extract(NEW.after_value,'$.body'),
  expires_at=json_extract(NEW.after_value,'$.expires_at'),
  withdrawn_at=json_extract(NEW.after_value,'$.withdrawn_at'),
  withdrawn_actor=CASE WHEN json_extract(NEW.after_value,'$.withdrawn_at') IS NULL THEN withdrawn_actor ELSE NEW.actor END,
  updated_at=NEW.created_at,revision=revision+1 WHERE id=NEW.notification_id;
END;
