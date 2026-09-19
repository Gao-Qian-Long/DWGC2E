-- P0-5 / P2-16 / P3-27 / P3-28: repair the historical seed values and rebuild the settlement
-- trigger. Additive and idempotent: every statement is guarded by the exact seed residue it
-- repairs, so a price or availability an operator configured in the admin console is never
-- overwritten. On the production database (pro=3900, max=5900/enabled/8,000,000, both already
-- edited in the console) every guard below is false and this migration only re-creates the trigger.

-- 1) `max` was seeded disabled with a zero quota. Super administrators take their entitlement from
--    this row and `grant_super` refuses to run without it, so the tier was unusable out of the box.
--    The guard is the untouched seed row (revision 1, both fields still zero), never a state an
--    administrator produced: availability itself is left to the console.
UPDATE plan_catalog SET quota=8000000,enabled=1,revision=revision+1,updated_at=strftime('%Y-%m-%dT%H:%M:%fZ','now')
 WHERE id='max' AND quota=0 AND enabled=0 AND revision=1;

-- 2) Seed residue. `pro` was seeded at 2 cents and `max` at 3 cents: the checkout path charges the
--    plan price verbatim, so a database initialised from those seeds sold Pro for 0.02 CNY.
UPDATE plan_catalog SET price_cents=3900,revision=revision+1,updated_at=strftime('%Y-%m-%dT%H:%M:%fZ','now')
 WHERE id='pro' AND price_cents=2;
UPDATE plan_catalog SET price_cents=5900,revision=revision+1,updated_at=strftime('%Y-%m-%dT%H:%M:%fZ','now')
 WHERE id='max' AND price_cents=3;

-- 3) The legacy FK table must keep matching the catalog exactly: orders snapshot `plans.price_cents`
--    and the catalog trigger rejects any order whose amount is not the catalog price. Only seed
--    residue is repaired here, and only the money field.
UPDATE plans SET price_cents=3900,duration_days=30 WHERE id='pro' AND price_cents=2;
UPDATE plans SET price_cents=5900,duration_days=30 WHERE id='max' AND price_cents=3;

-- 4) Settlement application: keep the customer's remaining paid time when the tier changes, and
--    refuse to settle a tier that has no quota mapping instead of granting a hardcoded 1,000,000.
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
