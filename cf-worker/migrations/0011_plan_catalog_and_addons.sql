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
 INSERT INTO subscriptions(user_id,plan_name,starts_at,expires_at,auto_renew,updated_at)
 SELECT user_id,membership_level,NEW.settled_at,strftime('%Y-%m-%dT%H:%M:%fZ',NEW.settled_at,'+'||duration_days||' days'),0,NEW.settled_at
 FROM orders WHERE order_no=NEW.order_no AND membership_level<>'go'
 ON CONFLICT(user_id) DO UPDATE SET plan_name=excluded.plan_name,
 starts_at=CASE WHEN subscriptions.plan_name=excluded.plan_name AND julianday(subscriptions.expires_at)>julianday(NEW.settled_at) THEN subscriptions.starts_at ELSE NEW.settled_at END,
 expires_at=strftime('%Y-%m-%dT%H:%M:%fZ',CASE WHEN subscriptions.plan_name=excluded.plan_name AND julianday(subscriptions.expires_at)>julianday(NEW.settled_at) THEN subscriptions.expires_at ELSE NEW.settled_at END,'+'||(SELECT duration_days FROM orders WHERE order_no=NEW.order_no)||' days'),auto_renew=0,updated_at=NEW.settled_at;
 INSERT INTO subscription_quotas(user_id,quota)
 SELECT o.user_id,COALESCE(s.quota,1000000) FROM orders o LEFT JOIN order_entitlements s ON s.order_no=o.order_no WHERE o.order_no=NEW.order_no AND o.membership_level<>'go'
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
