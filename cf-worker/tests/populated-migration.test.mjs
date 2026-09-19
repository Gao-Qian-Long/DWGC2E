import { test } from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { readFileSync, readdirSync } from 'node:fs';

const directory = new URL('../migrations/', import.meta.url);
const migrations = readdirSync(directory).filter(name => name.endsWith('.sql')).sort();
const sql = name => readFileSync(new URL(name, directory), 'utf8');

function populatedDatabase(t) {
  const db = new DatabaseSync(':memory:');
  t.after(() => db.close());
  db.exec('PRAGMA foreign_keys=ON');
  for (const name of migrations.filter(name => name < '0008')) db.exec(sql(name));
  db.exec("INSERT INTO users(id,account,password_hash,created_at) VALUES('upgrade-user','upgrade-fixture','unused','2026-09-01')");
  db.exec("INSERT INTO usage_monthly VALUES('upgrade-user','2026-09',321,100000,2)");
  for (const status of ['paid', 'pending', 'expired', 'failed', 'cancelled', 'refunded']) {
    db.prepare(`INSERT INTO orders(order_no,user_id,plan_id,plan_name,duration_days,membership_level,
      amount_cents,payable_cents,channel,idempotency_key,created_at,expires_at,status,create_state,provider_trade_no)
      VALUES(?,'upgrade-user','test_pro_019','fixture',7,'pro',19,19,'alipay',?,
      '2026-09-01T00:00:00Z','2026-09-02T00:00:00Z',?,'ready',?)`).run(
      status, status, status === 'paid' ? 'pending' : status, 'trade-' + status);
  }
  db.exec("INSERT INTO payment_settlements VALUES('paid','trade-paid',19,'2026-09-01T00:00:00Z')");
  return db;
}

function snapshot(db) {
  return Object.fromEntries(['users', 'orders', 'subscriptions', 'usage_monthly', 'payment_settlements', 'payment_events']
    .map(table => [table, db.prepare(`SELECT * FROM ${table} ORDER BY rowid`).all()]));
}

function upgrade(db) {
  for (const name of migrations.filter(name => name >= '0008' && name < '0011')) db.exec(sql(name));
}

test('0008-0010 preserve populated account, orders, settlements, membership and usage', t => {
  const db = populatedDatabase(t);
  const before = snapshot(db);
  upgrade(db);
  assert.deepEqual(snapshot(db), before);
  // Historical orders are older than the one-day review threshold, so the backfill hands them to
  // the human queue instead of making them due immediately for the next cron sweep.
  assert.deepEqual(db.prepare('SELECT order_no,state,attempts,reason FROM payment_recovery ORDER BY order_no').all()
    .map(row => ({ ...row })), [
    { order_no: 'expired', state: 'manual', attempts: 0, reason: 'legacy_order_requires_review' },
    { order_no: 'pending', state: 'manual', attempts: 0, reason: 'legacy_order_requires_review' },
  ]);
  assert.equal(db.prepare('SELECT COUNT(*) n FROM admin_membership_changes').get().n, 0);
  assert.deepEqual(db.prepare('PRAGMA foreign_key_check').all(), []);
});

test('upgraded populated database rejects conflicting replay and does not grant twice', t => {
  const db = populatedDatabase(t);
  upgrade(db);
  const before = snapshot(db);
  db.exec("INSERT OR IGNORE INTO payment_settlements VALUES('paid','trade-paid',19,'2026-09-03T00:00:00Z')");
  assert.deepEqual(snapshot(db), before);
  assert.throws(() => db.exec("INSERT OR IGNORE INTO payment_settlements VALUES('paid','other-trade',19,'2026-09-03T00:00:00Z')"), /settlement_conflict/);
  assert.deepEqual(snapshot(db), before);
  assert.deepEqual(db.prepare('PRAGMA foreign_key_check').all(), []);
});

for (const status of ['pending', 'expired']) {
  test(`upgraded populated database settles ${status} once and resolves recovery`, t => {
    const db = populatedDatabase(t);
    upgrade(db);
    const before = db.prepare("SELECT * FROM subscriptions WHERE user_id='upgrade-user'").get();
    db.prepare('INSERT INTO payment_settlements VALUES(?,?,19,?)')
      .run(status, 'trade-' + status, '2026-09-02T00:00:00Z');
    const after = db.prepare("SELECT * FROM subscriptions WHERE user_id='upgrade-user'").get();
    assert.equal(Date.parse(after.expires_at) - Date.parse(before.expires_at), 7 * 86400000);
    assert.equal(after.starts_at, before.starts_at);
    const order = db.prepare('SELECT status,create_state,provider_trade_no FROM orders WHERE order_no=?').get(status);
    assert.deepEqual({ ...order }, { status: 'paid', create_state: 'ready', provider_trade_no: 'trade-' + status });
    assert.equal(db.prepare('SELECT state FROM payment_recovery WHERE order_no=?').get(status).state, 'resolved');
    const committed = snapshot(db);
    db.prepare('INSERT OR IGNORE INTO payment_settlements VALUES(?,?,19,?)')
      .run(status, 'trade-' + status, '2026-09-03T00:00:00Z');
    assert.deepEqual(snapshot(db), committed);
    assert.deepEqual(db.prepare('PRAGMA foreign_key_check').all(), []);
  });
}

test('upgraded populated database rejects wrong amount without partial state changes', t => {
  const db = populatedDatabase(t);
  upgrade(db);
  const before = snapshot(db);
  const recovery = db.prepare('SELECT * FROM payment_recovery ORDER BY order_no').all();
  assert.throws(() => db.exec("INSERT INTO payment_settlements VALUES('pending','trade-pending',29,'2026-09-02T00:00:00Z')"), /payment_mismatch/);
  assert.deepEqual(snapshot(db), before);
  assert.deepEqual(db.prepare('SELECT * FROM payment_recovery ORDER BY order_no').all(), recovery);
});
