-- Read-only operational preview. No scheduled deletion is enabled.
SELECT 'expired_sessions' AS category, COUNT(*) AS rows FROM sessions WHERE expires_at < strftime('%Y-%m-%dT%H:%M:%fZ','now');
SELECT 'revoked_sessions' AS category, COUNT(*) AS rows FROM sessions WHERE revoked_at IS NOT NULL;
SELECT 'active_app_bindings' AS category, COUNT(*) AS rows FROM app_device_bindings WHERE revoked=0;
SELECT 'unclassified_legacy_sessions' AS category, COUNT(*) AS rows FROM sessions s WHERE NOT EXISTS(SELECT 1 FROM session_contexts c WHERE c.session_id=s.id);
SELECT user_id, COUNT(*) AS active_bindings FROM app_device_bindings WHERE revoked=0 GROUP BY user_id HAVING COUNT(*)>3;
PRAGMA foreign_key_check;
