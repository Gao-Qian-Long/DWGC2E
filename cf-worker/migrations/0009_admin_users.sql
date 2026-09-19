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
