-- Preserve all orders, settlement records and entitlement application. Replace only settlement guard.
DROP TRIGGER IF EXISTS payment_settlement_guard;
DROP TRIGGER IF EXISTS payment_settlement_bind;
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
