-- S8: directed user notifications (administrator -> selected users).
-- Additive and idempotent, in the discipline of 0022_plan_price_correction.sql: this file only
-- creates new objects (`IF NOT EXISTS`) and re-creates two triggers with identical bodies, so a
-- second application is a no-op. It never touches orders, payments, usage or user rows, never
-- rewrites operation_settings, and never deletes data. Notifications are deliberately kept OUT of
-- the operation_settings `content` section: that section is a strict equality whitelist
-- (src/admin/operations.ts:17) and a fleet-wide announcement has different semantics from a
-- per-user directed message.

-- 1) Envelope. `request_id` is the administrator's uuid idempotency key; UNIQUE is what makes a
--    retried send collapse onto the original row instead of duplicating it.
--    `withdrawn_at` is the recall state: a recalled notification stays in the audit trail but is
--    never returned to a recipient again. `expires_at` mirrors the site-wide announcement window
--    (src/admin/operations.ts:24-26): outside the window the notification is not returned.
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

-- 2) Recipient rows. The per-user read receipt lives here: `read_at IS NULL` means unread and the
--    composite primary key makes a read receipt structurally idempotent (one row per user per
--    notification, written once and never overwritten while it is non-null).
CREATE TABLE IF NOT EXISTS notification_recipients (
 notification_id TEXT NOT NULL REFERENCES notifications(id) ON DELETE CASCADE,
 user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
 read_at TEXT,
 created_at TEXT NOT NULL,
 PRIMARY KEY(notification_id,user_id)
);
CREATE INDEX IF NOT EXISTS idx_notification_recipients_user ON notification_recipients(user_id,read_at,notification_id);

-- 3) Optimistic concurrency ledger for edits and recalls, modelled on operation_changes
--    (0012_operations.sql:6-17). The BEFORE INSERT guard refuses any write whose revision/snapshot
--    no longer matches the live row; the AFTER INSERT trigger is the only path that mutates the
--    envelope, so a concurrent edit can never be silently overwritten.
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

DROP TRIGGER IF EXISTS notification_change_guard;
CREATE TRIGGER notification_change_guard BEFORE INSERT ON notification_changes BEGIN
 SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM notifications n WHERE n.id=NEW.notification_id AND n.revision=NEW.before_revision
  AND NEW.before_snapshot=json_object('title',n.title,'body',n.body,'expires_at',n.expires_at,'withdrawn_at',n.withdrawn_at))
 THEN RAISE(ABORT,'notification_conflict') END;
END;

DROP TRIGGER IF EXISTS notification_change_apply;
CREATE TRIGGER notification_change_apply AFTER INSERT ON notification_changes BEGIN
 UPDATE notifications SET title=json_extract(NEW.after_value,'$.title'),
  body=json_extract(NEW.after_value,'$.body'),
  expires_at=json_extract(NEW.after_value,'$.expires_at'),
  withdrawn_at=json_extract(NEW.after_value,'$.withdrawn_at'),
  withdrawn_actor=CASE WHEN json_extract(NEW.after_value,'$.withdrawn_at') IS NULL THEN withdrawn_actor ELSE NEW.actor END,
  updated_at=NEW.created_at,revision=revision+1 WHERE id=NEW.notification_id;
END;
