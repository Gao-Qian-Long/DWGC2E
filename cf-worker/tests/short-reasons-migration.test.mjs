import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {DatabaseSync} from 'node:sqlite';
test('0016 copies populated ledgers without replaying mutations or changing configured prices',()=>{
 const db=new DatabaseSync(':memory:');try{
 const schema=readFileSync(new URL('../schema.sql',import.meta.url),'utf8');db.exec(schema.slice(0,schema.indexOf('-- Additive short-reason ledgers.')));
 db.exec("INSERT INTO users(id,account,password_hash,created_at) VALUES('audit-user','audit-user','unchanged','2026-09-16'); INSERT INTO plan_changes SELECT 'pc','actor',id,revision,8,123456,30,1,'prior reason','{}','2026-09-16',name,description FROM plan_catalog WHERE id='pro'; INSERT INTO admin_membership_changes_v2 VALUES('mc','audit-user','actor','null','max','2099-01-01','prior reason','2026-09-16'); INSERT INTO quota_compensations VALUES('qc','audit-user','2026-09',123,'actor','prior reason','2026-09-16');");
 const tables=['users','subscriptions','subscription_quotas','usage_monthly','orders','plan_catalog','plans','operation_settings'];const before=Object.fromEntries(tables.map(n=>[n,db.prepare('SELECT * FROM '+n).all()]));
 db.exec(readFileSync(new URL('../migrations/0016_admin_short_reasons.sql',import.meta.url),'utf8'));
 for(const table of tables)assert.deepEqual(db.prepare('SELECT * FROM '+table).all(),before[table],table);
 for(const [old,next] of [['plan_changes','plan_changes_v3'],['admin_membership_changes_v2','admin_membership_changes_v3'],['quota_compensations','quota_compensations_v3']])assert.deepEqual(db.prepare('SELECT * FROM '+next).all(),db.prepare('SELECT * FROM '+old).all());
 db.exec("INSERT INTO plan_changes_v3 SELECT 'short','actor',id,revision,9,123456,30,1,'调','{}','2026-09-17',name,description FROM plan_catalog WHERE id='pro'; INSERT INTO quota_compensations_v3 VALUES('short','audit-user','2026-09',1,'actor','补','2026-09-17');");assert.equal(db.prepare("SELECT price_cents FROM plan_catalog WHERE id='pro'").get().price_cents,9);
 assert.throws(()=>db.exec("INSERT INTO quota_compensations_v3 VALUES('empty','audit-user','2026-09',1,'actor','','2026-09-17')"),/CHECK/);
 }finally{db.close();}
});
