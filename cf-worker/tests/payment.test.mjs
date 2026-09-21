import {runPaymentRecovery,recoveryAdmin} from '../src/payments/recovery.ts';
import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {DatabaseSync} from 'node:sqlite';
import {readFileSync} from 'node:fs';
import {md5,createEzfpySign,verifyEzfpySign,moneyStringToCents,centsToMoneyString} from '../src/payments/sign.ts';
import {billingRoute,notifyPayment,settlePayment,safeImage} from '../src/payments/index.ts';
import worker from '../src/index.ts';
for(const value of ['', 'a', 'abc', '中文商品名称', 'a'.repeat(1000),'法兰 & 0.19\\test'])test('MD5 matches independent implementation: '+value.slice(0,20),()=>assert.equal(md5(value),createHash('md5').update(value).digest('hex')));
test('sign canonicalization, null/empty exclusion and tamper detection',()=>{const p={pid:'123',money:'0.19',name:'中文',param:'0'};const sign=createEzfpySign(p,'secret');assert.equal(sign,createEzfpySign({sign:'ignored',sign_type:'MD5',nothing:null,empty:'',...Object.fromEntries(Object.entries(p).reverse())},'secret'));assert.ok(verifyEzfpySign({...p,sign},'secret'));assert.ok(!verifyEzfpySign({...p,money:'0.29',sign},'secret'));assert.ok(!verifyEzfpySign({...p,sign},'wrong'));});
test('strict integer cents conversion',()=>{for(const [s,n] of [['0.01',1],['0.19',19],['0.29',29],['19.90',1990],['100',10000]]){assert.equal(moneyStringToCents(s),n);assert.equal(moneyStringToCents(centsToMoneyString(n)),n);}for(const s of ['-1','0','1e2','0.199',' 0.19','NaN',0.19])assert.throws(()=>moneyStringToCents(s));});
function setup(t){const db=new DatabaseSync(':memory:');db.exec(readFileSync(new URL('../schema.sql',import.meta.url),'utf8'));db.exec("UPDATE plans SET enabled=1,price_cents=1900 WHERE id='test_pro_019'; UPDATE plans SET enabled=1,price_cents=2900 WHERE id='test_pro_029'");t.after(()=>db.close());
 const DB={prepare(sql){return {values:[],bind(...values){this.values=values;return this;},async first(){return db.prepare(sql).get(...this.values)||null;},async all(){return {results:db.prepare(sql).all(...this.values)};},async run(){return {meta:{changes:Number(db.prepare(sql).run(...this.values).changes)}};}};}};
 for(const id of ['u','other'])db.prepare('INSERT INTO users(id,account,password_hash,email,created_at) VALUES(?,?,?,?,?)').run(id,id,'unused',id+'@example.com',new Date().toISOString());
 const e={DB,EZFPY_API_BASE_URL:'https://provider.example/',EZFPY_PID:'123',EZFPY_KEY:'test-key',PUBLIC_API_URL:'https://api.example/',PUBLIC_WEB_URL:'https://web.example/',PAYMENTS_ENABLED:'true'};
 const user={user_id:'u',email:'u@example.com'},rows=()=>db.prepare('SELECT * FROM orders ORDER BY rowid').all();
 const route=(path,options={},who=user)=>billingRoute(new Request('https://api.example'+path,options),e,who,'https://web.example');
 const checkout=(key='test-checkout-00001',body={planId:'test_pro_019',channel:'alipay'})=>route('/v1/billing/checkout',{method:'POST',headers:{'Idempotency-Key':key},body:JSON.stringify(body)});
 let calls=0;t.mock.method(globalThis,'fetch',async(url,options)=>{calls++;assert.equal(options.redirect,'manual');const p=Object.fromEntries(new URLSearchParams(options.body));assert.ok(verifyEzfpySign(p,e.EZFPY_KEY));return Response.json({code:200,out_trade_no:p.out_trade_no,trade_no:'T'+calls,type:p.type,money:p.money,qrcode:'https://pay.example/'+calls});});
 const callback=(o,changes={},valid=true)=>{const p={pid:'123',type:o.channel,out_trade_no:o.order_no,trade_no:o.provider_trade_no,name:o.plan_name,money:centsToMoneyString(o.payable_cents),param:o.plan_id,trade_status:'TRADE_SUCCESS',sign_type:'MD5',...changes};p.sign=valid?createEzfpySign(p,e.EZFPY_KEY):'0'.repeat(32);return notifyPayment(new Request('https://api.example/v1/billing/notify/ezfpy?'+new URLSearchParams(p)),e);};
 return {db,e,user,rows,route,checkout,callback,calls:()=>calls};}
test('checkout server pricing and signed upstream request; duplicate key creates once',async t=>{const x=setup(t);assert.equal((await x.checkout()).status,200);const o=x.rows()[0];assert.equal(o.amount_cents,1900);assert.equal(o.duration_days,7);assert.equal(o.create_state,'ready');await x.checkout();assert.equal(x.calls(),1);assert.equal(x.rows().length,1);});
test('wxpay and second product price',async t=>{const x=setup(t);await x.checkout('test-checkout-00002',{planId:'test_pro_029',channel:'wxpay'});assert.equal(x.rows()[0].amount_cents,2900);});
test('global switch restricts checkout; all authenticated users may buy regardless of legacy allowlist',async t=>{const x=setup(t);x.e.PAYMENTS_ENABLED='false';assert.equal((await x.checkout()).status,403);assert.equal(x.calls(),0);x.e.PAYMENTS_ENABLED='true';x.e.PAYMENTS_TEST_USERS='nobody@example.com';assert.equal((await x.checkout()).status,200);const r=await x.route('/v1/billing/checkout',{method:'POST',headers:{'Idempotency-Key':'other-user-checkout-0001'},body:JSON.stringify({planId:'test_pro_029',channel:'wxpay'})},{user_id:'other',email:'other@example.com'});assert.equal(r.status,200);assert.equal(x.calls(),2);});
for(const body of [{planId:'bad',channel:'alipay'},{planId:'test_pro_019',channel:'qqpay'},{planId:'test_pro_019',channel:'alipay',price:1}])test('reject invalid checkout '+JSON.stringify(body),async t=>{const x=setup(t);assert.equal((await x.checkout('test-checkout-00001',body)).status,400);assert.equal(x.calls(),0);});
test('disabled plans and conflicting idempotency key',async t=>{const x=setup(t);await x.checkout();assert.equal((await x.checkout('test-checkout-00001',{planId:'test_pro_029',channel:'alipay'})).status,409);await x.callback(x.rows()[0]);x.db.exec('UPDATE plans SET enabled=0');assert.equal((await x.checkout('test-checkout-00002')).status,400);});
test('concurrent checkout is idempotent',async t=>{const x=setup(t);await Promise.all([x.checkout(),x.checkout()]);assert.equal(x.rows().length,1);assert.equal(x.calls(),1);});
test('quota preservation + first purchase + duplicate notification + renewal',async t=>{const x=setup(t);x.db.prepare('INSERT INTO usage_monthly VALUES(?,?,1234,100000,3)').run('u',new Date().toISOString().slice(0,7));await x.checkout();const o=x.rows()[0];assert.equal(await(await x.callback(o)).text(),'success');const first=x.db.prepare('SELECT * FROM subscriptions').get();assert.equal(first.plan_name,'pro');assert.ok(Math.abs(Date.parse(first.expires_at)-Date.now()-7*86400000)<10000);await Promise.all([x.callback(o),x.callback(o)]);assert.equal(x.db.prepare('SELECT expires_at FROM subscriptions').get().expires_at,first.expires_at);await x.checkout('test-checkout-00002');await x.callback(x.rows()[1]);assert.equal(Date.parse(x.db.prepare('SELECT expires_at FROM subscriptions').get().expires_at)-Date.parse(first.expires_at),7*86400000);assert.equal(x.db.prepare('SELECT chars_used FROM usage_monthly').get().chars_used,1234);assert.equal(x.db.prepare('SELECT chars_quota FROM usage_monthly').get().chars_quota,1000000);});
test('expired subscription starts at settlement time',async t=>{const x=setup(t);x.db.exec("INSERT INTO subscriptions(user_id,plan_name,expires_at,updated_at) VALUES('u','pro','2020-01-01T00:00:00Z','')");await x.checkout();await x.callback(x.rows()[0]);assert.ok(Math.abs(Date.parse(x.db.prepare('SELECT expires_at FROM subscriptions').get().expires_at)-Date.now()-7*86400000)<10000);});
for(const change of [{pid:'wrong'},{money:'0.01'},{trade_status:'WAIT_BUYER_PAY'},{type:'wxpay'},{trade_no:'wrong'},{out_trade_no:'DW'+'f'.repeat(32)}])test('reject signed mismatch '+JSON.stringify(change),async t=>{const x=setup(t);await x.checkout();assert.notEqual(await(await x.callback(x.rows()[0],change)).text(),'success');assert.equal(x.db.prepare('SELECT COUNT(*) n FROM payment_settlements').get().n,0);});
test('invalid signature and repeated query parameters rejected',async t=>{const x=setup(t);await x.checkout();assert.equal((await x.callback(x.rows()[0],{},false)).status,400);assert.equal((await notifyPayment(new Request('https://api.example/?pid=123&pid=123'),x.e)).status,400);});
test('other user cannot see an order',async t=>{const x=setup(t);await x.checkout();assert.equal((await x.route('/v1/billing/orders/'+x.rows()[0].order_no,{}, {user_id:'other'})).status,404);});
test('rollback after subscription write leaves both order and membership unchanged',async t=>{const x=setup(t);await x.checkout();x.db.exec("CREATE TRIGGER fail_settlement BEFORE UPDATE OF status ON orders WHEN NEW.status='paid' BEGIN SELECT RAISE(ABORT,'injected_failure'); END;");assert.equal((await x.callback(x.rows()[0])).status,503);assert.equal(x.rows()[0].status,'pending');assert.equal(x.db.prepare('SELECT COUNT(*) n FROM subscriptions').get().n,0);assert.equal(x.db.prepare('SELECT COUNT(*) n FROM payment_settlements').get().n,0);});
test('two distinct purchases settle concurrently without lost renewal',async t=>{const x=setup(t);await x.checkout();x.db.exec("UPDATE orders SET expires_at='2000-01-01'");await x.checkout('test-checkout-00002');assert.equal(x.rows().length,2);await Promise.all(x.rows().map(o=>x.callback(o)));const s=x.db.prepare('SELECT expires_at FROM subscriptions').get();assert.ok(Math.abs(Date.parse(s.expires_at)-Date.now()-14*86400000)<10000);});
test('trusted callback settles unknown creation',async t=>{const x=setup(t);await x.checkout();x.db.exec("UPDATE orders SET create_state='unknown'");assert.equal(await(await x.callback(x.rows()[0])).text(),'success');});
for(const result of [()=>{throw Error('network');},()=>new Response('bad json'),()=>Response.json({code:500}),()=>Response.json({code:200,money:'0.20'}),()=>new Response('',{status:502})])test('uncertain upstream result retained and never blindly retried',async t=>{const x=setup(t);t.mock.method(globalThis,'fetch',async()=>result());await x.checkout();assert.equal(x.rows()[0].create_state,'unknown');await x.checkout();assert.equal(x.rows().length,1);});
test('image URLs limited to approved HTTPS origins',t=>{const x=setup(t);assert.equal(safeImage(x.e,'qr.png'),'https://provider.example/qr.png');for(const u of ['http://provider.example/q','javascript:alert(1)','https://evil.example/q','https://a:b@provider.example/q'])assert.throws(()=>safeImage(x.e,u));});
test('repeated migration preserves existing test plan and settlement',async t=>{const x=setup(t);await x.checkout();await x.callback(x.rows()[0]);x.db.exec(readFileSync(new URL('../migrations/0003_payments.sql',import.meta.url),'utf8'));assert.equal(x.db.prepare('SELECT COUNT(*) n FROM payment_settlements').get().n,1);});
test('Worker checkout requires real session, callback bypasses user auth only',async t=>{const x=setup(t);assert.equal((await worker.fetch(new Request('https://api.example/v1/billing/checkout',{method:'POST'}),x.e)).status,401);assert.equal((await worker.fetch(new Request('https://api.example/v1/billing/notify/ezfpy'),x.e)).status,400);});

test('operator inspection requires strong separate key; never grants subscription',async t=>{const x=setup(t);await x.checkout();const endpoint='https://api.example/v1/admin/billing/orders/'+x.rows()[0].order_no+'/inspect';assert.equal((await worker.fetch(new Request(endpoint,{method:'POST'}),x.e)).status,401);x.e.ADMIN_API_KEY='operator-test-key-'.repeat(3);t.mock.method(globalThis,'fetch',async(url,opts)=>{assert.equal(opts.redirect,'manual');assert.equal(new URL(url).pathname,'/api/findorder');assert.equal(opts.method,'POST');assert.equal(new URLSearchParams(opts.body).get('order_no'),x.rows()[0].order_no);return Response.json({code:200,data:{id:'123',out_trade_no:x.rows()[0].order_no,type:'alipay',status:1,money:'19.00',key:x.e.EZFPY_KEY}});});const r=await worker.fetch(new Request(endpoint,{method:'POST',headers:{authorization:'Bearer '+x.e.ADMIN_API_KEY}}),x.e);const text=await r.text();assert.equal(r.status,200);assert.equal(JSON.parse(text).settled,false);assert.ok(!text.includes(x.e.EZFPY_KEY));assert.equal(x.db.prepare('SELECT COUNT(*) n FROM subscriptions').get().n,0);});

test('callback before create response settles once and does not get overwritten',async t=>{
 const x=setup(t);t.mock.method(globalThis,'fetch',async(_url,opts)=>{const p=Object.fromEntries(new URLSearchParams(opts.body));assert.equal(await(await x.callback(x.rows()[0],{trade_no:'EARLY'})).text(),'success');return Response.json({code:200,out_trade_no:p.out_trade_no,trade_no:'EARLY',type:p.type,money:p.money,qrcode:'https://pay.example/early'});});
 const r=await x.checkout();assert.equal((await r.json()).status,'paid');assert.equal(x.rows()[0].provider_trade_no,'EARLY');assert.equal(x.rows()[0].last_error_code,null);assert.equal(x.db.prepare('SELECT COUNT(*) n FROM payment_settlements').get().n,1);
});
test('timeout followed by signed callback binds unknown order and resolves recovery',async t=>{const x=setup(t);t.mock.method(globalThis,'fetch',async()=>{throw Error('timeout');});await x.checkout();assert.equal(x.rows()[0].create_state,'unknown');assert.equal(await(await x.callback(x.rows()[0],{trade_no:'LATE'})).text(),'success');assert.equal(x.db.prepare('SELECT state FROM payment_recovery').get().state,'resolved');});
test('expiry and hidden record do not discard late trusted payment',async t=>{const x=setup(t);await x.checkout();const o=x.rows()[0];await x.route('/v1/billing/orders/'+o.order_no+'/hide',{method:'POST'});x.db.exec("UPDATE orders SET status='expired',expires_at='2000-01-01'");assert.equal(await(await x.callback(x.rows()[0])).text(),'success');assert.equal((await(await x.route('/v1/billing/orders')).json()).orders.length,1);});
test('signed callbacks racing different transactions never double grant',async t=>{const x=setup(t);t.mock.method(globalThis,'fetch',async()=>{throw Error('timeout');});await x.checkout();const o=x.rows()[0];const replies=await Promise.all(['T-A','T-B'].map(trade_no=>x.callback(o,{trade_no})));assert.equal((await Promise.all(replies.map(r=>r.text()))).filter(s=>s==='success').length,1);assert.equal(x.db.prepare('SELECT COUNT(*) n FROM payment_settlements').get().n,1);});
test('transaction failure rolls back binding as well as subscription and can retry',async t=>{const x=setup(t);t.mock.method(globalThis,'fetch',async()=>{throw Error('timeout');});await x.checkout();x.db.exec("CREATE TRIGGER inject BEFORE UPDATE OF status ON orders WHEN NEW.status='paid' BEGIN SELECT RAISE(ABORT,'injected');END;");assert.equal((await x.callback(x.rows()[0],{trade_no:'RETRY'})).status,503);assert.equal(x.rows()[0].provider_trade_no,null);assert.equal(x.db.prepare('SELECT COUNT(*) n FROM subscriptions').get().n,0);x.db.exec('DROP TRIGGER inject');assert.equal(await(await x.callback(x.rows()[0],{trade_no:'RETRY'})).text(),'success');});
test('paused checkout still replays prior intent and accepts callbacks',async t=>{const x=setup(t);await x.checkout();x.e.PAYMENTS_ENABLED='false';assert.equal((await x.checkout()).status,200);assert.equal((await x.checkout('different-key-0000001')).status,409);assert.equal(x.calls(),1);assert.equal(await(await x.callback(x.rows()[0])).text(),'success');assert.equal((await x.checkout('new-after-paid-00001')).status,403);});
test('controlled checkout allowlist uses exact user IDs not emails',async t=>{const x=setup(t);x.e.PAYMENTS_ENABLED='false';x.e.PAYMENTS_TEST_USERS=x.user.email;assert.equal((await x.checkout()).status,403);x.e.PAYMENTS_TEST_USERS='u';assert.equal((await x.checkout()).status,200);});
test('confirmation is owner-scoped, throttled and cannot grant membership',async t=>{const x=setup(t);await x.checkout();const path='/v1/billing/orders/'+x.rows()[0].order_no+'/confirm';assert.equal((await x.route(path,{method:'POST'},{user_id:'other'})).status,404);for(let i=0;i<5;i++)assert.equal((await x.route(path,{method:'POST'})).status,200);assert.equal((await x.route(path,{method:'POST'})).status,429);assert.equal(x.db.prepare('SELECT COUNT(*) n FROM payment_settlements').get().n,0);});
test('consistent entitlement snapshot preserves usage and reflects expiry',async t=>{const x=setup(t);x.db.prepare('INSERT INTO usage_monthly VALUES(?,?,123,100000,1)').run('u',new Date().toISOString().slice(0,7));await x.checkout();await x.callback(x.rows()[0]);let d=await(await x.route('/v1/billing/entitlements')).json();assert.equal(d.userId,'u');assert.equal(d.subscription.plan_name,'pro');assert.equal(d.usage.used,123);assert.equal(d.usage.monthly_quota,1000000);x.db.exec("UPDATE subscriptions SET expires_at='2000-01-01'");d=await(await x.route('/v1/billing/entitlements')).json();assert.equal(d.subscription.plan_name,'free');assert.equal(d.usage.monthly_quota,100000);});
test('recovery is leased, persistent and never trusts unauthenticated queries',async t=>{const x=setup(t);await x.checkout();x.db.exec("UPDATE orders SET created_at='2000-01-01';UPDATE payment_recovery SET next_attempt_at='2000-01-01'");const before=x.calls();await Promise.all([runPaymentRecovery(x.e),runPaymentRecovery(x.e)]);assert.equal(x.calls(),before);const r=x.db.prepare('SELECT * FROM payment_recovery').get();assert.equal(r.state,'manual');assert.equal(r.attempts,1);assert.equal(x.db.prepare('SELECT COUNT(*) n FROM subscriptions').get().n,0);assert.equal((await recoveryAdmin(new Request('https://api.example/v1/admin/billing/recovery'),x.e)).status,401);});
test('reapplying reliability migration preserves paid order and renewals',async t=>{const x=setup(t);await x.checkout();await x.callback(x.rows()[0]);const before=x.db.prepare('SELECT * FROM subscriptions').get();x.db.exec(readFileSync(new URL('../migrations/0008_payment_reliability.sql',import.meta.url),'utf8'));await x.callback(x.rows()[0]);assert.deepEqual(x.db.prepare('SELECT * FROM subscriptions').get(),before);});

test('concurrent recovery cannot reclaim a waiting task before its next due time',async t=>{const x=setup(t);await x.checkout();x.db.exec("UPDATE payment_recovery SET next_attempt_at='2000-01-01'");await Promise.all([runPaymentRecovery(x.e),runPaymentRecovery(x.e)]);const r=x.db.prepare('SELECT * FROM payment_recovery').get();assert.equal(r.state,'waiting_callback');assert.equal(r.attempts,1);await runPaymentRecovery(x.e);assert.equal(x.db.prepare('SELECT attempts FROM payment_recovery').get().attempts,1);});

test('admin can inspect paid-order conflicts and complete evidence history without settling',async t=>{const x=setup(t);x.e.ADMIN_API_KEY='a'.repeat(40);await x.checkout();const o=x.rows()[0];await x.callback(o);await x.callback(o,{trade_no:'CONFLICT'});const headers={authorization:'Bearer '+x.e.ADMIN_API_KEY};const report=await(await recoveryAdmin(new Request('https://api.example/v1/admin/billing/recovery',{headers}),x.e)).json();assert.equal(report.conflicts.length,1);const endpoint='https://api.example/v1/admin/billing/orders/'+o.order_no+'/review';const review=await recoveryAdmin(new Request(endpoint,{headers,method:'POST',body:JSON.stringify({operator:'operator-1',evidenceRef:'ticket/123',outcome:'escalated'})}),x.e);assert.equal(review.status,200);const detail=await(await recoveryAdmin(new Request(endpoint,{headers}),x.e)).json();assert.equal(detail.order.status,'paid');assert.ok(detail.events.some(e=>e.event_type==='manual_review'));assert.equal(x.db.prepare('SELECT COUNT(*) n FROM payment_settlements').get().n,1);assert.equal((await recoveryAdmin(new Request(endpoint),x.e)).status,401);});

test('scheduled failure remains a failure and logs never expose database details',async t=>{
 const logs=[];t.mock.method(console,'log',message=>logs.push(JSON.parse(message)));t.mock.method(console,'error',message=>logs.push(JSON.parse(message)));
 const tasks=[];await worker.scheduled({scheduledTime:123},{DB:{prepare(){throw new Error('private-order-and-merchant-data');}}},{waitUntil(promise){tasks.push(promise);}});
 assert.equal(tasks.length,1);await assert.rejects(tasks[0],{message:'payment_recovery_failed'});
 assert.deepEqual(logs.map(x=>x.event),['payment_recovery_started','payment_recovery_failed']);assert.equal(logs[0].runId,logs[1].runId);assert.ok(!JSON.stringify(logs).includes('private-order-and-merchant-data'));
});

test('legacy success returns QR without blocking on unauthenticated lookup; never settles',async t=>{const x=setup(t);let calls=0;t.mock.method(globalThis,'fetch',async url=>{calls++;assert.equal(new URL(url).pathname,'/mapi.php');return Response.json({code:1,trade_no:'LEGACY123',qrcode:'https://pay.example/local-only'});});const r=await(await x.checkout()).json();assert.equal(r.createState,'ready');assert.equal(calls,1);assert.equal(x.db.prepare('SELECT COUNT(*) n FROM payment_settlements').get().n,0);assert.equal(x.db.prepare('SELECT COUNT(*) n FROM subscriptions').get().n,0);assert.notEqual(await(await x.callback(x.rows()[0],{money:'0.29'})).text(),'success');});
for(const change of [{money:'0.29'},{pid:'other'},{out_trade_no:'wrong'},{type:'wxpay'},{trade_no:''},{qrcode:''}])test('legacy QR rejects contradictions or missing QR '+JSON.stringify(change),async t=>{const x=setup(t);t.mock.method(globalThis,'fetch',async()=>Response.json({code:1,trade_no:'LEGACY123',qrcode:'https://pay.example/local',...change}));await x.checkout();assert.equal(x.rows()[0].create_state,'unknown');});
test('hidden order status retains hidden flag for reload without destroying settlement data',async t=>{const x=setup(t);await x.checkout();const no=x.rows()[0].order_no;await x.route('/v1/billing/orders/'+no+'/hide',{method:'POST'});const d=await(await x.route('/v1/billing/orders/'+no)).json();assert.equal(d.hidden,true);assert.equal(x.rows().length,1);});
test('stored mismatched hosted QR is suppressed on status, list, and checkout replay without changing order or settlement',async t=>{const x=setup(t);await x.checkout();const original=x.rows()[0];x.db.prepare('UPDATE orders SET qr_code=? WHERE order_no=?').run('https://code.ymyu.cn/url.php?price=0.22',original.order_no);const snapshot=x.rows();const status=await(await x.route('/v1/billing/orders/'+original.order_no)).json();const list=await(await x.route('/v1/billing/orders')).json();const replay=await(await x.checkout()).json();for(const d of [status,list.orders[0],replay]){assert.equal(d.errorCode,'provider_amount_mismatch');assert.equal(d.allowedActions.pay,false);assert.equal(d.createState,'unknown');assert.equal(d.qrCode,null);assert.equal(d.qrCodeImageUrl,null);assert.equal(d.payableCents,1900);}assert.deepEqual(x.rows(),snapshot);assert.equal(x.calls(),1);assert.equal((await x.callback(x.rows()[0])).status,200);assert.equal(x.rows()[0].status,'paid');});
test('new mismatched hosted QR is not persisted or exposed',async t=>{const x=setup(t);t.mock.method(globalThis,'fetch',async()=>Response.json({code:1,trade_no:'T1',qrcode:'https://code.ymyu.cn/url.php?price=0.22'}));const response=await(await x.checkout()).json();assert.equal(response.errorCode,'provider_amount_mismatch');assert.equal(response.allowedActions.pay,false);assert.equal(response.qrCode,null);assert.equal(x.rows()[0].qr_code,null);assert.equal(x.rows()[0].create_state,'unknown');assert.equal(x.rows()[0].amount_cents,1900);});

test('new keys reuse pending order; plan or channel switch cannot create another',async t=>{const x=setup(t);const first=await(await x.checkout()).json();const again=await(await x.checkout('different-device-00001')).json();assert.equal(again.order.orderNo,first.orderNo);assert.equal(x.calls(),1);for(const body of [{planId:'test_pro_029',channel:'alipay'},{planId:'test_pro_019',channel:'wxpay'}])assert.equal((await x.checkout('switch-intent-000001',body)).status,409);assert.equal(x.rows().length,1);});
test('unknown expired creation remains recoverable but never resubmitted',async t=>{const x=setup(t);t.mock.method(globalThis,'fetch',async()=>{throw Error('mock timeout');});await x.checkout();x.db.exec("UPDATE orders SET expires_at='2000-01-01'");const first=x.rows()[0];const again=await(await x.checkout('unknown-retry-00001')).json();assert.equal(again.order.orderNo,first.order_no);assert.equal(again.order.createState,'unknown');assert.equal(x.rows().length,1);});
test('simultaneous distinct purchase intents send exactly one upstream request',async t=>{const x=setup(t);const results=await Promise.all(Array.from({length:4},(_,i)=>x.checkout('concurrent-key-0000'+i)));const bodies=await Promise.all(results.map(r=>r.json()));assert.equal(new Set(bodies.map(o=>o.orderNo||o.order?.orderNo)).size,1);assert.equal(x.calls(),1);assert.equal(x.rows().length,1);});
test('hidden pending order still prevents a fresh purchase',async t=>{const x=setup(t);await x.checkout();const o=x.rows()[0];assert.equal((await x.route('/v1/billing/orders/'+o.order_no+'/hide',{method:'POST'})).status,200);assert.equal((await(await x.checkout('after-hide-00000001')).json()).order.orderNo,o.order_no);assert.equal(x.calls(),1);});

test('M-W4 losing duplicate settlement records a duplicate_payment event',async t=>{
 const x=setup(t);await x.checkout();const o=x.rows()[0];
 const winner=await(await x.callback(o)).text();
 assert.equal(winner,'success');
 assert.equal(x.db.prepare("SELECT COUNT(*) n FROM payment_events WHERE event_type='duplicate_payment'").get().n,0);
 // The concurrent-callback race: the winner's settlement has committed (the trigger already
 // marked the order paid), while the loser still holds its pre-commit snapshot (status pending).
 // settlePayment must detect the conflict, audit it, and never double-grant.
 const stale={...o,status:'pending'};
 await settlePayment(x.e,stale,o.provider_trade_no,o.payable_cents);
 const rows=x.db.prepare("SELECT event_type,reason FROM payment_events WHERE event_type='duplicate_payment'").all();
 assert.equal(rows.length,1);
 const reason=JSON.parse(rows[0].reason);
 assert.equal(reason.code,'duplicate_payment');
 assert.equal(reason.trade_no,o.provider_trade_no);
 assert.equal(reason.amount_cents,o.payable_cents);
 // No double grant: subscription expires_at unchanged by the losing settlement.
 const before=x.db.prepare('SELECT expires_at FROM subscriptions').get();
 await settlePayment(x.e,stale,o.provider_trade_no,o.payable_cents);
 assert.equal(x.db.prepare('SELECT expires_at FROM subscriptions').get().expires_at,before.expires_at);
 assert.equal(x.db.prepare("SELECT COUNT(*) n FROM payment_events WHERE event_type='duplicate_payment'").get().n,2);
});


test('confirm on QR-window-lapsed pending order re-issues a fresh QR on the SAME order',async t=>{
 const x=setup(t);await x.checkout();const o=x.rows()[0];
 const originalQr=o.qr_code,originalTrade=o.provider_trade_no;
 x.db.exec("UPDATE orders SET expires_at='2000-01-01'");
 const before=(await(await x.route('/v1/billing/orders/'+o.order_no)).json());
 assert.equal(before.allowedActions.pay,false);assert.equal(before.qrCode,null);   // window lapsed
 const callsBefore=x.calls();
 const reply=await x.route('/v1/billing/orders/'+o.order_no+'/confirm',{method:'POST'});
 assert.equal(reply.status,200);
 const d=await reply.json();
 assert.equal(d.orderNo,o.order_no);                       // same order, never a second one
 assert.equal(x.rows().length,1);
 assert.equal(d.allowedActions.pay,true);                  // payable again
 assert.ok(d.qrCode&&d.qrCode!==originalQr);               // fresh QR content
 assert.ok(Date.parse(d.expiresAt)>Date.now());           // window pushed forward
 assert.equal(x.calls(),callsBefore+1);                   // one upstream create call
 assert.equal(x.db.prepare("SELECT COUNT(*) n FROM payment_events WHERE event_type='payment_qr_reissued'").get().n,1);
 // The reissued order still settles through the trusted callback.
 assert.equal(await(await x.callback(x.rows()[0])).text(),'success');
 assert.equal(x.rows()[0].status,'paid');
});

test('confirm does NOT re-issue while the QR window is still open (no duplicate create)',async t=>{
 const x=setup(t);await x.checkout();const o=x.rows()[0];const callsBefore=x.calls();
 const d=await(await x.route('/v1/billing/orders/'+o.order_no+'/confirm',{method:'POST'})).json();
 assert.equal(d.qrCode,o.qr_code);                 // original QR untouched
 assert.equal(d.expiresAt,o.expires_at);          // window not extended
 assert.equal(x.calls(),callsBefore);              // no provider call at all
});

test('confirm reissue is owner-scoped and never re-issues a paid order',async t=>{
 const x=setup(t);await x.checkout();const o=x.rows()[0];x.db.exec("UPDATE orders SET expires_at='2000-01-01'");
 assert.equal((await x.route('/v1/billing/orders/'+o.order_no+'/confirm',{method:'POST'},{user_id:'other'})).status,404);
 await x.callback(o);                               // pay it while lapsed
 const callsBefore=x.calls();
 const d=await(await x.route('/v1/billing/orders/'+o.order_no+'/confirm',{method:'POST'})).json();
 assert.equal(d.status,'paid');assert.equal(d.qrCode,null);
 assert.equal(x.calls(),callsBefore);              // no reissue for a paid order
});

test('amount-mismatch-blocked order never gets a reissued QR',async t=>{
 const x=setup(t);await x.checkout();const o=x.rows()[0];
 x.db.prepare('UPDATE orders SET qr_code=? WHERE order_no=?').run('https://code.ymyu.cn/url.php?price=0.22',o.order_no);
 x.db.exec("UPDATE orders SET expires_at='2000-01-01'");
 const callsBefore=x.calls();
 const d=await(await x.route('/v1/billing/orders/'+o.order_no+'/confirm',{method:'POST'})).json();
 assert.equal(d.errorCode,'provider_amount_mismatch');assert.equal(d.allowedActions.pay,false);assert.equal(d.qrCode,null);
 assert.equal(x.calls(),callsBefore);              // blocked: no provider call, no window push
});

test('dismiss is owner-only, paid orders are never deletable, and the order stays listed after payment',async t=>{
 const x=setup(t);await x.checkout();const o=x.rows()[0];const path='/v1/billing/orders/'+o.order_no+'/hide';
 assert.equal((await x.route(path,{method:'POST'},{user_id:'other'})).status,404);       // owner only
 await x.callback(o);assert.equal(x.rows()[0].status,'paid');
 assert.equal((await x.route(path,{method:'POST'})).status,409);                          // paid not deletable
 assert.equal(x.db.prepare('SELECT COUNT(*) n FROM payment_order_hidden').get().n,0);   // nothing hidden
 assert.equal((await(await x.route('/v1/billing/orders')).json()).orders.length,1);       // still listed
});

test('double payment stays blocked after dismiss: same pending order returned, callback still settles',async t=>{
 const x=setup(t);await x.checkout();const o=x.rows()[0];const callsBefore=x.calls();
 assert.equal((await x.route('/v1/billing/orders/'+o.order_no+'/hide',{method:'POST'})).status,200);
 assert.equal(x.db.prepare('SELECT COUNT(*) n FROM payment_order_hidden').get().n,1);
 // A fresh checkout must NOT create a second order while the dismissed one is still pending.
 const again=await(await x.checkout('after-dismiss-0001')).json();
 assert.equal(again.order.orderNo,o.order_no);assert.equal(x.rows().length,1);assert.equal(x.calls(),callsBefore);
 // The hidden pending order is out of the list but still visible by id, and still settleable.
 assert.equal((await(await x.route('/v1/billing/orders')).json()).orders.length,0);
 assert.equal((await(await x.route('/v1/billing/orders/'+o.order_no)).json()).hidden,true);
 assert.equal(await(await x.callback(x.rows()[0])).text(),'success');
 assert.equal(x.rows()[0].status,'paid');
 // Now paid: it reappears in the list (paid history is undeletable), and hide must 409.
 assert.equal((await(await x.route('/v1/billing/orders')).json()).orders.length,1);
 assert.equal((await x.route('/v1/billing/orders/'+o.order_no+'/hide',{method:'POST'})).status,409);
});

test('QR reissue failure degrades to the plain confirm reply; order untouched',async t=>{
 const x=setup(t);await x.checkout();const o=x.rows()[0];x.db.exec("UPDATE orders SET expires_at='2000-01-01'");
 t.mock.method(globalThis,'fetch',async()=>new Response('bad json'));
 const reply=await x.route('/v1/billing/orders/'+o.order_no+'/confirm',{method:'POST'});
 assert.equal(reply.status,200);
 const d=await reply.json();
 assert.equal(d.orderNo,o.order_no);assert.equal(d.status,'pending');assert.equal(d.allowedActions.pay,false);
 assert.equal(x.db.prepare("SELECT COUNT(*) n FROM payment_events WHERE event_type='payment_qr_reissue_failed'").get().n,1);
 // The lapsed order keeps its recovery semantics: a later trusted callback still settles.
 assert.equal(await(await x.callback(x.rows()[0])).text(),'success');
});
