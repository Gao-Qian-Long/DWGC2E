import {test} from 'node:test';
import assert from 'node:assert/strict';
import {DatabaseSync} from 'node:sqlite';
import {readFileSync,readdirSync} from 'node:fs';
import {setup} from './helpers/worker.mjs';
import worker from '../src/index.ts';
const body={source_lang:'ZH',target_lang:'en-US',items:[{id:0,text:'法兰'}]};
async function fixture(t,answer){
 const x=setup(t),auth=await x.login();let calls=0;
 t.mock.method(globalThis,'fetch',async()=>{calls++;return Response.json({choices:[{message:{content:JSON.stringify(await answer())}}]});});
 const send=(id='metadata-request-01')=>worker.fetch(new Request('https://test/v1/translate',{method:'POST',headers:{authorization:'Bearer '+auth.token,'content-type':'application/json','Idempotency-Key':id},body:JSON.stringify(body)}),x.env);
 const history=async()=> (await(await x.request('/v1/translation/history',{token:auth.token})).json()).items;
 return {...x,send,history,calls:()=>calls};
}
test('new request records exact language and start/end once; replay preserves metadata and billing',async t=>{
 const before=Date.now();const x=await fixture(t,()=>[{id:0,translated_text:'Flange'}]);
 await x.send();const [row]=await x.history();
 assert.equal(row.source_language,'ZH');assert.equal(row.target_language,'en-US');assert.equal(row.timestamp_kind,'recorded');
 assert.ok(Date.parse(row.started_at)>=before);assert.ok(Date.parse(row.completed_at)>=Date.parse(row.started_at));assert.ok(Date.parse(row.completed_at)<=Date.now());
 assert.equal(row.created_at,row.started_at);assert.equal(row.completion_kind,'recorded');
 const usage=x.db.prepare('SELECT * FROM usage_monthly').get();await x.send();assert.deepEqual((await x.history())[0],row);assert.deepEqual(x.db.prepare('SELECT * FROM usage_monthly').get(),usage);assert.equal(x.calls(),1);
});
test('in-flight history exposes recorded start without inventing completion',async t=>{
 let release,entered;const gate=new Promise(r=>release=r),ready=new Promise(r=>entered=r);
 const x=await fixture(t,async()=>{entered();await gate;return [{id:0,translated_text:'Flange'}];});
 const pending=x.send();await ready;
 try { const [row]=await x.history();assert.equal(row.status,'processing');assert.ok(row.started_at);assert.equal(row.completed_at,null);assert.equal(row.completion_kind,null); }
 finally {release();await pending;}
 assert.ok((await x.history())[0].completed_at);
});
test('provider failure records completion without text leakage or charge',async t=>{
 const x=await fixture(t,()=>null);assert.equal((await x.send()).status,502);const [row]=await x.history();assert.equal(row.status,'failed');assert.ok(row.completed_at);assert.equal(row.characters,0);assert.ok(!JSON.stringify(row).includes('法兰'));
});
test('legacy requests retain explicit estimate and null languages/end; logs do not fabricate starts',async t=>{
 const x=await fixture(t,()=>[]);x.db.exec("INSERT INTO usage_monthly VALUES('alice','2020-01',0,100000,0)");
 x.db.exec("INSERT INTO translation_requests(user_id,request_id,payload_hash,year_month,reserved,expires_at) VALUES('alice','legacy-request','h','2020-01',2,1577836980)");
 x.db.exec("INSERT INTO usage_logs(id,user_id,device_id,src_lang,tgt_lang,chars,created_at) VALUES('legacy-log','alice','d','zh','en',2,'2020-01-01T00:00:00Z')");
 const rows=await x.history(),row=rows.find(x=>x.id==='request_legacy-request'),log=rows.find(x=>x.id==='legacy-log');
 assert.equal(row.timestamp_kind,'request_start_estimate');assert.equal(row.created_at,'2020-01-01T00:00:00.000Z');assert.equal(row.started_at,null);assert.equal(row.completed_at,null);assert.equal(row.source_language,null);
 assert.equal(log.timestamp_kind,'recorded');assert.equal(log.started_at,null);assert.equal(log.completed_at,null);
});
test('expired request uses labelled deadline, not later cleanup timestamp; refund remains once',async t=>{
 const x=await fixture(t,()=>[{id:0,translated_text:'Flange'}]);
 x.db.exec("INSERT INTO usage_monthly VALUES('alice','2020-01',0,100000,0)");
 x.db.exec("INSERT INTO translation_requests(user_id,request_id,payload_hash,year_month,reserved,expires_at) VALUES('alice','expired-request','h','2020-01',5,1577836980)");
 await x.send();const row=(await x.history()).find(x=>x.id==='request_expired-request');
 assert.equal(row.completed_at,'2020-01-01T00:03:00.000Z');assert.equal(row.completion_kind,'expiry_deadline');assert.equal(row.characters,0);
 assert.equal(x.db.prepare("SELECT chars_used FROM usage_monthly WHERE year_month='2020-01'").get().chars_used,0);
 await x.send();assert.deepEqual((await x.history()).find(x=>x.id===row.id),row);
});
test('additive migration preserves old records, billing totals, response payloads and triggers',t=>{
 const db=new DatabaseSync(':memory:');t.after(()=>db.close());
 for(const file of readdirSync(new URL('../migrations/',import.meta.url)).filter(x=>x.endsWith('.sql')&&x<'0010').sort())db.exec(readFileSync(new URL('../migrations/'+file,import.meta.url),'utf8'));
 db.exec("INSERT INTO users(id,account,password_hash,created_at) VALUES('alice','alice','hash','now');INSERT INTO usage_monthly VALUES('alice','2020-01',0,100000,0);INSERT INTO translation_requests(user_id,request_id,payload_hash,year_month,reserved,expires_at) VALUES('alice','old-request','hash','2020-01',7,100);UPDATE translation_requests SET state='settled',billed=4,response_json='old response',response_status=200;");
 const before=db.prepare('SELECT * FROM translation_requests').get(),usage=db.prepare('SELECT * FROM usage_monthly').get(),triggers=db.prepare("SELECT name,sql FROM sqlite_master WHERE type='trigger' ORDER BY name").all();
 db.exec(readFileSync(new URL('../migrations/0010_translation_history_metadata.sql',import.meta.url),'utf8'));
 const after=db.prepare('SELECT * FROM translation_requests').get();for(const [key,value] of Object.entries(before))assert.equal(after[key],value);
 for(const key of ['source_language','target_language','started_at','completed_at'])assert.equal(after[key],null);
 assert.deepEqual(db.prepare('SELECT * FROM usage_monthly').get(),usage);assert.deepEqual(db.prepare("SELECT name,sql FROM sqlite_master WHERE type='trigger' ORDER BY name").all(),triggers);
});
