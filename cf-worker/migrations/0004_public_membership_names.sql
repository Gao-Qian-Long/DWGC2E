-- Public sale labels only. Keep stable product IDs and preserve all prices/entitlements.
-- Existing order snapshots are intentionally not modified.
UPDATE plans SET name='Pro 7 天 A' WHERE id='test_pro_019' AND name='Pro 7 天测试 A';
UPDATE plans SET name='Pro 7 天 B' WHERE id='test_pro_029' AND name='Pro 7 天测试 B';
