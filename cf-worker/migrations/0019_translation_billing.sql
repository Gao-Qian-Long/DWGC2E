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
