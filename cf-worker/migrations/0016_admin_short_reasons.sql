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
