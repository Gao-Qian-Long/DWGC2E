import {test} from 'node:test';
import assert from 'node:assert/strict';
import {DatabaseSync} from 'node:sqlite';
import {readFileSync,readdirSync} from 'node:fs';
import {billingRoute,notifyPayment,settlePayment} from '../src/payments/index.ts';
import {runPaymentRecovery} from '../src/payments/recovery.ts';
import {entitlementSnapshot} from '../src/entitlements.ts';
import {createEzfpySign,verifyEzfpySign,centsToMoneyString} from '../src/payments/sign.ts';

const migrationDirectory=new URL('../migrations/',import.meta.url);
const migrationFiles=readdirSync(migrationDirectory).filter(name=>name.endsWith('.sql')).sort();
const canonicalSchema=()=>readFileSync(new URL('../schema.sql',import.meta.url),'utf8');
const migrationSql=name=>readFileSync(new URL(name,migrationDirectory),'utf8');
const priceCorrection=migrationSql('0022_plan_price_correction.sql');
const catalogValues=db=>db.prepare('SELECT id,price_cents,quota,duration_days,enabled FROM plan_catalog ORDER BY id').all().map(row=>({...row}));

function paymentFixture(t){
 const db=new DatabaseSync(':memory:');db.exec(canonicalSchema());t.after(()=>db.close());
 const DB={prepare(sql){return {sql,values:[],bind(...values){this.values=values;return this;},async first(){return db.prepare(sql).get(...this.values)||null;},async all(){return {results:db.prepare(sql).all(...this.values)};},async run(){return {success:true,meta:{changes:Number(db.prepare(sql).run(...this.values).changes)}};}};},
  async batch(statements){db.exec('BEGIN');try{const results=statements.map(statement=>({success:true,meta:{changes:Number(db.prepare(statement.sql).run(...statement.values).changes)}}));db.exec('COMMIT');return results;}catch(error){db.exec('ROLLBACK');throw error;}}};
 for(const id of ['u','u2'])db.prepare('INSERT INTO users(id,account,password_hash,email,created_at) VALUES(?,?,?,?,?)').run(id,id,'unused',id+'@example.com',new Date().toISOString());
 const ym=new Date().toISOString().slice(0,7);
 for(const id of ['u','u2'])db.prepare('INSERT INTO usage_monthly(user_id,year_month,chars_used,chars_quota,task_count) VALUES(?,?,0,100000,0)').run(id,ym);
 const env={DB,EZFPY_API_BASE_URL:'https://provider.example/',EZFPY_PID:'123',EZFPY_KEY:'test-key',PUBLIC_API_URL:'https://api.example/',PUBLIC_WEB_URL:'https://web.example/',PAYMENTS_ENABLED:'true'};
 let calls=0;
 t.mock.method(globalThis,'fetch',async(url,options)=>{calls++;const p=Object.fromEntries(new URLSearchParams(options.body));assert.ok(verifyEzfpySign(p,env.EZFPY_KEY));return Response.json({code:200,out_trade_no:p.out_trade_no,trade_no:'T'+calls,type:p.type,money:p.money,qrcode:'https://pay.example/'+calls});});
 const route=(path,options={},who={user_id:'u',email:'u@example.com'})=>billingRoute(new Request('https://api.example'+path,options),env,who,'https://web.example');
 const checkout=(key,body,who)=>route('/v1/billing/checkout',{method:'POST',headers:{'Idempotency-Key':key},body:JSON.stringify(body)},who);
 const callback=(order,changes={})=>{const p={pid:env.EZFPY_PID,type:order.channel,out_trade_no:order.order_no,trade_no:order.provider_trade_no,name:order.plan_name,money:centsToMoneyString(order.payable_cents),param:order.plan_id,trade_status:'TRADE_SUCCESS',sign_type:'MD5',...changes};p.sign=createEzfpySign(p,env.EZFPY_KEY);return notifyPayment(new Request('https://api.example/v1/billing/notify/ezfpy?'+new URLSearchParams(p)),env);};
 const order=orderNo=>db.prepare('SELECT * FROM orders WHERE order_no=?').get(orderNo);
 return {db,DB,env,ym,route,checkout,callback,order,rows:()=>db.prepare('SELECT * FROM orders ORDER BY rowid').all(),calls:()=>calls};
}
function insertOrder(db,{no,planId,planName,level,amount,duration=7,user='u'}){
 db.prepare("INSERT INTO orders(order_no,user_id,plan_id,plan_name,duration_days,membership_level,amount_cents,payable_cents,channel,idempotency_key,created_at,expires_at,create_state,provider_trade_no) VALUES(?,?,?,?,?,?,?,?,'alipay',?,?,?,'ready',?)")
  .run(no,user,planId,planName,duration,level,amount,amount,no,new Date().toISOString(),new Date(Date.now()+900000).toISOString(),'TRADE-'+no);
 return db.prepare('SELECT * FROM orders WHERE order_no=?').get(no);
}

// P0-5: the checkout path charges plans.price_cents verbatim, so the seeded catalog is what a new
// deployment would actually sell.
test('P0-5 seeded catalog matches the production prices and quotas',t=>{
 const db=new DatabaseSync(':memory:');t.after(()=>db.close());db.exec(canonicalSchema());
 assert.deepEqual(catalogValues(db),[
  {id:'free',price_cents:0,quota:100000,duration_days:30,enabled:1},
  {id:'go',price_cents:1,quota:0,duration_days:30,enabled:0},
  {id:'max',price_cents:5900,quota:8000000,duration_days:30,enabled:1},
  {id:'pro',price_cents:3900,quota:1000000,duration_days:30,enabled:1},
 ]);
 // The legacy FK table is what the checkout path reads, so it has to mirror the catalog.
 assert.deepEqual({...db.prepare("SELECT price_cents,duration_days,enabled FROM plans WHERE id='pro'").get()},{price_cents:3900,duration_days:30,enabled:1});
 assert.deepEqual({...db.prepare("SELECT price_cents,duration_days,enabled FROM plans WHERE id='max'").get()},{price_cents:5900,duration_days:30,enabled:1});
});

test('P0-5 an initialised database reaches the same catalogue through the migration chain',t=>{
 const db=new DatabaseSync(':memory:');t.after(()=>db.close());
 for(const name of migrationFiles)db.exec(migrationSql(name));
 const canonical=new DatabaseSync(':memory:');t.after(()=>canonical.close());canonical.exec(canonicalSchema());
 assert.deepEqual(catalogValues(db),catalogValues(canonical));
 assert.deepEqual(db.prepare('SELECT * FROM plans ORDER BY id').all(),canonical.prepare('SELECT * FROM plans ORDER BY id').all());
});

test('P0-5 price correction migration is idempotent and never overwrites a configured price',t=>{
 const db=new DatabaseSync(':memory:');t.after(()=>db.close());db.exec(canonicalSchema());
 db.exec("UPDATE plan_catalog SET price_cents=5000,revision=revision+1 WHERE id='pro'");
 const configured={...db.prepare("SELECT price_cents,revision FROM plan_catalog WHERE id='pro'").get()};
 db.exec(priceCorrection);db.exec(priceCorrection);
 assert.deepEqual({...db.prepare("SELECT price_cents,revision FROM plan_catalog WHERE id='pro'").get()},configured);
 // Seed residue is repaired, including the legacy FK table the checkout path reads.
 db.exec("UPDATE plan_catalog SET price_cents=2 WHERE id='pro'; UPDATE plans SET price_cents=2 WHERE id='pro'");
 db.exec("UPDATE plan_catalog SET price_cents=3,quota=0,enabled=0,revision=1 WHERE id='max'; UPDATE plans SET price_cents=3 WHERE id='max'");
 db.exec(priceCorrection);db.exec(priceCorrection);
 assert.deepEqual({...db.prepare("SELECT price_cents,quota,enabled FROM plan_catalog WHERE id='pro'").get()},{price_cents:3900,quota:1000000,enabled:1});
 assert.deepEqual({...db.prepare("SELECT price_cents,quota,enabled FROM plan_catalog WHERE id='max'").get()},{price_cents:5900,quota:8000000,enabled:1});
 assert.deepEqual({...db.prepare("SELECT price_cents,duration_days FROM plans WHERE id='pro'").get()},{price_cents:3900,duration_days:30});
 assert.deepEqual({...db.prepare("SELECT price_cents,duration_days FROM plans WHERE id='max'").get()},{price_cents:5900,duration_days:30});
 // Availability is never changed for a tier an administrator has already touched.
 db.exec("UPDATE plan_catalog SET quota=0,enabled=0,revision=7 WHERE id='max'");
 db.exec(priceCorrection);
 assert.deepEqual({...db.prepare("SELECT quota,enabled FROM plan_catalog WHERE id='max'").get()},{quota:0,enabled:0});
});

test('P0-5 checkout refuses an unpriced plan instead of charging it and records the event',async t=>{
 const x=paymentFixture(t);
 x.db.exec("INSERT INTO plans VALUES('cheap','Cheap',2,30,'pro',1)");
 const response=await x.checkout('cheap-plan-key-000001',{planId:'cheap',channel:'alipay'});
 assert.equal(response.status,503);
 assert.equal((await response.json()).error_code,'plan_price_invalid');
 assert.equal(x.rows().length,0);
 assert.equal(x.calls(),0);
 const events=x.db.prepare("SELECT event_type,reason FROM payment_events ORDER BY id").all();
 assert.deepEqual(events.map(e=>({...e})),[{event_type:'plan_price_invalid',reason:'plan_price_invalid'}]);
 // Free is never sold through this endpoint: it has no purchasable row and orders reject 0 amount.
 assert.equal((await x.checkout('free-plan-key-000001',{planId:'free',channel:'alipay'})).status,400);
 assert.equal(x.db.prepare("SELECT COUNT(*) n FROM payment_events WHERE event_type='plan_price_invalid'").get().n,1);
});

test('P2-13 default pro plan can be purchased, granted once and replayed idempotently',async t=>{
 const x=paymentFixture(t);
 const created=await x.checkout('pro-purchase-key-0001',{planId:'pro',channel:'alipay'});
 assert.equal(created.status,200);
 const order=x.rows()[0];
 assert.equal(order.amount_cents,3900);
 assert.equal(order.payable_cents,3900);
 assert.equal(order.duration_days,30);
 assert.equal(order.create_state,'ready');
 assert.equal(x.db.prepare('SELECT quota FROM order_entitlements WHERE order_no=?').get(order.order_no).quota,1000000);
 assert.equal(await(await x.callback(order)).text(),'success');
 const subscription=x.db.prepare("SELECT * FROM subscriptions WHERE user_id='u'").get();
 assert.equal(subscription.plan_name,'pro');
 assert.ok(Math.abs(Date.parse(subscription.expires_at)-Date.now()-30*86400000)<10000);
 assert.equal(x.db.prepare("SELECT quota FROM subscription_quotas WHERE user_id='u'").get().quota,1000000);
 assert.equal(x.db.prepare("SELECT chars_quota FROM usage_monthly WHERE user_id='u' AND year_month=?").get(x.ym).chars_quota,1000000);
 assert.equal(x.db.prepare('SELECT COUNT(*) n FROM payment_settlements').get().n,1);
 await x.callback(order);
 assert.equal(x.db.prepare('SELECT COUNT(*) n FROM payment_settlements').get().n,1);
 assert.equal(x.db.prepare("SELECT expires_at FROM subscriptions WHERE user_id='u'").get().expires_at,subscription.expires_at);
 const replay=await x.checkout('pro-purchase-key-0001',{planId:'pro',channel:'alipay'});
 assert.equal(replay.status,200);
 assert.equal((await replay.json()).orderNo,order.order_no);
 assert.equal(x.rows().length,1);
 assert.equal(x.calls(),1);
});

test('P2-16 super administrators receive the configured Max quota',async t=>{
 const x=paymentFixture(t);
 const before=await entitlementSnapshot(x.env,'u');
 assert.equal(before.usage.base_quota,100000);
 const version=x.db.prepare("SELECT version FROM admin_account_snapshot WHERE id='u'").get().version;
 x.db.prepare('INSERT INTO admin_account_changes(id,user_id,actor,before_snapshot,action,reason,created_at) VALUES(?,?,?,?,?,?,?)')
  .run(crypto.randomUUID(),'u','key:test',version,'grant_super','grant super for entitlement test',new Date().toISOString());
 const snapshot=await entitlementSnapshot(x.env,'u');
 assert.equal(snapshot.subscription.plan_name,'max');
 assert.equal(snapshot.subscription.is_super,true);
 assert.equal(snapshot.usage.plan_quota,8000000);
 assert.ok(snapshot.usage.base_quota>=8000000);
});

test('P3-27 settlement maps quota from the catalog and refuses an unmapped tier',async t=>{
 const x=paymentFixture(t);
 // A legacy product that exists only in the FK table: the tier still has to resolve through the
 // catalog instead of a hardcoded constant.
 x.db.exec("INSERT INTO plans VALUES('legacy-pro','Legacy Pro',1900,7,'pro',1)");
 const legacy=insertOrder(x.db,{no:'DWa'.padEnd(34,'1'),planId:'legacy-pro',planName:'Legacy Pro',level:'pro',amount:1900});
 await settlePayment(x.env,legacy,legacy.provider_trade_no,1900);
 assert.equal(x.db.prepare("SELECT quota FROM subscription_quotas WHERE user_id='u'").get().quota,1000000);
 // An order whose tier has no catalog mapping must not be granted an invented entitlement.
 x.db.exec("INSERT INTO plans VALUES('retired','Retired',500,7,'pro',1)");
 const retired=insertOrder(x.db,{no:'DWb'.padEnd(34,'2'),planId:'retired',planName:'Retired',level:'retired',amount:500,user:'u2'});
 await assert.rejects(()=>settlePayment(x.env,retired,retired.provider_trade_no,500),/settlement_quota_mapping_missing/);
 assert.equal(x.db.prepare("SELECT COUNT(*) n FROM subscription_quotas WHERE user_id='u2'").get().n,0);
 assert.equal(x.db.prepare('SELECT COUNT(*) n FROM payment_settlements').get().n,1);
 // The signed callback records the exact rejection instead of inventing an entitlement.
 const response=await x.callback(retired);
 assert.equal(await response.text(),'error');
 assert.equal(response.status,503);
 assert.deepEqual({...x.db.prepare("SELECT reason FROM payment_events WHERE order_no=? AND event_type='callback_rejected'").get(retired.order_no)},{reason:'settlement_quota_mapping_missing'});
 assert.equal(x.db.prepare("SELECT COUNT(*) n FROM subscriptions WHERE user_id='u2'").get().n,0);
});

test('P3-28 upgrading a tier keeps the remaining paid time',async t=>{
 const x=paymentFixture(t);
 const expires=new Date(Date.now()+3*86400000).toISOString();
 x.db.prepare("INSERT INTO subscriptions(user_id,plan_name,starts_at,expires_at,auto_renew,updated_at) VALUES('u','pro',?,?,0,?)").run('2026-01-01T00:00:00.000Z',expires,'2026-01-01T00:00:00.000Z');
 const created=await x.checkout('max-upgrade-key-00001',{planId:'max',channel:'alipay'});
 assert.equal(created.status,200);
 const order=x.rows()[0];
 assert.equal(order.amount_cents,5900);
 assert.equal(await(await x.callback(order)).text(),'success');
 const subscription=x.db.prepare("SELECT * FROM subscriptions WHERE user_id='u'").get();
 assert.equal(subscription.plan_name,'max');
 assert.equal(subscription.starts_at,'2026-01-01T00:00:00.000Z');
 assert.equal(Date.parse(subscription.expires_at)-Date.parse(expires),30*86400000);
 assert.equal(x.db.prepare("SELECT quota FROM subscription_quotas WHERE user_id='u'").get().quota,8000000);
});

test('P3-26 the dead settlement trigger definition is gone and the live one has no hardcoded quota',t=>{
 const schema=canonicalSchema();
 assert.equal((schema.match(/CREATE TRIGGER (?:IF NOT EXISTS )?payment_settlement_apply/g)||[]).length,1);
 const db=new DatabaseSync(':memory:');t.after(()=>db.close());db.exec(schema);
 const live=db.prepare("SELECT COUNT(*) n FROM sqlite_master WHERE type='trigger' AND name='payment_settlement_apply'").get().n;
 assert.equal(live,1);
 const body=db.prepare("SELECT sql FROM sqlite_master WHERE type='trigger' AND name='payment_settlement_apply'").get().sql;
 assert.ok(!body.includes('1000000'),'the live trigger must not invent a quota');
 assert.ok(body.includes('settlement_quota_mapping_missing'));
});

test('P3-30 the recovery backfill hands historical orders to the review queue and schedules recent ones',t=>{
 const db=new DatabaseSync(':memory:');t.after(()=>db.close());db.exec('PRAGMA foreign_keys=ON');
 for(const name of migrationFiles.filter(name=>name<'0008'))db.exec(migrationSql(name));
 db.exec("INSERT INTO users(id,account,password_hash,created_at) VALUES('u','u','unused','2026-01-01')");
 const insert=(no,created,status)=>db.prepare("INSERT INTO orders(order_no,user_id,plan_id,plan_name,duration_days,membership_level,amount_cents,payable_cents,channel,idempotency_key,created_at,expires_at,status) VALUES(?,'u','test_pro_019','fixture',7,'pro',1900,1900,'alipay',?,?,?,?)").run(no,no,created,created,status);
 insert('DW'+'a'.repeat(32),'2026-01-01T00:00:00Z','pending');
 insert('DW'+'b'.repeat(32),new Date().toISOString(),'pending');
 insert('DW'+'c'.repeat(32),'2026-01-02T00:00:00Z','expired');
 db.exec(migrationSql('0008_payment_reliability.sql'));
 const rows=db.prepare('SELECT order_no,state,reason,next_attempt_at FROM payment_recovery ORDER BY order_no').all().map(row=>({...row}));
 assert.deepEqual(rows.map(row=>[row.order_no,row.state,row.reason]),[
  ['DW'+'a'.repeat(32),'manual','legacy_order_requires_review'],
  ['DW'+'b'.repeat(32),'queued','awaiting_confirmation'],
  ['DW'+'c'.repeat(32),'manual','legacy_order_requires_review'],
 ]);
 assert.ok(Date.parse(rows[1].next_attempt_at)>Date.now());
});

test('P3-31 the order list rejects a malformed cursor and accepts a well formed one',async t=>{
 const x=paymentFixture(t);
 const invalid=await x.route('/v1/billing/orders?before='+encodeURIComponent('1 OR 1=1'));
 assert.equal(invalid.status,400);
 assert.equal((await invalid.json()).error_code,'invalid_cursor');
 const accepted=await x.route('/v1/billing/orders?before='+encodeURIComponent('2026-01-01T00:00:00.000ZDW'+'a'.repeat(32)));
 assert.equal(accepted.status,200);
 assert.deepEqual((await accepted.json()).orders,[]);
});

test('P3-25 upstream prose is never persisted and the debug switch masks digits',async t=>{
 const x=paymentFixture(t);
 const upstream='余额不足，商户号1234567890 请联系客服 或访问 https://provider.example/secret-path';
 t.mock.method(globalThis,'fetch',async()=>Response.json({code:500,msg:upstream}));
 const response=await x.checkout('diagnostic-key-0000001',{planId:'pro',channel:'alipay'});
 assert.equal(response.status,200);
 const order=x.rows()[0];
 assert.equal(order.create_state,'unknown');
 assert.equal(order.last_error_code,'provider_merchant_rejected');
 const recorded=x.db.prepare("SELECT reason FROM payment_events WHERE order_no=? AND event_type='payment_create_unknown'").get(order.order_no).reason;
 assert.ok(recorded.includes('provider_merchant_rejected'));
 for(const leak of ['余额','商户号','1234567890','请联系客服','provider.example/secret-path'])
  assert.ok(!recorded.includes(leak),`audit record leaked ${leak}`);
 // Diagnostics still exist, but only behind an explicit switch and with every digit masked.
 x.env.PAYMENT_DEBUG_DIAGNOSTICS='true';
 const second=await x.checkout('diagnostic-key-0000002',{planId:'pro',channel:'alipay'},{user_id:'u2',email:'u2@example.com'});
 assert.equal(second.status,200);
 const debugged=JSON.parse(x.db.prepare("SELECT reason FROM payment_events WHERE event_type='payment_create_unknown' AND order_no<>? ORDER BY id DESC LIMIT 1").get(order.order_no).reason);
 assert.ok(debugged.businessMessage.includes('余额不足'));
 assert.ok(!/[0-9]/.test(debugged.businessMessage),'digits must be masked in diagnostic text');
});

test('P3-29 a lease is released when the task changes underneath the recovery sweep',async t=>{
 const x=paymentFixture(t);
 await x.checkout('recovery-lease-key-0001',{planId:'pro',channel:'alipay'});
 x.db.exec("UPDATE payment_recovery SET next_attempt_at='2000-01-01'");
 // Simulate a trusted callback resolving the order while the sweep holds its lease.
 const prepare=x.DB.prepare.bind(x.DB);
 x.DB.prepare=sql=>{const statement=prepare(sql);if(/SET lease_until=\?/.test(sql)){const run=statement.run.bind(statement);statement.run=async()=>{const result=await run();x.db.exec("UPDATE payment_recovery SET state='resolved'");return result;};}return statement;};
 await runPaymentRecovery(x.env);
 assert.deepEqual({...x.db.prepare('SELECT state,lease_until,lease_token FROM payment_recovery').get()},{state:'resolved',lease_until:null,lease_token:null});
});
