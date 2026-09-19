import {test} from 'node:test';
import {fileURLToPath} from 'node:url';
import assert from 'node:assert/strict';
import {Miniflare,convertV4MiniflareOptions} from 'miniflare';
import {DatabaseSync} from 'node:sqlite';
import {readFileSync} from 'node:fs';
import {settlePayment} from '../src/payments/index.ts';
test('workerd D1: concurrent early callbacks, rollback and duplicate renewal',async t=>{
 const mf=new Miniflare(convertV4MiniflareOptions({workers:[{name:"payment-test",modules:true,script:'export default {fetch(){return new Response("test")}}',d1Databases:{DB:'payment-isolated'},compatibilityDate:'2026-09-13'}]}));t.after(()=>mf.dispose());
 const DB=await mf.getD1Database('DB','payment-test');const schema=new DatabaseSync(':memory:');schema.exec(readFileSync(new URL('../schema.sql',import.meta.url),'utf8'));
 const objects=schema.prepare("SELECT sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'index' THEN 1 ELSE 2 END").all();schema.close();
 for(const o of objects)await DB.prepare(o.sql).run();
 await DB.prepare("INSERT INTO users(id,account,password_hash,created_at) VALUES('d1-user','d1-test','unused','2026-09-15')").run();
 await DB.prepare("INSERT INTO plans VALUES('p','test',19,7,'pro',1)").run();
 // The settlement actor must be able to map this order's tier to a quota without a hardcoded
 // constant, exactly like a deployed database whose catalog is seeded.
 await DB.prepare("INSERT INTO plan_catalog(id,name,price_cents,quota,duration_days,enabled,revision,updated_at) VALUES('pro','Pro',3900,1000000,30,1,1,'2026-09-15T00:00:00.000Z')").run();
 const insert=async no=>DB.prepare("INSERT INTO orders(order_no,user_id,plan_id,plan_name,duration_days,membership_level,amount_cents,payable_cents,channel,idempotency_key,created_at,expires_at) VALUES(?,'d1-user','p','test',7,'pro',19,19,'alipay',?,'2026-09-15','2026-09-16')").bind(no,no).run();
 await insert('first');let o=await DB.prepare("SELECT * FROM orders WHERE order_no='first'").first();
 const results=await Promise.allSettled([settlePayment({DB},o,'TRADE-A',19),settlePayment({DB},o,'TRADE-B',19)]);assert.equal(results.filter(x=>x.status==='fulfilled').length,1);
 o=await DB.prepare("SELECT * FROM orders WHERE order_no='first'").first();assert.equal(o.status,'paid');assert.equal(o.create_state,'ready');
 const first=await DB.prepare("SELECT expires_at FROM subscriptions WHERE user_id='d1-user'").first();await Promise.all([settlePayment({DB},o,o.provider_trade_no,19),settlePayment({DB},o,o.provider_trade_no,19)]);assert.deepEqual(await DB.prepare("SELECT expires_at FROM subscriptions WHERE user_id='d1-user'").first(),first);
 await insert('second');await DB.prepare("CREATE TRIGGER injected BEFORE UPDATE OF status ON orders WHEN NEW.order_no='second' AND NEW.status='paid' BEGIN SELECT RAISE(ABORT,'injected');END").run();o=await DB.prepare("SELECT * FROM orders WHERE order_no='second'").first();await assert.rejects(settlePayment({DB},o,'TRADE-C',19));assert.equal((await DB.prepare("SELECT provider_trade_no FROM orders WHERE order_no='second'").first()).provider_trade_no,null);assert.deepEqual(await DB.prepare("SELECT expires_at FROM subscriptions WHERE user_id='d1-user'").first(),first);
 await DB.prepare('DROP TRIGGER injected').run();await settlePayment({DB},o,'TRADE-C',19);const second=await DB.prepare("SELECT expires_at FROM subscriptions WHERE user_id='d1-user'").first();assert.equal(Date.parse(second.expires_at)-Date.parse(first.expires_at),7*86400000);
});

// Exercise the deployed entrypoint inside workerd, not only the recovery function in Node.
test('workerd scheduled entrypoint drains due recovery without granting payment',async t=>{
 const {build}=await import('esbuild');
 const bundled=await build({entryPoints:[fileURLToPath(new URL('../src/index.ts',import.meta.url))],bundle:true,write:false,format:'esm',platform:'browser',target:'es2022'});
 const mf=new Miniflare(convertV4MiniflareOptions({workers:[{name:'scheduled-test',modules:true,script:bundled.outputFiles[0].text,d1Databases:{DB:'scheduled-isolated'},compatibilityDate:'2026-09-13'}]}));t.after(()=>mf.dispose());
 const DB=await mf.getD1Database('DB','scheduled-test');const schema=new DatabaseSync(':memory:');schema.exec(readFileSync(new URL('../schema.sql',import.meta.url),'utf8'));
 const objects=schema.prepare("SELECT sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'index' THEN 1 ELSE 2 END").all();schema.close();for(const o of objects)await DB.prepare(o.sql).run();
 await DB.prepare("INSERT INTO users(id,account,password_hash,created_at) VALUES('cron-user','cron-test','unused','2026-09-15')").run();
 await DB.prepare("INSERT INTO plans VALUES('p','test',19,7,'pro',1)").run();
 await DB.prepare("INSERT INTO orders(order_no,user_id,plan_id,plan_name,duration_days,membership_level,amount_cents,payable_cents,channel,idempotency_key,created_at,expires_at) VALUES('cron-order','cron-user','p','test',7,'pro',19,19,'alipay','cron-key','2000-01-01','2000-01-02')").run();
 await DB.prepare("UPDATE payment_recovery SET next_attempt_at='2000-01-01' WHERE order_no='cron-order'").run();
 const worker=await mf.getWorker('scheduled-test');
 const first=await worker.scheduled({cron:'* * * * *',scheduledTime:Date.now()});assert.equal(first.outcome,'ok');
 const row=await DB.prepare("SELECT state,attempts,lease_until FROM payment_recovery").first();assert.deepEqual(row,{state:'manual',attempts:1,lease_until:null});
 await worker.scheduled({cron:'* * * * *',scheduledTime:Date.now()});assert.equal((await DB.prepare('SELECT attempts FROM payment_recovery').first()).attempts,1);
 assert.equal((await DB.prepare('SELECT status FROM orders').first()).status,'pending');assert.equal((await DB.prepare('SELECT COUNT(*) n FROM payment_settlements').first()).n,0);
});

test('workerd D1: different checkout keys atomically reuse one pending order',async t=>{
 const {billingRoute}=await import('../src/payments/index.ts');
 const mf=new Miniflare(convertV4MiniflareOptions({workers:[{name:'checkout-race',modules:true,script:'export default {fetch(){return new Response("test")}}',d1Databases:{DB:'checkout-race'},compatibilityDate:'2026-09-13'}]}));t.after(()=>mf.dispose());
 const DB=await mf.getD1Database('DB','checkout-race');const schema=new DatabaseSync(':memory:');schema.exec(readFileSync(new URL('../schema.sql',import.meta.url),'utf8'));
 const objects=schema.prepare("SELECT sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'index' THEN 1 ELSE 2 END").all();schema.close();for(const o of objects)await DB.prepare(o.sql).run();
 await DB.prepare("INSERT INTO users(id,account,password_hash,created_at) VALUES('race','race','unused','2026-09-17')").run();await DB.prepare("INSERT INTO plans VALUES('p','test',900,7,'pro',1)").run();
 let calls=0;const original=globalThis.fetch;t.mock.method(globalThis,'fetch',async(url,init)=>{if(String(url)!=='https://provider.example/mapi.php')return original(url,init);calls++;const p=new URLSearchParams(init.body);assert.equal(p.get('money'),'9.00');return Response.json({code:1,trade_no:'mockTrade',qrcode:'https://code.ymyu.cn/url.php?price=9.00'});});
 const env={DB,EZFPY_API_BASE_URL:'https://provider.example/',EZFPY_PID:'test',EZFPY_KEY:'test',PUBLIC_API_URL:'https://api.example/',PUBLIC_WEB_URL:'https://web.example/',PAYMENTS_ENABLED:'true'};
 const create=i=>billingRoute(new Request('https://api.example/v1/billing/checkout',{method:'POST',headers:{'Idempotency-Key':'race-checkout-key-'+i},body:JSON.stringify({planId:'p',channel:'alipay'})}),env,{user_id:'race'},'https://web.example');
 const responses=await Promise.all([create(1),create(2),create(3)]);assert.ok(responses.every(r=>[200,409].includes(r.status)));const orders=await Promise.all(responses.map(r=>r.json()));assert.equal(new Set(orders.map(o=>o.orderNo||o.order?.orderNo)).size,1);assert.equal(calls,1);assert.equal((await DB.prepare('SELECT count(*) n FROM orders').first()).n,1);
});
