-- Additive history metadata only. Do not infer languages or precise timestamps for old rows.
ALTER TABLE translation_requests ADD COLUMN source_language TEXT;
ALTER TABLE translation_requests ADD COLUMN target_language TEXT;
ALTER TABLE translation_requests ADD COLUMN started_at TEXT;
ALTER TABLE translation_requests ADD COLUMN completed_at TEXT;
