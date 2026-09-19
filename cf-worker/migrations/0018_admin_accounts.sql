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
