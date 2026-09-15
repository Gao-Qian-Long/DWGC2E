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

