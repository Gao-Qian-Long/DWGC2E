-- Optional display copy; product IDs and purchased snapshots remain unchanged.
ALTER TABLE plan_catalog ADD COLUMN description TEXT NOT NULL DEFAULT '';
ALTER TABLE plan_changes ADD COLUMN display_name TEXT;
ALTER TABLE plan_changes ADD COLUMN description TEXT;
DROP TRIGGER IF EXISTS plan_change_apply;
CREATE TRIGGER plan_change_apply AFTER INSERT ON plan_changes BEGIN
 UPDATE plan_catalog SET name=COALESCE(NEW.display_name,name),description=COALESCE(NEW.description,description),price_cents=NEW.price_cents,quota=NEW.quota,duration_days=NEW.duration_days,enabled=NEW.enabled,revision=revision+1,updated_at=NEW.created_at WHERE id=NEW.plan_id;
 UPDATE plans SET name=COALESCE(NEW.display_name,name),price_cents=NEW.price_cents,duration_days=NEW.duration_days,enabled=NEW.enabled WHERE id=NEW.plan_id AND id<>'free';
END;
