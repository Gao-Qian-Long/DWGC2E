-- Explicit current-UTC-month compensation, never alters historical charges or orders.
CREATE TABLE IF NOT EXISTS quota_compensations (
 id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id), year_month TEXT NOT NULL,
 amount INTEGER NOT NULL CHECK(amount BETWEEN 1 AND 1000000000), actor TEXT NOT NULL,
 reason TEXT NOT NULL CHECK(length(reason) BETWEEN 5 AND 500), created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_quota_compensation_user ON quota_compensations(user_id,year_month);
