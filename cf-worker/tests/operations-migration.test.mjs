import {test} from 'node:test';import assert from 'node:assert/strict';import {readFileSync,readdirSync} from 'node:fs';import {DatabaseSync} from 'node:sqlite';
test('0011–0015 preserve populated pre-upgrade accounts, orders, usage and bindings',()=>{
 const db=new DatabaseSync(':memory:');try{const schema=readFileSync(new URL('../schema.sql',import.meta.url),'utf8');db.exec(schema.slice(0,schema.indexOf('-- Four-tier catalog;')));
 db.exec("INSERT INTO users(id,account,password_hash,created_at) VALUES('existing','existing','keep-hash','2026-01-01'); INSERT INTO subscriptions VALUES('existing','pro','2026-01-01','2099-01-01',0,'2026-01-01'); INSERT INTO usage_monthly VALUES('existing','2026-09',123,1000000,2); INSERT INTO orders(order_no,user_id,plan_id,plan_name,duration_days,membership_level,amount_cents,payable_cents,channel,idempotency_key,created_at,expires_at) SELECT 'old-order','existing',id,name,duration_days,membership_level,price_cents,price_cents,'alipay','existing-key','2026-09-01','2026-09-30' FROM plans LIMIT 1;");
 const tables=['users','subscriptions','usage_monthly','orders','payment_settlements','app_device_bindings','sessions','admin_membership_changes'];const snapshots=Object.fromEntries(tables.map(n=>[n,db.prepare('SELECT * FROM '+n).all()]));
 for(const file of readdirSync(new URL('../migrations/',import.meta.url)).filter(n=>/^001[1-5]_/.test(n)).sort())db.exec(readFileSync(new URL('../migrations/'+file,import.meta.url),'utf8'));
 for(const n of tables)assert.deepEqual(db.prepare('SELECT * FROM '+n).all(),snapshots[n],n);
 assert.equal(db.prepare('SELECT COUNT(*) n FROM orders').get().n,1);assert.equal(db.prepare('SELECT COUNT(*) n FROM plan_catalog').get().n,4);assert.equal(db.prepare("SELECT quota FROM subscription_quotas WHERE user_id='existing'").get().quota,1000000);
 }finally{db.close();}
});
