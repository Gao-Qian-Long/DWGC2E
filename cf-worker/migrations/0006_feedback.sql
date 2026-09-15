CREATE TABLE IF NOT EXISTS feedback (id TEXT PRIMARY KEY, email TEXT NOT NULL, category TEXT NOT NULL, message TEXT NOT NULL, page TEXT NOT NULL DEFAULT '', status TEXT NOT NULL DEFAULT 'new' CHECK(status IN ('new','resolved')), created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_feedback_created ON feedback(created_at DESC,id DESC);
