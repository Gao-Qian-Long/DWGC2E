CREATE TABLE numeric_captchas (
 id TEXT PRIMARY KEY, binding TEXT NOT NULL, answer_hash TEXT NOT NULL,
 expires_at INTEGER NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, used INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX numeric_captcha_expiry ON numeric_captchas(expires_at);
