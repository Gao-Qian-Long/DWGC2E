-- Server-side OpenAI-compatible provider routing. Provider credentials are AES-GCM
-- ciphertext; the encryption key remains a Worker Secret and is never stored in D1.
CREATE TABLE IF NOT EXISTS ai_providers (
 id TEXT PRIMARY KEY,
 name TEXT NOT NULL CHECK(length(name) BETWEEN 1 AND 80),
 base_url TEXT NOT NULL CHECK(length(base_url) BETWEEN 8 AND 2048),
 model TEXT NOT NULL CHECK(length(model) BETWEEN 1 AND 200),
 credential_ciphertext TEXT,
 credential_nonce TEXT,
 secret_name TEXT,
 enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)),
 weight INTEGER NOT NULL DEFAULT 100 CHECK(weight BETWEEN 1 AND 1000),
 priority INTEGER NOT NULL DEFAULT 100 CHECK(priority BETWEEN 1 AND 1000),
 timeout_ms INTEGER NOT NULL DEFAULT 90000 CHECK(timeout_ms BETWEEN 5000 AND 120000),
 max_failures INTEGER NOT NULL DEFAULT 3 CHECK(max_failures BETWEEN 1 AND 20),
 cooldown_seconds INTEGER NOT NULL DEFAULT 60 CHECK(cooldown_seconds BETWEEN 5 AND 3600),
 temperature REAL NOT NULL DEFAULT 0.1 CHECK(temperature BETWEEN 0 AND 2),
 revision INTEGER NOT NULL DEFAULT 1 CHECK(revision > 0),
 created_at TEXT NOT NULL,
 updated_at TEXT NOT NULL,
 CHECK((credential_ciphertext IS NULL)=(credential_nonce IS NULL)),
 CHECK(NOT(credential_ciphertext IS NOT NULL AND secret_name IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS idx_ai_providers_route ON ai_providers(enabled,priority,id);
CREATE TABLE IF NOT EXISTS ai_provider_health (
 provider_id TEXT PRIMARY KEY REFERENCES ai_providers(id) ON DELETE CASCADE,
 consecutive_failures INTEGER NOT NULL DEFAULT 0 CHECK(consecutive_failures >= 0),
 last_success_at TEXT,
 last_failure_at TEXT,
 last_latency_ms INTEGER,
 cooldown_until TEXT,
 last_error_code TEXT,
 updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS ai_prompt_policies (
 id TEXT PRIMARY KEY,
 version TEXT NOT NULL UNIQUE,
 name TEXT NOT NULL,
 system_prompt TEXT NOT NULL CHECK(length(system_prompt) BETWEEN 40 AND 20000),
 published INTEGER NOT NULL DEFAULT 0 CHECK(published IN (0,1)),
 created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS ai_routing_profiles (
 id TEXT PRIMARY KEY,
 name TEXT NOT NULL,
 prompt_policy_id TEXT NOT NULL REFERENCES ai_prompt_policies(id),
 context_version TEXT NOT NULL UNIQUE,
 active INTEGER NOT NULL DEFAULT 0 CHECK(active IN (0,1)),
 created_at TEXT NOT NULL,
 updated_at TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_ai_routing_one_active ON ai_routing_profiles(active) WHERE active=1;
CREATE TABLE IF NOT EXISTS ai_config_changes (
 id TEXT PRIMARY KEY,
 kind TEXT NOT NULL,
 target_id TEXT NOT NULL,
 actor TEXT NOT NULL,
 before_json TEXT,
 after_json TEXT,
 reason TEXT NOT NULL,
 created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_ai_config_changes_time ON ai_config_changes(created_at DESC,id DESC);
